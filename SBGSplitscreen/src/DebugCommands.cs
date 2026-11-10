using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace SBGSplitscreen
{
    /// <summary>
    /// Test hooks, only with --sbg-debug. Write commands into BepInEx/SBGSplitscreen/cmd.txt; they
    /// run within half a second and the file is deleted. A status snapshot is kept in status.txt.
    ///   host | status | devices | shot &lt;file.png&gt; | quit
    ///   vpad add                      add a virtual gamepad (does not touch real controllers)
    ///   vpad &lt;n&gt; press &lt;button&gt;     tap a button (south/east/west/north/start/select/rb/lb/rt/lt)
    ///   vpad &lt;n&gt; hold &lt;button&gt; | vpad &lt;n&gt; release
    ///   vpad &lt;n&gt; stick &lt;lx&gt; &lt;ly&gt; [rx ry]
    ///   join &lt;n&gt;                      join with gamepad n of the ordered pad list
    ///   leave &lt;player&gt;               player is 2-4
    /// </summary>
    internal static class DebugCommands
    {
        private sealed class VirtualPad
        {
            public Gamepad Device;
            public GamepadState State;
            public float ReleaseAt = -1f;
            public GamepadButton? TapButton;
        }

        private static readonly List<VirtualPad> virtualPads = new List<VirtualPad>();

        /// <summary>drive: steers a virtual pad's golf cart at another player, every frame, until it gets there.</summary>
        private sealed class Autopilot
        {
            public VirtualPad Pad;
            public int Driver;
            public int Target;
            public float Until;
            public Vector3? TargetStart;
            public float Closest = float.MaxValue;
        }

        private static Autopilot autopilot;

        /// <summary>Test runs log the spread each gun shot is given, to check the pistol setting.</summary>
        [HarmonyPatch(typeof(VectorExtensions), nameof(VectorExtensions.RandomlyRotatedDeg), new[] { typeof(Vector3), typeof(float) })]
        private static class LogShotSpread
        {
            private static void Postfix(Vector3 vector, float maxAngle, Vector3 __result)
            {
                if (Settings.DebugMode)
                {
                    Plugin.Log.LogInfo($"Shot spread: up to {maxAngle:F2} degrees, this shot {Vector3.Angle(vector, __result):F2} degrees off aim");
                }
            }
        }

        /// <summary>startcourse with a hole: the course starts there instead of at its first hole.</summary>
        public static int StartHoleIndex = -1;

        [HarmonyPatch(typeof(ServerPersistentCourseData), nameof(ServerPersistentCourseData.ResetNextHoleIndex))]
        private static class StartAtHole
        {
            private static void Postfix()
            {
                if (StartHoleIndex > 0)
                {
                    ServerPersistentCourseData.SetNextHoleIndex(StartHoleIndex);
                    StartHoleIndex = -1;
                }
            }
        }
        private static string dir;
        private static float nextPoll;
        private static float nextStatus;
        private static long lastLookups;
        private static int lastLookupFrame;

        private static string CommandPath => Path.Combine(dir, "cmd.txt");
        private static string StatusPath => Path.Combine(dir, "status.txt");

        public static void Init()
        {
            dir = Path.Combine(Paths.BepInExRootPath, "SBGSplitscreen");
            Directory.CreateDirectory(dir);
            Plugin.Log.LogInfo("Debug commands enabled: " + CommandPath);
        }

        public static void Update()
        {
            UpdateAutopilot();
            foreach (var vp in virtualPads)
            {
                if (vp.TapButton.HasValue && vp.ReleaseAt >= 0f && Time.unscaledTime >= vp.ReleaseAt)
                {
                    vp.State = SetButton(vp.State, vp.TapButton.Value, false);
                    vp.TapButton = null;
                    vp.ReleaseAt = -1f;
                    InputSystem.QueueStateEvent(vp.Device, vp.State);
                }
            }

            if (Time.unscaledTime >= nextStatus)
            {
                nextStatus = Time.unscaledTime + 2f;
                File.WriteAllText(StatusPath, Status());
            }
            if (Time.unscaledTime < nextPoll)
            {
                return;
            }
            nextPoll = Time.unscaledTime + 0.5f;
            if (!File.Exists(CommandPath))
            {
                return;
            }
            string[] lines = File.ReadAllLines(CommandPath);
            File.Delete(CommandPath);
            foreach (string line in lines.Select(l => l.Trim()).Where(l => l.Length > 0))
            {
                try
                {
                    Run(line);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Debug command '{line}' failed: {e}");
                }
            }
        }

        private static void Run(string line)
        {
            Plugin.Log.LogInfo("Debug command: " + line);
            string[] a = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            switch (a[0].ToLowerInvariant())
            {
                case "host":
                    SingletonBehaviour<MainMenu>.Instance.StartHost();
                    break;
                case "status":
                    Plugin.Log.LogInfo(Status());
                    break;
                case "devices":
                    foreach (var d in InputSystem.devices)
                    {
                        Plugin.Log.LogInfo($"  #{d.deviceId} {d.layout} '{d.displayName}' iface={d.description.interfaceName} caps={d.description.capabilities}");
                    }
                    break;
                case "shot":
                    ScreenCapture.CaptureScreenshot(line.Substring(line.IndexOf(' ') + 1).Trim());
                    break;
                case "quit":
                    Application.Quit();
                    break;
                case "join":
                    Splitscreen.Join(InputContexts.OrderedPads()[int.Parse(a[1])]);
                    break;
                case "leave":
                    Splitscreen.Leave(int.Parse(a[1]) - 1);
                    break;
                case "drive":
                {
                    // drive <pad> <driver player> <target player> [seconds]: full throttle, steering the driver's cart at the target.
                    autopilot = new Autopilot
                    {
                        Pad = virtualPads[int.Parse(a[1])],
                        Driver = int.Parse(a[2]) - 1,
                        Target = int.Parse(a[3]) - 1,
                        Until = Time.unscaledTime + (a.Length > 4 ? F(a[4]) : 4f)
                    };
                    break;
                }
                case "shapes":
                {
                    // shapes <player>: that player's colliders, their layers and sizes, and which ones gunshots can hit.
                    var player = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    var sb = new StringBuilder("Shapes:\n");
                    int gunMask = GameManager.LayerSettings.GunHittablesMask;
                    foreach (var col in player.GetComponentsInChildren<Collider>(true))
                    {
                        string size = col is CapsuleCollider cap ? $"capsule r={cap.radius * col.transform.lossyScale.x:F2} h={cap.height * col.transform.lossyScale.y:F2}"
                            : col is SphereCollider sph ? $"sphere r={sph.radius * col.transform.lossyScale.x:F2}"
                            : col is BoxCollider box ? $"box {Vector3.Scale(box.size, col.transform.lossyScale):F2}" : col.GetType().Name;
                        sb.AppendLine($"  {col.name} layer={LayerMask.LayerToName(col.gameObject.layer)} {(col.isTrigger ? "trigger " : "")}{(col.enabled && col.gameObject.activeInHierarchy ? "" : "off ")}{size} bounds={col.bounds.size:F2} gunHits={((gunMask >> col.gameObject.layer) & 1) == 1}");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "equip":
                {
                    // equip <player> <ItemType>: select the inventory slot holding that item.
                    int ctx = int.Parse(a[1]) - 1;
                    var inventory = Contexts.GetValue(ctx, "GameManager.localPlayerInventory") as PlayerInventory;
                    var item = (ItemType)Enum.Parse(typeof(ItemType), a[2], true);
                    int index = inventory == null ? -1 : inventory.slots.FindIndex(s => s.itemType == item);
                    int previous = Contexts.Enter(ctx);
                    try
                    {
                        Plugin.Log.LogInfo($"Equip {item} for player {a[1]}: slot {index}, {index >= 0 && inventory.TrySelectItemSlot(index, false)}");
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    break;
                }
                case "pistolat":
                {
                    // pistolat <shooter> <target> <sideways m> [distance m]: stand the target in front of the
                    // shooter's camera, its body that far beside the aim line, and fire the equipped item.
                    int shooterCtx = int.Parse(a[1]) - 1, targetCtx = int.Parse(a[2]) - 1;
                    float sideways = F(a[3]), distance = a.Length > 4 ? F(a[4]) : 8f;
                    var shooter = Contexts.GetValue(shooterCtx, "GameManager.localPlayerInfo") as PlayerInfo;
                    var target = Contexts.GetValue(targetCtx, "GameManager.localPlayerInfo") as PlayerInfo;
                    var camera = Contexts.GetValue(shooterCtx, "GameManager.camera") as Camera;
                    if (shooter == null || target == null || camera == null)
                    {
                        Plugin.Log.LogInfo("pistolat: missing shooter, target or camera");
                        break;
                    }
                    var cam = camera.transform;
                    Vector3 flatRight = Vector3.Cross(Vector3.up, cam.forward).normalized;
                    Vector3 aimed = cam.position + cam.forward * distance;
                    var body = target.Movement.UprightCollider;
                    Vector3 bodyOffset = body.transform.TransformPoint(body.center) - target.transform.position;
                    Vector3 spot = aimed + flatRight * sideways - bodyOffset;
                    int previous = Contexts.Enter(targetCtx);
                    try
                    {
                        target.Movement.Teleport(spot, Quaternion.LookRotation(-new Vector3(cam.forward.x, 0f, cam.forward.z)), resetState: true);
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    Physics.SyncTransforms();
                    Vector3 axis = body.transform.TransformPoint(body.center);
                    Vector3 toAxis = axis - cam.position;
                    float miss = Vector3.Cross(cam.forward, toAxis).magnitude;
                    previous = Contexts.Enter(shooterCtx);
                    try
                    {
                        bool used = shooter.Inventory.TryUseItem(false, out _);
                        Plugin.Log.LogInfo($"pistolat: player {a[2]} {distance:F0} m ahead, aim line {miss:F2} m from body centre; fired: {used}; target knockout now {target.Movement.KnockoutState}");
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    break;
                }
                case "onlinecheck":
                {
                    // onlinecheck <target> <sideways m> [distance m] [turned degrees]: the host's check of a pistol hit
                    // reported by someone online, for a shot from that far away passing that far beside the
                    // target's body; "turned" spins the target that much between the shot and the check.
                    var target = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    float sideways = F(a[2]), distance = a.Length > 3 ? F(a[3]) : 8f, turned = a.Length > 4 ? F(a[4]) : 0f;
                    var hittable = target.AsHittable;
                    var body = target.Movement.UprightCollider;
                    Vector3 centre = body.transform.TransformPoint(body.center);
                    Vector3 forward = target.transform.forward;
                    Vector3 side = Vector3.Cross(Vector3.up, forward).normalized;
                    Vector3 origin = centre + forward * distance + Vector3.up * 0.3f;
                    Vector3 aim = centre + side * sideways;
                    var ray = new Ray(origin, aim - origin);
                    if (!target.Movement.HittableCollider.Raycast(ray, out var hit, distance * 2f))
                    {
                        Plugin.Log.LogInfo($"onlinecheck: a shot {sideways:F2} m beside the body misses the hit capsule anyway");
                        break;
                    }
                    Vector3 hitLocal = hittable.transform.InverseTransformPoint(hit.point);
                    Vector3 originLocal = hittable.transform.InverseTransformPoint(origin);
                    var rotation = target.transform.rotation;
                    target.transform.rotation = Quaternion.AngleAxis(turned, Vector3.up) * rotation;
                    Physics.SyncTransforms();
                    bool allowed = ItemTweaks.AllowsReportedHit(hittable, hitLocal, ray.direction, originLocal);
                    target.transform.rotation = rotation;
                    Physics.SyncTransforms();
                    Plugin.Log.LogInfo($"onlinecheck: shot {sideways:F2} m beside the body from {distance:F0} m, target turned {turned:F0} degrees since: {(allowed ? "counts" : "ignored")}");
                    break;
                }
                case "ko":
                {
                    var sb = new StringBuilder("Knockout:");
                    for (int c = 0; c < Contexts.MaxPlayers; c++)
                    {
                        if (Contexts.GetValue(c, "GameManager.localPlayerInfo") is PlayerInfo p && p != null)
                        {
                            sb.Append($" p{c + 1}={p.Movement.KnockoutState}");
                        }
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "stophost":
                    // Back to the main menu, as Exit Game does for the host.
                    BNetworkManager.singleton.StopHost();
                    break;
                case "leavebutton":
                {
                    // leavebutton <player>: click that player's "Leave Game" pause menu button, as with the mouse.
                    var menu = Contexts.GetValue(int.Parse(a[1]) - 1, Contexts.SingletonField(typeof(PauseMenu))) as PauseMenu;
                    menu?.exitGameButton.onClick.Invoke();
                    break;
                }
                case "vpad":
                    VirtualPadCommand(a);
                    break;
                case "ui":
                    DumpCanvases();
                    break;
                case "tree":
                {
                    // tree [pN:]<path under that player's HUD root, player 1's gameplay canvas by default> [depth]
                    string treePath = a[1];
                    GameObject treeRoot = PlayerHuds.PlayerOneCanvas();
                    if (treePath.StartsWith("p") && treePath.Length > 3 && treePath[2] == ':')
                    {
                        int player = treePath[1] - '1';
                        treeRoot = player == 0 ? PlayerHuds.PlayerOneCanvas() : PlayerHuds.HudOf(player);
                        treePath = treePath.Substring(3);
                    }
                    var root = treeRoot?.transform.Find(treePath.Replace('+', ' '));
                    var sb = new StringBuilder();
                    if (root != null)
                    {
                        DumpTree(root, 0, a.Length > 2 ? int.Parse(a[2]) : 3, sb);
                    }
                    File.WriteAllText(Path.Combine(dir, "tree.txt"), root != null ? sb.ToString() : "not found");
                    break;
                }
                case "pause":
                case "custom":
                {
                    // pause <player> | custom <player> [close]: open that player's own menu, as their Start button / the shop would.
                    int previous = Contexts.Enter(int.Parse(a[1]) - 1);
                    try
                    {
                        if (a[0] == "pause")
                        {
                            PauseMenu.Pause();
                        }
                        else
                        {
                            SingletonBehaviour<PlayerCustomizationMenu>.Instance.SetEnabled(a.Length < 3, fromInteraction: false);
                        }
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    break;
                }
                case "give":
                {
                    // give <player> <ItemType> [uses]
                    var inventory = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInventory") as PlayerInventory;
                    var item = (ItemType)Enum.Parse(typeof(ItemType), a[2], true);
                    bool added = inventory != null && inventory.ServerTryAddItem(item, a.Length > 3 ? int.Parse(a[3]) : 3);
                    Plugin.Log.LogInfo($"Give {item} to player {a[1]}: {added}");
                    break;
                }
                case "p1pad":
                    // p1pad <n>: player 1 uses virtual pad n (test runs ignore the real controllers).
                    InputContexts.ForcedPrimaryPad = virtualPads[int.Parse(a[1])].Device;
                    break;
                case "giant":
                {
                    // giant <player>: as if that player ate a jumbo burger.
                    int ctx = int.Parse(a[1]) - 1;
                    var player = Contexts.GetValue(ctx, "GameManager.localPlayerInfo") as PlayerInfo;
                    int previous = Contexts.Enter(ctx);
                    try
                    {
                        player?.LocalPlayerActivateJumboBurgerGiantForm(ItemUseId.Invalid);
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    break;
                }
                case "respawn":
                {
                    // respawn <player>: what the server does after an elimination, from player 1's context.
                    var player = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    bool started = player != null && player.Movement.TryBeginRespawn(isRestart: true, RespawnTarget.TeeOrCheckpoint, immediate: true);
                    Plugin.Log.LogInfo($"Respawn player {a[1]}: {started}");
                    break;
                }
                case "flash":
                {
                    // flash <player>: as if a flash camera went off right in front of them.
                    var player = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    AccessTools.Method(typeof(PlayerInfo), "UserCode_RpcInformOfFlashCameraHit__Single")?.Invoke(player, new object[] { 1f });
                    break;
                }
                case "eliminate":
                {
                    // eliminate <player>: as if they went out of bounds, through the server's own path.
                    var player = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    player?.AsGolfer.ServerEliminate(EliminationReason.OutOfBounds);
                    Plugin.Log.LogInfo($"Eliminate player {a[1]}: {player != null}");
                    break;
                }
                case "traffic":
                {
                    // Moving city traffic, and for each player how many of each car's colliders ignore them.
                    var sb = new StringBuilder("Traffic:\n");
                    var players = UnityEngine.Object.FindObjectsByType<PlayerInfo>(FindObjectsSortMode.None);
                    foreach (var car in UnityEngine.Object.FindObjectsByType<TrafficVehicle>(FindObjectsSortMode.None).Where(v => v.NetworkisActive).Take(6))
                    {
                        var colliders = car.NonTriggerColliders.Where(c => c != null).ToList();
                        string ignores = string.Join(", ", players.Select(p => $"{p.name} {colliders.Count(c => Physics.GetIgnoreCollision(c, p.Movement.UprightCollider))}/{colliders.Count}"));
                        sb.AppendLine($"  {car.name} {car.Type} @{car.transform.position:F1} v={car.Networkvelocity.magnitude:F1} ignores: {ignores}");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "infront":
                {
                    // infront <player> [metres]: stand that player on the road ahead of the nearest moving car.
                    int ctx = int.Parse(a[1]) - 1;
                    float ahead = a.Length > 2 ? F(a[2]) : 12f;
                    var player = Contexts.GetValue(ctx, "GameManager.localPlayerInfo") as PlayerInfo;
                    var car = UnityEngine.Object.FindObjectsByType<TrafficVehicle>(FindObjectsSortMode.None)
                        .Where(v => v.NetworkisActive && v.Networkvelocity.sqrMagnitude > 4f)
                        .OrderBy(v => player == null ? 0f : Vector3.Distance(v.transform.position, player.transform.position))
                        .FirstOrDefault();
                    if (player == null || car == null)
                    {
                        Plugin.Log.LogInfo("infront: no player or no moving car");
                        break;
                    }
                    Vector3 heading = car.Networkvelocity.normalized;
                    Vector3 spot = car.transform.position + heading * ahead + Vector3.up * 0.5f;
                    int previous = Contexts.Enter(ctx);
                    try
                    {
                        player.Movement.Teleport(spot, Quaternion.LookRotation(-heading), resetState: true);
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    Plugin.Log.LogInfo($"infront: player {ctx + 1} placed {ahead:F0} m ahead of {car.name} ({car.Type}, {car.Networkvelocity.magnitude:F1} m/s) at {spot:F1}");
                    break;
                }
                case "bodies":
                {
                    // Physics state of players and golf carts: what could stop them colliding.
                    var layers = GameManager.LayerSettings;
                    var sb = new StringBuilder($"Bodies: player<->cart layers collide={!Physics.GetIgnoreLayerCollision(layers.PlayerLayer, layers.GolfCartLayer)} player<->player={!Physics.GetIgnoreLayerCollision(layers.PlayerLayer, layers.PlayerLayer)}\n");
                    var entities = UnityEngine.Object.FindObjectsByType<Entity>(FindObjectsSortMode.None).Where(e => e.IsPlayer || e.IsGolfCart).ToList();
                    foreach (var e in entities)
                    {
                        var rb = e.Rigidbody;
                        var colliders = e.GetComponentsInChildren<Collider>(true);
                        string ignored = PhysicsManager.TryGetEntitiesIgnoringCollisionsWith(e, out var set) ? string.Join(",", set.Where(x => x != null).Select(x => x.name)) : "";
                        sb.AppendLine($"  {e.name} @{e.transform.position:F1} layer={LayerMask.LayerToName(e.gameObject.layer)} kinematic={rb?.isKinematic} detect={rb?.detectCollisions} " +
                            $"colliders={colliders.Count(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)}/{colliders.Length} layers=[{string.Join(",", colliders.Where(c => !c.isTrigger).Select(c => LayerMask.LayerToName(c.gameObject.layer)).Distinct())}] " +
                            $"exclude={(rb != null ? rb.excludeLayers.value : 0)} ignoring=[{ignored}]");
                    }
                    foreach (var cart in entities.Where(e => e.IsGolfCart))
                    {
                        var cartColliders = cart.GetComponentsInChildren<Collider>(true).Where(c => !c.isTrigger).ToList();
                        foreach (var player in entities.Where(e => e.IsPlayer))
                        {
                            var body = player.PlayerInfo.Movement.UprightCollider;
                            int ignoring = cartColliders.Count(c => Physics.GetIgnoreCollision(c, body));
                            sb.AppendLine($"  {cart.name} ignores {player.name}: {ignoring}/{cartColliders.Count} colliders");
                        }
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "p1current":
                {
                    // p1current <vpad>: make that virtual pad player 1's "current gamepad", as the game can at startup.
                    AccessTools.PropertySetter(typeof(InputManager), nameof(InputManager.CurrentGamepad)).Invoke(null, new object[] { virtualPads[int.Parse(a[1])].Device });
                    Plugin.Log.LogInfo($"p1current: player 1's current gamepad is now {InputManager.CurrentGamepad?.name}");
                    break;
                }
                case "menus":
                {
                    // Per player: pause state, input mode, and the menu stack with what is selected in it.
                    var sb = new StringBuilder("Menus:\n");
                    for (int c = 0; c < Contexts.MaxPlayers; c++)
                    {
                        if (!Contexts.IsActive(c))
                        {
                            continue;
                        }
                        int previous = Contexts.Enter(c);
                        try
                        {
                            var stack = Traverse.Create(typeof(MenuNavigation)).Field("activeInstances").GetValue() as List<MenuNavigation>;
                            sb.AppendLine($"  ctx{c}: paused={JoinedPauseMenus.IsPaused(c)} mode={InputManager.CurrentMode} mask={InputManager.CurrentModeMask} menus=[{string.Join(" > ", (stack ?? new List<MenuNavigation>()).Select(m => m == null ? "(destroyed)" : $"{m.name}:{(m.Selected != null ? m.Selected.name : "-")}"))}]");
                        }
                        finally
                        {
                            Contexts.Restore(previous);
                        }
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "lockon":
                {
                    // Per player: what they are locking onto and the marker their HUD shows; then homing projectiles in flight.
                    var sb = new StringBuilder("Lock-on:\n");
                    for (int c = 0; c < Contexts.MaxPlayers; c++)
                    {
                        if (!Contexts.IsActive(c))
                        {
                            continue;
                        }
                        int previous = Contexts.Enter(c);
                        try
                        {
                            var me = GameManager.LocalPlayerInfo;
                            sb.AppendLine($"  ctx{c}: golferTarget={me?.AsGolfer.LockOnTarget?.name} itemTarget={me?.Inventory.LockOnTarget?.name} aimingItem={me?.Inventory.IsAimingItem} item={me?.Inventory.GetEffectivelyEquippedItem()}");
                            if (SingletonBehaviour<LockOnTargetUiManager>.HasInstance)
                            {
                                var manager = SingletonBehaviour<LockOnTargetUiManager>.Instance;
                                var active = Traverse.Create(manager).Field("activeTargets").GetValue() as System.Collections.IDictionary;
                                sb.AppendLine($"    marker manager in {manager.transform.root.name}: {active?.Count ?? -1} marker(s)");
                                if (active != null)
                                {
                                    foreach (System.Collections.DictionaryEntry e in active)
                                    {
                                        var ui = (Component)e.Value;
                                        var container = Traverse.Create(ui).Field("container").GetValue<Transform>();
                                        sb.AppendLine($"      on {((Component)e.Key).name}: parent={ui.transform.parent?.name} shown={container.gameObject.activeInHierarchy} at={container.position}");
                                    }
                                }
                            }
                        }
                        finally
                        {
                            Contexts.Restore(previous);
                        }
                    }
                    foreach (var h in UnityEngine.Object.FindObjectsByType<Hittable>(FindObjectsSortMode.None))
                    {
                        if (h.NetworkhomingTargetHittable != null)
                        {
                            sb.AppendLine($"  homing {h.name} -> {h.NetworkhomingTargetHittable.name}: p1={h.IsPlayingHomingWarning} {HomingWarnings.Describe(h)}");
                        }
                    }
                    foreach (var r in UnityEngine.Object.FindObjectsByType<Rocket>(FindObjectsSortMode.None))
                    {
                        sb.AppendLine($"  rocket {r.name} -> {(r.NetworkhomingTargetHittable != null ? r.NetworkhomingTargetHittable.name : "nothing")}: p1={r.IsPlayingHomingWarning} {HomingWarnings.Describe(r)}");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "settings":
                {
                    // settings <tab name>: open the main menu's settings on that tab.
                    var menu = Resources.FindObjectsOfTypeAll<SettingsMenu>().FirstOrDefault(m => m.gameObject.scene.name != null);
                    if (menu != null)
                    {
                        for (var t = menu.transform; t != null; t = t.parent)
                        {
                            t.gameObject.SetActive(true);
                        }
                        foreach (var tab in menu.tabNavigation)
                        {
                            tab.gameObject.SetActive(tab.name.Equals(a[1], StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    break;
                }
                case "monitor":
                {
                    // monitor [index]: list the entries in Settings > Graphics > Monitor; with an index, pick one as a player would.
                    var row = Resources.FindObjectsOfTypeAll<DropdownOption>().FirstOrDefault(o => o.name == "Monitor (mod)" && o.gameObject.scene.name != null);
                    if (row != null && a.Length > 1)
                    {
                        row.value = int.Parse(a[1]);
                    }
                    var nav = row?.Selectable.navigation;
                    Plugin.Log.LogInfo(row == null ? "Monitor row: missing"
                        : $"Monitor row: [{string.Join(" | ", row.Dropdown.options.Select(o => o.text))}] selected={row.value} up={nav?.selectOnUp?.transform.parent.parent.name} down={nav?.selectOnDown?.transform.parent.parent.name}");
                    break;
                }
                case "setting":
                {
                    // setting <SettingsMenu dropdown field> <index>: change a game setting as a player would.
                    var menu = Resources.FindObjectsOfTypeAll<SettingsMenu>().FirstOrDefault(m => m.gameObject.scene.name != null);
                    if (menu != null && AccessTools.Field(typeof(SettingsMenu), a[1])?.GetValue(menu) is DropdownOption option)
                    {
                        option.value = int.Parse(a[2]);
                    }
                    break;
                }
                case "window":
                    // window free: stop keeping the test window in place (to test moving it).
                    Settings.TestWindow = null;
                    break;
                case "find":
                {
                    // find <field of SettingsMenu | component type> [levels up] [depth]: hierarchy around it, into tree.txt.
                    int up = a.Length > 2 ? int.Parse(a[2]) : 1;
                    int depth = a.Length > 3 ? int.Parse(a[3]) : 3;
                    Transform found = null;
                    var menu = Resources.FindObjectsOfTypeAll<SettingsMenu>().FirstOrDefault(m => m.gameObject.scene.name != null);
                    if (menu != null && AccessTools.Field(typeof(SettingsMenu), a[1])?.GetValue(menu) is Component field)
                    {
                        found = field.transform;
                    }
                    else
                    {
                        var type = AccessTools.TypeByName(a[1]);
                        found = type == null ? null : Resources.FindObjectsOfTypeAll(type).OfType<Component>().FirstOrDefault(c => c.gameObject.scene.name != null)?.transform;
                    }
                    var sb = new StringBuilder();
                    if (found != null)
                    {
                        var start = found;
                        for (int i = 0; i < up && start.parent != null; i++)
                        {
                            start = start.parent;
                        }
                        string path = start.name;
                        for (var p = start.parent; p != null; p = p.parent)
                        {
                            path = p.name + "/" + path;
                        }
                        sb.AppendLine("path: " + path);
                        DumpTree(start, 0, depth, sb);
                    }
                    File.WriteAllText(Path.Combine(dir, "tree.txt"), found != null ? sb.ToString() : "not found");
                    break;
                }
                case "prompts":
                {
                    // Per player: the button prompts on screen and what the icon lookup gives for the Aim action.
                    var sb = new StringBuilder("Prompts:\n");
                    for (int c = 0; c < Contexts.MaxPlayers; c++)
                    {
                        if (!Contexts.IsActive(c))
                        {
                            continue;
                        }
                        int previous = Contexts.Enter(c);
                        try
                        {
                            var iconSet = Traverse.Create(typeof(InputManager)).Field("currentDeviceIconSet").GetValue();
                            var aim = global::PlayerInput.Controls?.Gameplay.Aim;
                            sb.AppendLine($"  ctx{c}: device={InputManager.CurrentDeviceType} iconSet={(iconSet == null ? "null" : iconSet.ToString())} controlsIsMine={ReferenceEquals(global::PlayerInput.Controls, InputManager.Controls)} aimIcon='{InputManager.GetCompositeInputIconRichTextTags(aim, string.Empty, true)}'");
                            if (SingletonBehaviour<ButtonPromptManager>.HasInstance)
                            {
                                foreach (var prompt in SingletonBehaviour<ButtonPromptManager>.Instance.GetComponentsInChildren<ButtonPrompt>(true))
                                {
                                    sb.AppendLine($"    {prompt.transform.parent?.name}: active={prompt.gameObject.activeInHierarchy} text='{prompt.labelObj.text}'");
                                }
                            }
                        }
                        finally
                        {
                            Contexts.Restore(previous);
                        }
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "courses":
                {
                    var sb = new StringBuilder("Courses:\n");
                    var courses = GameManager.AllCourses.Courses;
                    for (int i = 0; i < courses.Length; i++)
                    {
                        sb.AppendLine($"  {i}: {courses[i].name} ({courses[i].Holes.Length} holes) {string.Join(", ", courses[i].Holes.Select(h => $"{h.name}({h.Par})"))}");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "startcourse":
                    // startcourse [course] [hole]: same as the match setup menu (pick a course, start it), optionally from a later hole.
                    CourseManager.ServerSetCourse(GameManager.AllCourses.Courses[a.Length > 1 ? int.Parse(a[1]) : 0]);
                    StartHoleIndex = a.Length > 2 ? int.Parse(a[2]) : -1;
                    CourseManager.StartCourse();
                    break;
                case "checkpoints":
                {
                    var sb = new StringBuilder("Checkpoints:\n");
                    var found = UnityEngine.Object.FindObjectsByType<Checkpoint>(FindObjectsSortMode.None).OrderBy(cp => cp.Order).ToList();
                    foreach (var cp in found)
                    {
                        sb.AppendLine($"  {cp.name} order={cp.Order} @{cp.transform.position:F1}");
                    }
                    sb.AppendLine("  reached: " + CheckpointViews.Describe());
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "tocheckpoint":
                {
                    // tocheckpoint <player> [index]: walk that player into a checkpoint (as if they reached it).
                    int ctx = int.Parse(a[1]) - 1;
                    var found = UnityEngine.Object.FindObjectsByType<Checkpoint>(FindObjectsSortMode.None).OrderBy(cp => cp.Order).ToList();
                    var player = Contexts.GetValue(ctx, "GameManager.localPlayerInfo") as PlayerInfo;
                    int index = a.Length > 2 ? int.Parse(a[2]) : 0;
                    if (player != null && index < found.Count)
                    {
                        var cp = found[index];
                        int previous = Contexts.Enter(ctx);
                        try
                        {
                            player.Movement.Teleport(cp.transform.position + Vector3.up * 0.5f + cp.transform.forward * 1.5f, cp.transform.rotation, resetState: true);
                        }
                        finally
                        {
                            Contexts.Restore(previous);
                        }
                        Plugin.Log.LogInfo($"tocheckpoint: player {ctx + 1} at {cp.name}");
                    }
                    break;
                }
                case "entercart":
                {
                    // entercart <player>: stand that player beside the nearest golf cart, facing it.
                    int ctx = int.Parse(a[1]) - 1;
                    var player = Contexts.GetValue(ctx, "GameManager.localPlayerInfo") as PlayerInfo;
                    var cart = UnityEngine.Object.FindObjectsByType<GolfCartInfo>(FindObjectsSortMode.None)
                        .OrderBy(g => player == null ? 0f : Vector3.Distance(g.transform.position, player.transform.position)).FirstOrDefault();
                    if (player == null || cart == null)
                    {
                        Plugin.Log.LogInfo("entercart: no player or no cart");
                        break;
                    }
                    int previous = Contexts.Enter(ctx);
                    try
                    {
                        // Beside the cart, facing it; the test then presses the player's own Enter button.
                        Vector3 spot = cart.transform.position + cart.transform.right * 2.2f + Vector3.up * 0.3f;
                        player.Movement.Teleport(spot, Quaternion.LookRotation(-cart.transform.right), resetState: true);
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    Plugin.Log.LogInfo($"entercart: player {ctx + 1} standing beside {cart.name}");
                    break;
                }
                case "carts":
                {
                    var sb = new StringBuilder("Carts:\n");
                    foreach (var cart in UnityEngine.Object.FindObjectsByType<GolfCartInfo>(FindObjectsSortMode.None))
                    {
                        var conn = cart.netIdentity.connectionToClient;
                        var body = cart.GetComponent<Rigidbody>();
                        sb.AppendLine($"  {cart.name} @{cart.transform.position:F1} speed={(body != null ? body.linearVelocity.magnitude : 0f):F1} "
                            + $"controlledBy={(conn is LocalPlayerConnection l ? $"p{l.Slot + 1}" : conn != null ? conn.connectionId.ToString() : "host")} owned={cart.netIdentity.isOwned} "
                            + $"sync={cart.Movement.syncDirection} reserver={(cart.NetworkdriverSeatReserver != null ? cart.NetworkdriverSeatReserver.name : "-")} "
                            + $"seats=[{string.Join(", ", cart.passengers.Select((p, i) => $"{i}:{(p != null ? p.name : "-")}"))}]");
                    }
                    for (int c = 0; c < Contexts.MaxPlayers; c++)
                    {
                        if (Contexts.GetValue(c, "GameManager.localPlayerInfo") is PlayerInfo p && p != null)
                        {
                            var seat = p.ActiveGolfCartSeat;
                            sb.AppendLine($"  p{c + 1} thinks: {(seat.golfCart != null ? $"{seat.golfCart.name} seat {seat.seat}" : "not in a cart")}");
                        }
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    break;
                }
                case "interact":
                {
                    // interact <player>: press that player's interact on whatever they face.
                    var player = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    var interactor = player != null ? player.GetComponentInChildren<PlayerInteractor>(true) : null;
                    Plugin.Log.LogInfo($"interact: player {a[1]}: {(interactor != null && interactor.TryInteract())}");
                    break;
                }
                case "exitcart":
                {
                    var player = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    player?.ExitGolfCart(GolfCartExitType.Default);
                    Plugin.Log.LogInfo($"exitcart: player {a[1]}");
                    break;
                }
                case "cartseat":
                {
                    // cartseat <player> <seat>: switch seats in the cart they are in (0 = driver).
                    var player = Contexts.GetValue(int.Parse(a[1]) - 1, "GameManager.localPlayerInfo") as PlayerInfo;
                    Plugin.Log.LogInfo($"cartseat: player {a[1]} to seat {a[2]}: {(player != null && player.TrySwitchGolfCartSeatTo(int.Parse(a[2])))}");
                    break;
                }
                case "bindings":
                {
                    // bindings <player>: that player's gamepad bindings for interacting and the golf cart.
                    int previous = Contexts.Enter(int.Parse(a[1]) - 1);
                    try
                    {
                        var controls = InputManager.Controls;
                        var actions = new[] { controls.Gameplay.Interact }.Concat(controls.GolfCartDriver.Get().actions).Concat(controls.GolfCartShared.Get().actions);
                        Plugin.Log.LogInfo("Bindings: " + string.Join("; ", actions.Select(act =>
                            $"{act.actionMap.name}/{act.name}={string.Join("|", act.bindings.Where(b => b.effectivePath.Contains("Gamepad")).Select(b => b.effectivePath))} {(act.enabled ? "on" : "off")}")));
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                    break;
                }
                case "skiphole":
                    HarmonyLib.AccessTools.Method(typeof(CourseManager), "SkipHole").Invoke(null, null);
                    break;
                default:
                    Plugin.Log.LogWarning("Unknown debug command: " + a[0]);
                    break;
            }
        }

        private static void VirtualPadCommand(string[] a)
        {
            if (a[1] == "add")
            {
                var device = InputSystem.AddDevice<Gamepad>($"SplitscreenTestPad{virtualPads.Count}");
                virtualPads.Add(new VirtualPad { Device = device, State = new GamepadState() });
                Plugin.Log.LogInfo($"Virtual pad {virtualPads.Count - 1} = device #{device.deviceId}");
                return;
            }
            var vp = virtualPads[int.Parse(a[1])];
            switch (a[2])
            {
                case "press":
                    vp.TapButton = ParseButton(a[3]);
                    vp.State = SetButton(vp.State, vp.TapButton.Value, true);
                    vp.ReleaseAt = Time.unscaledTime + 0.15f;
                    break;
                case "hold":
                    vp.State = SetButton(vp.State, ParseButton(a[3]), true);
                    break;
                case "release":
                    vp.State = new GamepadState { leftStick = vp.State.leftStick, rightStick = vp.State.rightStick };
                    break;
                case "stick":
                    vp.State.leftStick = new Vector2(F(a[3]), F(a[4]));
                    if (a.Length >= 7)
                    {
                        vp.State.rightStick = new Vector2(F(a[5]), F(a[6]));
                    }
                    break;
            }
            InputSystem.QueueStateEvent(vp.Device, vp.State);
        }

        /// <summary>Root canvases with their render mode, top-level children and the singletons living inside.</summary>
        private static void DumpCanvases()
        {
            var sb = new StringBuilder("Canvases:\n");
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (!canvas.isRootCanvas || canvas.gameObject.scene.name == null)
                {
                    continue;
                }
                var go = canvas.gameObject;
                var singletons = go.GetComponentsInChildren<SingletonBehaviourBase>(true).Select(s => s.GetType().Name).Distinct();
                var children = Enumerable.Range(0, go.transform.childCount).Select(i => go.transform.GetChild(i))
                    .Select(t => t.name + (t.gameObject.activeSelf ? "" : "(off)"));
                sb.AppendLine($"- {go.name} scene={go.scene.name} active={go.activeInHierarchy} mode={canvas.renderMode} order={canvas.sortingOrder} scaler={go.GetComponent<UnityEngine.UI.CanvasScaler>() != null}");
                sb.AppendLine($"    singletons: {string.Join(", ", singletons)}");
                sb.AppendLine($"    children: {string.Join(", ", children)}");
            }
            File.WriteAllText(Path.Combine(dir, "ui.txt"), sb.ToString());
            Plugin.Log.LogInfo("Wrote ui.txt");
        }

        private static void DumpTree(Transform t, int depth, int maxDepth, StringBuilder sb)
        {
            string layout = t is RectTransform rt
                ? $" anchors={rt.anchorMin}-{rt.anchorMax} pivot={rt.pivot} pos={rt.anchoredPosition} size={rt.sizeDelta} rect={rt.rect.size}"
                : "";
            var comps = string.Join(",", t.GetComponents<Component>().Select(c => c.GetType().Name).Where(n => n != "RectTransform" && n != "CanvasRenderer"));
            var group = t.GetComponent<CanvasGroup>();
            var graphic = t.GetComponent<UnityEngine.UI.Graphic>();
            string look = (group != null ? $" alpha={group.alpha:F2}" : "")
                + (graphic != null ? $" graphic={(graphic.enabled ? "on" : "off")} color.a={graphic.color.a:F2}" : "")
                + (graphic is TMPro.TMP_Text text ? $" text='{text.text}'" : "")
                + $" scale={t.localScale}";
            sb.AppendLine($"{new string(' ', depth * 2)}{t.name}{(t.gameObject.activeSelf ? "" : " (off)")}{layout}{look} [{comps}]");
            if (depth < maxDepth)
            {
                foreach (Transform child in t)
                {
                    DumpTree(child, depth + 1, maxDepth, sb);
                }
            }
        }

        private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        private static void UpdateAutopilot()
        {
            if (autopilot == null)
            {
                return;
            }
            var pad = autopilot.Pad;
            var driver = Contexts.GetValue(autopilot.Driver, "GameManager.localPlayerInfo") as PlayerInfo;
            var target = Contexts.GetValue(autopilot.Target, "GameManager.localPlayerInfo") as PlayerInfo;
            Transform cart = driver != null && driver.ActiveGolfCartSeat.IsValid() ? driver.ActiveGolfCartSeat.golfCart.transform : null;
            bool done = Time.unscaledTime > autopilot.Until || cart == null || target == null;
            if (!done)
            {
                autopilot.TargetStart = autopilot.TargetStart ?? target.transform.position;
                Vector3 toTarget = target.transform.position - cart.position;
                toTarget.y = 0f;
                Vector3 forward = cart.forward;
                forward.y = 0f;
                float angle = Vector3.SignedAngle(forward, toTarget, Vector3.up);
                float distance = toTarget.magnitude;
                autopilot.Closest = Mathf.Min(autopilot.Closest, distance);
                pad.State.leftStick = new Vector2(Mathf.Clamp(angle / 25f, -1f, 1f), 0f);
                // The game reads the accelerator as pressed or not, so it is always all the way down.
                pad.State.rightTrigger = 1f;
                bool targetPushed = Vector3.Distance(target.transform.position, autopilot.TargetStart.Value) > 1.5f;
                done = distance < 1.2f || targetPushed;
            }
            if (done)
            {
                float pushed = target != null && autopilot.TargetStart.HasValue ? Vector3.Distance(target.transform.position, autopilot.TargetStart.Value) : 0f;
                Plugin.Log.LogInfo($"Autopilot stopped: {(cart == null ? "no cart" : $"{Vector3.Distance(cart.position, target != null ? target.transform.position : cart.position):F1} m from target")}, closest {autopilot.Closest:F1} m, target pushed {pushed:F1} m");
                pad.State.leftStick = Vector2.zero;
                pad.State.rightTrigger = 0f;
                autopilot = null;
            }
            InputSystem.QueueStateEvent(pad.Device, pad.State);
        }

        /// <summary>Triggers are analog values in GamepadState, not bits in its button mask.</summary>
        private static GamepadState SetButton(GamepadState state, GamepadButton button, bool down)
        {
            switch (button)
            {
                case GamepadButton.LeftTrigger:
                    state.leftTrigger = down ? 1f : 0f;
                    return state;
                case GamepadButton.RightTrigger:
                    state.rightTrigger = down ? 1f : 0f;
                    return state;
                default:
                    return state.WithButton(button, down);
            }
        }

        private static GamepadButton ParseButton(string name)
        {
            switch (name.ToLowerInvariant())
            {
                case "a": case "south": return GamepadButton.South;
                case "b": case "east": return GamepadButton.East;
                case "x": case "west": return GamepadButton.West;
                case "y": case "north": return GamepadButton.North;
                case "start": return GamepadButton.Start;
                case "select": case "back": return GamepadButton.Select;
                case "rb": return GamepadButton.RightShoulder;
                case "lb": return GamepadButton.LeftShoulder;
                case "rt": return GamepadButton.RightTrigger;
                case "lt": return GamepadButton.LeftTrigger;
                case "up": return GamepadButton.DpadUp;
                case "down": return GamepadButton.DpadDown;
                case "left": return GamepadButton.DpadLeft;
                case "right": return GamepadButton.DpadRight;
                default: throw new ArgumentException("Unknown button " + name);
            }
        }

        private static string Status()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"time={Time.unscaledTime:F0} fps={1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f):F0} context={Contexts.Current} extra={Contexts.ExtraCount}");
            sb.AppendLine($"scene={SceneManager.GetActiveScene().name} mainMenu={SingletonBehaviour<MainMenu>.HasInstance} loading={LoadingScreen.IsVisible} canJoin={Splitscreen.CanJoin()}");
            sb.AppendLine($"server={NetworkServer.active} conns={(NetworkServer.active ? NetworkServer.connections.Count : 0)} transport={Transport.active?.GetType().Name} netScene={NetworkManager.networkSceneName} loadingScene={NetworkServer.isLoadingScene} changingScene={BNetworkManager.IsChangingSceneOrShuttingDown} fadingIn={LoadingScreen.IsFadingScreenIn} match={CourseManager.MatchState}");
            long lookups = Owners.Lookups - lastLookups;
            int frames = Time.frameCount - lastLookupFrame;
            lastLookups = Owners.Lookups;
            lastLookupFrame = Time.frameCount;
            sb.AppendLine($"ownerLookupsPerFrame={(frames > 0 ? lookups / frames : 0)} fps={(frames > 0 ? frames / 2f : 0):F0}");
            sb.AppendLine($"players={UnityEngine.Object.FindObjectsByType<PlayerInfo>(FindObjectsSortMode.None).Length} {Splitscreen.Describe()}");
            for (int c = 0; c < Contexts.MaxPlayers; c++)
            {
                if (!Contexts.IsActive(c))
                {
                    continue;
                }
                var local = Contexts.GetValue(c, "GameManager.localPlayerInfo") as PlayerInfo;
                var cam = Contexts.GetValue(c, PlayerViews.CameraField) as Camera;
                var mode = Contexts.GetValue(c, "InputManager.<CurrentMode>k__BackingField");
                var remotes = Contexts.GetValue(c, "GameManager.remotePlayers") as List<PlayerInfo>;
                var pad = Contexts.GetValue(c, "InputManager.<CurrentGamepad>k__BackingField") as Gamepad;
                var mask = Contexts.GetValue(c, "InputManager.<CurrentModeMask>k__BackingField");
                var custom = Contexts.Has(Contexts.SingletonField(typeof(PlayerCustomizationMenu)))
                    ? Contexts.GetValue(c, Contexts.SingletonField(typeof(PlayerCustomizationMenu))) as PlayerCustomizationMenu : null;
                string category = custom != null ? HarmonyLib.Traverse.Create(custom).Field("currentCategory").GetValue()?.ToString() : "-";
                var localIdentity = Contexts.GetValue(c, LocalPlayerFlags.Field) as NetworkIdentity;
                var ownBall = local != null ? local.AsGolfer.OwnBall : null;
                var ballIcon = ownBall != null ? HarmonyLib.Traverse.Create(ownBall).Field("worldspaceIcon").GetValue() as UnityEngine.Object : null;
                string iconCanvas = ballIcon is Component ic && ic != null ? ic.GetComponentInParent<Canvas>()?.rootCanvas.name : "-";
                sb.AppendLine($"  ctx{c}: localPlayer={(localIdentity != null ? localIdentity.name + " flag=" + localIdentity.isLocalPlayer : "none")} ownBall={(ownBall != null ? ownBall.name : "none")} p1BallIconField={(ballIcon != null ? ballIcon.name : "none")} iconCanvas={iconCanvas}");
                if (cam != null)
                {
                    sb.AppendLine($"  ctx{c}: camera pos={cam.transform.position.ToString("F1")} yaw={cam.transform.eulerAngles.y:F0} knockedOut={(local != null && local.Movement.IsKnockedOutOrRecovering)} giant={(local != null && local.IsInJumboBurgerGiantForm)} inCart={(local != null && local.ActiveGolfCartSeat.IsValid())}");
                }
                sb.AppendLine($"  ctx{c}: local={(local != null ? local.name + " @" + local.transform.position.ToString("F1") : "none")} remotes={remotes?.Count} mode={mode} mask={mask} pad={(pad != null ? pad.name : "none")} category={category} camera={(cam != null ? cam.name + " rect=" + cam.rect : "none")}");
            }
            sb.AppendLine($"pads: {string.Join(", ", InputContexts.OrderedPads().Select(p => $"{p.displayName}#{p.deviceId}(x{InputContexts.XInputSlot(p)})"))}");
            return sb.ToString();
        }
    }
}
