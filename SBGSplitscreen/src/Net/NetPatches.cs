using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Mirror;
using Mirror.RemoteCalls;

namespace SBGSplitscreen
{
    internal static class IdentityFlags
    {
        private static readonly Action<object, object> setIsLocalPlayer =
            FastAccess.InstanceSetter(AccessTools.Field(typeof(NetworkIdentity), "<isLocalPlayer>k__BackingField"));
        private static readonly Action<object, object> setIsOwned =
            FastAccess.InstanceSetter(AccessTools.Field(typeof(NetworkIdentity), "<isOwned>k__BackingField"));
        private static readonly Action<object, object> setConnectionToServer =
            FastAccess.InstanceSetter(AccessTools.Field(typeof(NetworkIdentity), "<connectionToServer>k__BackingField"));

        public static void SetIsLocalPlayer(NetworkIdentity identity, bool value) => setIsLocalPlayer(identity, value);
        public static void SetIsOwned(NetworkIdentity identity, bool value) => setIsOwned(identity, value);
        public static void SetConnectionToServer(NetworkIdentity identity, NetworkConnection value) => setConnectionToServer(identity, value);

    }

    /// <summary>
    /// When the host's client starts a joined player's object, mark it as a local, owned player
    /// and run its start-up callbacks (OnStartClient, OnStartAuthority, OnStartLocalPlayer) in that
    /// player's context, so it registers as that context's local player and binds that player's controls.
    /// </summary>
    [HarmonyPatch(typeof(NetworkClient), "InvokeIdentityCallbacks")]
    internal static class LocalPlayerStartPatch
    {
        private static void Prefix(NetworkIdentity identity, out int __state)
        {
            __state = -1;
            if (!(identity.connectionToClient is LocalPlayerConnection conn))
            {
                return;
            }
            // Owned on the host, so client-authoritative movement and rigidbodies simulate here.
            IdentityFlags.SetIsOwned(identity, true);
            if (conn.identity == identity)
            {
                LocalPlayerFlags.Assign(conn.Slot, identity);
                IdentityFlags.SetConnectionToServer(identity, NetworkClient.connection);
            }
            __state = Contexts.Enter(conn.Slot);
        }

        private static void Finalizer(int __state) => Contexts.Restore(__state);
    }

    /// <summary>
    /// When a joined player's character is destroyed (hole change, leaving), Mirror only runs its
    /// "stop being the local player" callbacks if the character counts as local right then, which
    /// it does not from player 1's point of view. Those callbacks unhook the controller from the
    /// character, so without them the old character's handlers stayed on the player's controls and
    /// fired into a destroyed object on the next hole. Run them in the player's own context first.
    /// </summary>
    [HarmonyPatch(typeof(NetworkIdentity), "OnStopClient")]
    internal static class LocalPlayerStopPatch
    {
        private static readonly System.Action<NetworkIdentity> onStopLocalPlayer =
            AccessTools.MethodDelegate<System.Action<NetworkIdentity>>(AccessTools.Method(typeof(NetworkIdentity), "OnStopLocalPlayer"));

        private static void Prefix(NetworkIdentity __instance)
        {
            if (__instance.isLocalPlayer || !(__instance.connectionToClient is LocalPlayerConnection conn)
                || !(Contexts.GetValue(conn.Slot, LocalPlayerFlags.Field) is NetworkIdentity local) || local != __instance)
            {
                return;
            }
            int previous = Contexts.Enter(conn.Slot);
            try
            {
                onStopLocalPlayer(__instance);
            }
            finally
            {
                Contexts.Restore(previous);
            }
        }
    }

    /// <summary>
    /// Every player registers once per context: as the local player in its own context and as a
    /// remote player in every other one (isLocalPlayer follows the live context, see
    /// LocalPlayerFlags). Player 1's HUD then shows player 2 as another golfer and vice versa.
    /// </summary>
    internal static class PlayerRegistry
    {
        private static readonly MethodInfo registerInternal = AccessTools.Method(typeof(GameManager), "RegisterPlayerInternal");
        private static readonly MethodInfo deregisterInternal = AccessTools.Method(typeof(GameManager), "DeregisterPlayerInternal");

        internal static bool Running;

        public static void RunInAllContexts(PlayerInfo player, bool register)
        {
            if (!SingletonBehaviour<GameManager>.HasInstance)
            {
                return;
            }
            int previous = Contexts.Current;
            Running = true;
            try
            {
                for (int c = 0; c < Contexts.MaxPlayers; c++)
                {
                    if (Contexts.IsActive(c))
                    {
                        Contexts.Switch(c);
                        Invoke(register ? registerInternal : deregisterInternal, player);
                    }
                }
            }
            finally
            {
                Contexts.Switch(previous);
                Running = false;
            }
        }

        /// <summary>A context that just started learns about everyone already in the game, as remote players.</summary>
        public static void ReplayInto(int context, IEnumerable<PlayerInfo> players)
        {
            int previous = Contexts.Switch(context);
            Running = true;
            try
            {
                foreach (var player in players)
                {
                    if (player != null)
                    {
                        Invoke(registerInternal, player);
                    }
                }
            }
            finally
            {
                Contexts.Switch(previous);
                Running = false;
            }
        }

        private static void Invoke(MethodInfo method, PlayerInfo player)
        {
            try
            {
                method.Invoke(SingletonBehaviour<GameManager>.Instance, new object[] { player });
            }
            catch (TargetInvocationException e)
            {
                Plugin.Log.LogError($"{method.Name}({player.name}) in context {Contexts.Current}: {e.InnerException}");
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), "RegisterPlayerInternal")]
    internal static class RegisterPlayerPatch
    {
        private static bool Prefix(PlayerInfo playerInfo)
        {
            if (PlayerRegistry.Running || Contexts.ExtraCount == 0)
            {
                return true;
            }
            PlayerRegistry.RunInAllContexts(playerInfo, register: true);
            return false;
        }
    }

    [HarmonyPatch(typeof(GameManager), "DeregisterPlayerInternal")]
    internal static class DeregisterPlayerPatch
    {
        private static bool Prefix(PlayerInfo playerInfo)
        {
            if (PlayerRegistry.Running || Contexts.ExtraCount == 0)
            {
                return true;
            }
            PlayerRegistry.RunInAllContexts(playerInfo, register: false);
            return false;
        }
    }

    /// <summary>The physics manager tracks one local player; that stays player 1.</summary>
    [HarmonyPatch]
    internal static class PhysicsRegistrationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PhysicsManager), nameof(PhysicsManager.RegisterLocalPlayer));
            yield return AccessTools.Method(typeof(PhysicsManager), nameof(PhysicsManager.DeregisterLocalPlayer));
        }

        private static bool Prefix() => Contexts.Current == 0;
    }

    /// <summary>
    /// The physics manager's "remote player" list exists for one rule: a giant (jumbo burger)
    /// ignores contacts with remote players, because online, each player detects being run over on
    /// their own machine. Joined players are simulated right here, so they must not be on that
    /// list, or player 1's giant would pass straight through them and they would never be run
    /// over. Real online players are still registered as usual (from player 1's context only).
    /// </summary>
    [HarmonyPatch]
    internal static class PhysicsRemoteRegistrationPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PhysicsManager), nameof(PhysicsManager.RegisterRemotePlayer));
            yield return AccessTools.Method(typeof(PhysicsManager), nameof(PhysicsManager.DeregisterRemotePlayer));
        }

        private static bool Prefix(PlayerInfo player)
        {
            return Contexts.Current == 0 && !(player.netIdentity.connectionToClient is LocalPlayerConnection);
        }
    }

    [HarmonyPatch(typeof(BNetworkManager), "ServerGetPlayerGuid")]
    internal static class LocalPlayerGuidPatch
    {
        private static bool Prefix(NetworkConnectionToClient connection, ref ulong __result)
        {
            if (connection is LocalPlayerConnection conn)
            {
                __result = conn.Guid;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(CourseManager), nameof(CourseManager.GetPlayerName), typeof(ulong))]
    internal static class LocalPlayerNamePatch
    {
        private static bool Prefix(ulong guid, ref string __result)
        {
            int slot = LocalPlayerConnection.SlotOfGuid(guid);
            if (slot < 0)
            {
                return true;
            }
            __result = Settings.PlayerName(slot);
            return false;
        }
    }

    /// <summary>
    /// Targeted RPCs for a joined player (spawn placement, respawn, out-of-bounds timers...) would
    /// go to a connection with no client behind it. Run them right here instead, in that player's context.
    ///
    /// Most targeted RPCs are something else, though: relays. The host applies a hit, a sound or an
    /// effect itself, then sends a copy to every other connection so remote players see it too.
    /// Those are dropped for local players: the shared game already did it, and running the copy
    /// again doubled every hit's force (tripled with three players).
    /// </summary>
    [HarmonyPatch(typeof(NetworkBehaviour), "SendTargetRPCInternal")]
    internal static class TargetRpcForwardPatch
    {
        /// <summary>
        /// RPCs the game sends to one specific player's connection (found by checking every call
        /// site in the game's code; all other RPCs sent with an explicit connection are relays).
        /// RPCs sent without a connection always go to the object's owner and are always forwarded.
        /// </summary>
        private static readonly HashSet<string> DirectMessages = new HashSet<string>
        {
            "CourseManager::RpcAwardCourseBonus",
            "CourseManager::RpcInformAutomaticallyMovedTeams",
            "CourseManager::RpcPlayAnnouncerLines",
            "GolfCartInfo::RpcInformPassengerSeatChangeFailed",
            "GolfCartInfo::RpcLocalPlayerSetSeat",
            "MatchSetupMenu::RpcInformTeamChangeRequestFailed",
            "PlayerInfo::RpcForceExitGolfCart",
            "PlayerMovement::RpcReturnToBounds",
            "VoteKickManager::RpcInformAlreadyOngoing",
            "VoteKickManager::RpcInformCooldown"
        };

        private static readonly HashSet<string> loggedRelays = new HashSet<string>();

        /// <summary>"System.Void Type::Method(args)" to "Type::Method".</summary>
        private static string ShortName(string functionFullName)
        {
            int start = functionFullName.IndexOf(' ') + 1;
            int end = functionFullName.IndexOf('(');
            return end > start ? functionFullName.Substring(start, end - start) : functionFullName;
        }

        private static bool Prefix(NetworkBehaviour __instance, NetworkConnection conn, string functionFullName, int functionHashCode, NetworkWriter writer)
        {
            var target = conn ?? __instance.connectionToClient;
            if (!(target is LocalPlayerConnection local) || !NetworkServer.active || !__instance.isServer)
            {
                return true;
            }
            if (conn != null && !DirectMessages.Contains(ShortName(functionFullName)))
            {
                if (Settings.DebugMode && loggedRelays.Add(ShortName(functionFullName)))
                {
                    Plugin.Log.LogInfo($"Dropped relay for a local player: {ShortName(functionFullName)}");
                }
                return false;
            }
            var call = RemoteProcedureCalls.GetDelegate((ushort)functionHashCode);
            if (call == null)
            {
                Plugin.Log.LogWarning($"No handler for targeted RPC {functionFullName}");
                return false;
            }
            // The whole message is about that one player ("you got a seat in this cart"), so it
            // stays in their context even when it runs on an object someone else owns: without the
            // pin, a seat meant for player 3 in an empty cart (player 1's by default) went to player 1.
            int previous = Contexts.Enter(local.Slot);
            bool wasPinned = Contexts.Pinned;
            Contexts.Pinned = true;
            try
            {
                using (var reader = NetworkReaderPool.Get(writer.ToArraySegment()))
                {
                    call(__instance, reader, null);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Forwarded RPC {functionFullName} failed: {e}");
            }
            finally
            {
                Contexts.Pinned = wasPinned;
                Contexts.Restore(previous);
            }
            return false;
        }
    }

    /// <summary>
    /// Joined players share player 1's Steam account. Settings, key bindings and achievements stay
    /// player 1's. (Outfits are saved per player, see PlayerFiles; coins and unlocks are one shared
    /// wallet, which only this process writes, so they save from any player.)
    /// </summary>
    [HarmonyPatch]
    internal static class OnlyPlayerOneSavesPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(GameSettings), nameof(GameSettings.SaveWithoutApplying));
            yield return AccessTools.Method(typeof(GameSettings), nameof(GameSettings.SaveInputBindings));
            yield return AccessTools.Method(typeof(SteamAchievementManager), nameof(SteamAchievementManager.Unlock));
            yield return AccessTools.Method(typeof(SteamAchievementManager), nameof(SteamAchievementManager.SetProgress));
            yield return AccessTools.Method(typeof(SteamAchievementManager), nameof(SteamAchievementManager.IncrementProgress));
            yield return AccessTools.Method(typeof(SteamAchievementManager), nameof(SteamAchievementManager.IndicateProgressOnMultipleOf));
        }

        private static bool Prefix() => Contexts.Current == 0;
    }

    /// <summary>Joined players have no microphone of their own.</summary>
    [HarmonyPatch(typeof(PlayerVoiceChat), "Update")]
    internal static class NoVoiceForJoinedPlayersPatch
    {
        private static bool Prefix(PlayerVoiceChat __instance) => Owners.Of(__instance) <= 0;
    }
}
