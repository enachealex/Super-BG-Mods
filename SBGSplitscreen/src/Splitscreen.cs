using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SBGSplitscreen
{
    /// <summary>
    /// Drop-in local players. While player 1 hosts a game, any other controller can press A to
    /// join; the new player gets a local connection, a player object, a camera, and their own
    /// context of "local player" state.
    /// </summary>
    internal static class Splitscreen
    {
        private sealed class JoinedPlayer
        {
            public int Slot;
            public Gamepad Pad;
            public LocalPlayerConnection Connection;
            public float NextSpawnAttempt;
        }

        private static readonly JoinedPlayer[] players = new JoinedPlayer[Contexts.MaxPlayers];
        private static bool typesPatched;
        private static bool leaving;

        public static int JoinedCount => players.Count(p => p != null);

        public static void Init(Harmony harmony)
        {
            InputContexts.RegisterFields();
            PlayerViews.RegisterFields();
            RegisterGameplayFields();
            LocalPlayerFlags.Register();
            ScreenEffects.Init(harmony);
            ContextPatcher.Init(harmony);
            PerViewerUi.Init(harmony);
            HomingWarnings.Init(harmony);
            EventBroadcast.Init(harmony);
            ViewportFixes.Init(harmony);
            ContextPatcher.PatchLifecycle(PerPlayerEventSubscribers());
            NetworkIdentity.clientAuthorityCallback += OnClientAuthorityChanged;
        }

        private static readonly Action<NetworkIdentity> notifyAuthority =
            AccessTools.MethodDelegate<Action<NetworkIdentity>>(AccessTools.Method(typeof(NetworkIdentity), "NotifyAuthority"));

        /// <summary>
        /// The server hands control of an object (a golf cart's driver seat, for one) to a joined
        /// player's connection. Mirror only marks objects as owned in the host's own game when the
        /// host's connection gets them; mark them for joined players too, or the host would treat
        /// the object as someone else's and stop simulating it.
        /// </summary>
        private static void OnClientAuthorityChanged(NetworkConnectionToClient conn, NetworkIdentity identity, bool authorityState)
        {
            if (!(conn is LocalPlayerConnection local) || !NetworkClient.active || identity == null)
            {
                return;
            }
            IdentityFlags.SetIsOwned(identity, authorityState);
            int previous = Contexts.Enter(local.Slot);
            try
            {
                notifyAuthority(identity);
            }
            finally
            {
                Contexts.Restore(previous);
            }
        }

        /// <summary>
        /// Every game class that subscribes to a per-player event (found by scanning the game's
        /// code: InputManager/PlayerInput Switched*, LocalPlayer*, camera module events...).
        /// </summary>
        private static IEnumerable<Type> PerPlayerEventSubscribers()
        {
            string[] names =
            {
                "BLevelOfDetailManager", "ButtonPrompt", "ButtonPromptExplicit", "CheckpointManager", "ControllerInputReselect",
                "ControllerSelectable", "GameplayCameraManager", "GolfBall", "GolfCartInfo", "GolfCartMovement", "GolfHole",
                "Hittable", "HoleProgressBarUi", "Hotkeys", "LocalSpectatorCameraFollower", "LockOnTargetUiManager",
                "MatchSetupHole", "MatchSetupMenu", "MatchSetupPlayerEntry", "MenuOpenInteraction", "NameTagUi",
                "PauseMenuPlayerEntry", "PlayerCosmeticsSwitcher", "PlayerCustomizationMenu", "PlayerGolfer", "PlayerId",
                "PlayerInfo", "PlayerInput", "PlayerMovement", "PlayerShaderDataManager", "PlayerSpectator", "PlayerVfx",
                "RestartPrompt", "ReticleManager", "Rocket", "ScrollRectControllerInput", "SettingsMenu",
                "SteamAchievementManager", "SwingPitchUi", "TextChatUi", "TrafficVehicle", "TutorialPromptUi", "VoteKickUi",
                "MenuNavigation"
            };
            var assembly = typeof(GameManager).Assembly;
            return names.Select(n => assembly.GetType(n)).Where(t => t != null);
        }

        /// <summary>The game's notion of "the local player", all of which each joined player needs a copy of.</summary>
        private static void RegisterGameplayFields()
        {
            foreach (string field in new[] { "localPlayerInfo", "localPlayerId", "localPlayerMovement", "localPlayerAsGolfer",
                "localPlayerInventory", "localPlayerInteractableTargeter", "localPlayerAsSpectator" })
            {
                Contexts.AddGameManagerField(field);
            }
            Contexts.AddGameManagerField("remotePlayers", () => new List<PlayerInfo>());
            Contexts.AddGameManagerField("remotePlayerIndices", () => new Dictionary<PlayerInfo, int>());
            Contexts.AddGameManagerField("remotePlayerPerconnectionId", () => new Dictionary<int, PlayerInfo>());

            foreach (string evt in new[] { "LocalPlayerRegistered", "LocalPlayerDeregistered", "RemotePlayerRegistered", "RemotePlayerDeregistered" })
            {
                Contexts.AddStaticEvent(typeof(GameManager), evt);
            }
            // HUD hiding (pause menu, scoreboard) belongs to the player who opened them.
            Contexts.AddStaticProperty(typeof(GameManager), "HiddenUiGroups");
            Contexts.AddStaticEvent(typeof(GameManager), "UiHidingModeChanged");

            // Every "LocalPlayer..." event the game declares (golf cart, spectating, swing, team...).
            foreach (var type in typeof(GameManager).Assembly.GetTypes())
            {
                foreach (var evt in type.GetEvents(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (evt.Name.StartsWith("LocalPlayer") && type != typeof(GameManager))
                    {
                        Contexts.AddStaticEvent(type, evt.Name);
                    }
                }
            }
        }

        public static bool IsClaimed(Gamepad pad) => players.Any(p => p != null && p.Pad == pad);

        /// <summary>A joined player whose controller is currently unplugged or switched off.</summary>
        public static bool IsWaitingForController(int slot) => players[slot] != null && !players[slot].Pad.added;

        public static bool CanJoin()
        {
            return Settings.Enabled
                && NetworkServer.active && NetworkServer.activeHost && NetworkClient.active
                && GameManager.LocalPlayerInfo != null
                && !BNetworkManager.IsChangingSceneOrShuttingDown
                && !LoadingScreen.IsVisible
                && 1 + JoinedCount < Settings.MaxPlayers;
        }

        /// <summary>Gamepads that could press A to join right now.</summary>
        public static IEnumerable<Gamepad> FreePads()
        {
            return InputContexts.OrderedPads().Where(p => p != InputContexts.PrimaryPad && !IsClaimed(p));
        }

        private static readonly bool[] leaveRequested = new bool[Contexts.MaxPlayers];

        /// <summary>
        /// "Leave Game" in a joined player's pause menu. Handled on the next frame, from the top of
        /// the update loop, because the button press arrives in the middle of that player's own UI code.
        /// </summary>
        public static void RequestLeave(int slot)
        {
            if (slot > 0 && slot < Contexts.MaxPlayers)
            {
                leaveRequested[slot] = true;
            }
        }

        /// <summary>Players who left this frame: their context closes once Unity has finished destroying their things.</summary>
        private static readonly bool[] departing = new bool[Contexts.MaxPlayers];

        public static bool IsDeparting(int slot) => departing[slot];

        public static void Update()
        {
            for (int s = 1; s < Contexts.MaxPlayers; s++)
            {
                if (departing[s])
                {
                    departing[s] = false;
                    InputContexts.TearDownContext(s);
                    Contexts.Deactivate(s);
                }
            }
            for (int s = 1; s < Contexts.MaxPlayers; s++)
            {
                if (leaveRequested[s])
                {
                    leaveRequested[s] = false;
                    Leave(s);
                }
            }

            InputContexts.UpdatePrimaryDevices(IsClaimed);

            if (GameManager.LocalPlayerInfo != null && !typesPatched)
            {
                PatchPerPlayerTypes();
            }

            foreach (var pad in FreePads())
            {
                if (!pad.buttonSouth.wasPressedThisFrame)
                {
                    continue;
                }
                // A player whose controller dropped out gets the next controller that presses A.
                var waiting = players.FirstOrDefault(p => p != null && !p.Pad.added);
                if (waiting != null)
                {
                    Plugin.Log.LogInfo($"{pad.displayName} (#{pad.deviceId}) takes over for player {waiting.Slot + 1}.");
                    waiting.Pad = pad;
                    InputContexts.SetPad(waiting.Slot, pad);
                }
                else if (CanJoin())
                {
                    Join(pad);
                }
                break;
            }

            if (NetworkServer.active)
            {
                PlayerHuds.PrepareTypes();
            }

            foreach (var p in players)
            {
                if (p != null)
                {
                    PlayerViews.EnsureRig(p.Slot);
                    PlayerHuds.EnsureHud(p.Slot);
                    EnsureSpawned(p);
                    HoleIntro.CatchUp(p.Slot);
                }
            }
            var order = PlayerViews.ActiveOrder();
            PlayerViews.ApplyViewports(order);
            PlayerHuds.ApplyLayout(order);
            HomingWarnings.Update();
            VehicleCollisions.Update();
            CheckpointViews.Update();
        }

        /// <summary>Camera rig components, known up front because the main menu has no gameplay camera to look at.</summary>
        private static readonly Type[] CameraTypes =
        {
            typeof(CameraModuleController), typeof(OrbitCameraModule), typeof(FirstPersonCameraModule),
            typeof(OverviewCameraModule), typeof(PhotoCameraModule), typeof(GameplayCameraManager), typeof(LevelBoundsTracker),
            typeof(ImpactFrameController), typeof(CrtScreenController), typeof(StealthOverlayController), typeof(FlashFrameController),
            // Not camera parts: the golf cart runs as whoever drives it (see Owners), so its many
            // "is the local player driving" checks see the right player.
            typeof(GolfCartInfo), typeof(GolfCartMovement)
        };

        /// <summary>
        /// Every component on the player character and the camera rig runs per player. Patching
        /// takes a few seconds, so it happens once, behind the loading screen of the first hosted game.
        /// </summary>
        public static void PatchPerPlayerTypes()
        {
            if (typesPatched)
            {
                return;
            }
            var prefab = BNetworkManager.singleton != null ? BNetworkManager.singleton.playerPrefab : null;
            if (prefab == null)
            {
                return;
            }
            typesPatched = true;
            var gameAssemblies = new HashSet<Assembly> { typeof(GameManager).Assembly, typeof(SingletonBehaviour<>).Assembly };
            var types = prefab.GetComponentsInChildren<MonoBehaviour>(true).Select(c => c.GetType())
                .Concat(CameraTypes)
                .Concat(PlayerViews.RigComponentTypes())
                .Where(t => gameAssemblies.Contains(t.Assembly))
                .Distinct()
                .ToList();
            ContextPatcher.PatchTypes(types);
        }

        public static bool Join(Gamepad pad)
        {
            int slot = Enumerable.Range(1, Contexts.MaxPlayers - 1).FirstOrDefault(s => players[s] == null && !Contexts.IsActive(s));
            if (slot == 0)
            {
                return false;
            }
            if (!typesPatched)
            {
                PatchPerPlayerTypes();
            }
            Plugin.Log.LogInfo($"{pad.displayName} (#{pad.deviceId}) joins as player {slot + 1}.");

            Contexts.Activate(slot);
            var joined = new JoinedPlayer { Slot = slot, Pad = pad, Connection = new LocalPlayerConnection(slot) };
            players[slot] = joined;
            try
            {
                InputContexts.SetupContext(slot, pad);
                var others = new List<PlayerInfo> { GameManager.LocalPlayerInfo };
                others.AddRange(GameManager.RemotePlayers);
                PlayerRegistry.ReplayInto(slot, others);
                PlayerViews.EnsureRig(slot);
                PlayerHuds.EnsureHud(slot);
                Connect(joined.Connection);
                EnsureSpawned(joined);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Player {slot + 1} could not join: {e}");
                Leave(slot);
                return false;
            }
        }

        private static void Connect(LocalPlayerConnection conn)
        {
            var manager = BNetworkManager.singleton;
            NetworkServer.AddConnection(conn);
            manager.OnServerConnect(conn);
        }

        /// <summary>
        /// Spawns the player object when it has none: right after joining, and again after every
        /// scene change (each hole is a new scene, and a real client would re-request its player).
        /// </summary>
        private static void EnsureSpawned(JoinedPlayer p)
        {
            var conn = p.Connection;
            if (conn.identity != null || !NetworkServer.active || BNetworkManager.IsChangingSceneOrShuttingDown
                || GameManager.LocalPlayerInfo == null || Time.unscaledTime < p.NextSpawnAttempt)
            {
                return;
            }
            p.NextSpawnAttempt = Time.unscaledTime + 5f;
            var manager = BNetworkManager.singleton;
            if (!conn.isReady)
            {
                NetworkServer.SetClientReady(conn);
            }
            // The owner marker rides along on the prefab for this one Instantiate, so the new
            // player's components already know whose they are when Awake runs.
            var marker = manager.playerPrefab.AddComponent<SplitscreenOwner>();
            marker.Slot = p.Slot;
            try
            {
                manager.OnServerAddPlayer(conn);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(marker, true);
            }
            Plugin.Log.LogInfo($"Spawned player {p.Slot + 1}: {(conn.identity != null ? conn.identity.name : "pending")}");
        }

        public static void Leave(int slot)
        {
            var p = players[slot];
            if (p == null || leaving)
            {
                return;
            }
            leaving = true;
            try
            {
                Plugin.Log.LogInfo($"Player {slot + 1} leaves.");
                var conn = p.Connection;
                if (NetworkServer.active && NetworkServer.connections.ContainsKey(conn.connectionId))
                {
                    conn.Cleanup();
                    NetworkServer.RemoveConnection(conn.connectionId);
                    if (NetworkServer.OnDisconnectedEvent != null)
                    {
                        NetworkServer.OnDisconnectedEvent(conn);
                    }
                    else
                    {
                        NetworkServer.DestroyPlayerForConnection(conn);
                    }
                }
                // Right away and inside this player's context: their pause menu unpauses on its way
                // out, and done later, from player 1's context, that would switch off player 1's own
                // open pause menu and leave it stuck on screen.
                PlayerViews.DestroyRig(slot);
                PlayerHuds.DestroyHud(slot);
            }
            finally
            {
                players[slot] = null;
                // The character itself goes at the end of this frame (Unity destroys objects then);
                // its clean-up must still run as this player, so the context closes next frame.
                departing[slot] = true;
                leaving = false;
            }
        }

        /// <summary>The game kicked or disconnected a joined player (vote kick, anti-cheat, server stop).</summary>
        public static void OnConnectionDisconnected(LocalPlayerConnection conn)
        {
            Leave(conn.Slot);
        }

        /// <summary>When player 1 stops hosting, everyone else leaves too.</summary>
        public static void OnServerStopped()
        {
            for (int s = 1; s < Contexts.MaxPlayers; s++)
            {
                if (players[s] != null)
                {
                    Leave(s);
                }
            }
        }

        public static string Describe()
        {
            var parts = new List<string> { $"P1 pad={(InputContexts.PrimaryPad != null ? InputContexts.PrimaryPad.displayName : "none")}" };
            foreach (var p in players.Where(x => x != null))
            {
                var id = p.Connection.identity;
                parts.Add($"P{p.Slot + 1} pad={p.Pad.displayName}#{p.Pad.deviceId} conn={p.Connection.connectionId} ready={p.Connection.isReady} player={(id != null ? id.name : "none")} local={(id != null && id.isLocalPlayer)} owned={(id != null && id.isOwned)} camera={PlayerViews.HasRig(p.Slot)}");
            }
            return string.Join(" | ", parts);
        }
    }

    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.StopHost))]
    internal static class StopHostPatch
    {
        private static void Prefix() => Splitscreen.OnServerStopped();
    }

    /// <summary>The loading screen is already up when hosting starts, which hides the one-time patching pause.</summary>
    [HarmonyPatch(typeof(NetworkManager), nameof(NetworkManager.StartHost))]
    internal static class StartHostPatch
    {
        private static void Prefix() => Splitscreen.PatchPerPlayerTypes();
    }
}
