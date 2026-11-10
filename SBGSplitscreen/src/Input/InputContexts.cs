using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.InputSystem.Utilities;

namespace SBGSplitscreen
{
    /// <summary>
    /// Every player gets their own copy of the game's controls, bound to their own devices.
    /// Player 1 keeps keyboard, mouse and controller 1; each joined player gets exactly one gamepad.
    /// </summary>
    internal static class InputContexts
    {
        /// <summary>Modes that describe the whole screen rather than one player (loading, pop-ups, overlays).</summary>
        public const InputMode SharedModes = InputMode.ForceDisabled | InputMode.FullScreenMessage
            | InputMode.DevConsole | InputMode.SteamOverlay | InputMode.MainMenu;

        private const string ControlsField = "InputManager.controls";
        private const string ModeMaskField = "InputManager.<CurrentModeMask>k__BackingField";

        private static readonly Regex UserIndexPattern = new Regex("\"userIndex\"\\s*:\\s*(\\d+)");
        private static readonly Dictionary<InputActionAsset, int> contextOfAsset = new Dictionary<InputActionAsset, int>();
        private static InputDevice[] lastPrimaryDevices;

        private static readonly Action<Gamepad> setCurrentGamepad =
            AccessTools.MethodDelegate<Action<Gamepad>>(AccessTools.PropertySetter(typeof(InputManager), nameof(InputManager.CurrentGamepad)));

        /// <summary>Player 1's controller. Other gamepads are free to join.</summary>
        public static Gamepad PrimaryPad { get; private set; }

        public static void RegisterFields()
        {
            var t = typeof(InputManager);
            Contexts.AddStatic(t, "controls");
            Contexts.AddStatic(t, "currentDeviceIconSet");
            Contexts.AddStatic(t, "currentDeviceId", () => -1);
            Contexts.AddStatic(t, "cursorForceLocked");
            Contexts.AddStatic(t, "inputActionsOverride", () => new HashSet<InputAction>());
            Contexts.AddStaticProperty(t, "CurrentDeviceType");
            Contexts.AddStaticProperty(t, "CurrentMode");
            Contexts.AddStaticProperty(t, "CurrentModeMask");
            Contexts.AddStaticProperty(t, "CurrentModeOverrideMask");
            Contexts.AddStaticProperty(t, "CurrentGamepad");
            Contexts.AddStaticEvent(t, "SwitchedToGamepad");
            Contexts.AddStaticEvent(t, "SwitchedToKeyboard");
            Contexts.AddStaticEvent(t, "SwitchedInputDeviceType");
            Contexts.AddStaticEvent(typeof(global::PlayerInput), "SwitchedToGamepad");
            Contexts.AddStaticEvent(typeof(global::PlayerInput), "SwitchedToKeyboard");
            Contexts.AddStaticEvent(typeof(global::PlayerInput), "SwitchedInputDeviceType");
        }

        /// <summary>Gamepads in a stable order: XInput slot first (both pads you plug in are XInput), then connection order.</summary>
        public static List<Gamepad> OrderedPads()
        {
            return Gamepad.all
                .OrderBy(p => XInputSlot(p) >= 0 ? 0 : 1)
                .ThenBy(p => XInputSlot(p))
                .ThenBy(p => p.deviceId)
                .ToList();
        }

        public static int ContextOfAsset(InputActionAsset asset)
        {
            return asset != null && contextOfAsset.TryGetValue(asset, out int c) ? c : -1;
        }

        /// <summary>Gives player <paramref name="context"/> a fresh copy of the controls, bound only to <paramref name="pad"/>.</summary>
        public static void SetupContext(int context, Gamepad pad)
        {
            var primaryControls = (GameControls)Contexts.GetValue(0, ControlsField);
            var controls = new GameControls();
            try
            {
                if (primaryControls != null)
                {
                    controls.LoadBindingOverridesFromJson(primaryControls.SaveBindingOverridesAsJson());
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not copy key bindings to the new player: " + e.Message);
            }
            controls.devices = new ReadOnlyArray<InputDevice>(new InputDevice[] { pad });
            contextOfAsset[controls.asset] = context;

            Contexts.SetValue(context, ControlsField, controls);
            Contexts.SetValue(context, "InputManager.<CurrentGamepad>k__BackingField", pad);
            Contexts.SetValue(context, "InputManager.<CurrentDeviceType>k__BackingField", InputManager.DeviceType.Xbox);
            Contexts.SetValue(context, "InputManager.currentDeviceId", pad.deviceId);
            var iconSets = Traverse.Create(typeof(InputManager)).Field("iconSets").GetValue<InputIconsSets>();
            if (iconSets != null)
            {
                Contexts.SetValue(context, "InputManager.currentDeviceIconSet", iconSets.GetIconSet(InputManager.DeviceType.Xbox));
            }

            // Whatever is covering the whole screen right now (a loading screen, a pop-up) covers this player too.
            var shared = (InputMode)Contexts.GetValue(0, ModeMaskField) & SharedModes;
            int previous = Contexts.Enter(context);
            try
            {
                foreach (InputMode mode in Enum.GetValues(typeof(InputMode)))
                {
                    if (mode != InputMode.None && (shared & mode) == mode)
                    {
                        InputManager.EnableMode(mode);
                    }
                }
            }
            finally
            {
                Contexts.Restore(previous);
            }
        }

        /// <summary>Moves a joined player onto another controller (theirs disconnected).</summary>
        public static void SetPad(int context, Gamepad pad)
        {
            if (Contexts.GetValue(context, ControlsField) is GameControls controls)
            {
                controls.devices = new ReadOnlyArray<InputDevice>(new InputDevice[] { pad });
            }
            Contexts.SetValue(context, "InputManager.<CurrentGamepad>k__BackingField", pad);
            Contexts.SetValue(context, "InputManager.currentDeviceId", pad.deviceId);
        }

        public static void TearDownContext(int context)
        {
            if (Contexts.GetValue(context, ControlsField) is GameControls controls)
            {
                contextOfAsset.Remove(controls.asset);
                controls.Disable();
                controls.Dispose();
            }
        }

        /// <summary>Test-only: drive player 1 with a given (virtual) pad instead of controller 1.</summary>
        public static Gamepad ForcedPrimaryPad;

        /// <summary>Keeps player 1's controls on keyboard, mouse and controller 1 only, so free pads cannot steer player 1.</summary>
        public static void UpdatePrimaryDevices(Func<Gamepad, bool> isClaimedByJoinedPlayer)
        {
            var pads = OrderedPads();
            if (ForcedPrimaryPad != null && ForcedPrimaryPad.added)
            {
                PrimaryPad = ForcedPrimaryPad;
            }
            else if (PrimaryPad == null || !PrimaryPad.added || isClaimedByJoinedPlayer(PrimaryPad))
            {
                PrimaryPad = pads.FirstOrDefault(p => !isClaimedByJoinedPlayer(p));
            }

            // Menus read "the current gamepad" directly (customization tabs on the bumpers and
            // triggers, D-pad navigation). The game starts it on whichever controller was used
            // last, so player 1's could be a joined player's, and their presses then worked player
            // 1's open menu. Player 1's is always player 1's own controller.
            if (Contexts.Current == 0 && PrimaryPad != null && InputManager.CurrentGamepad != PrimaryPad)
            {
                setCurrentGamepad(PrimaryPad);
            }

            var primaryControls = (GameControls)Contexts.GetValue(0, ControlsField);
            if (primaryControls == null)
            {
                return;
            }
            contextOfAsset[primaryControls.asset] = 0;

            var devices = InputSystem.devices.Where(d => !(d is Gamepad) || d == PrimaryPad).ToArray();
            // Unity's UI input module (menu clicks, D-pad navigation of shared menus) has its own
            // actions; without this, any controller could drive a menu player 1 has open.
            var uiModule = EventSystem.current != null ? EventSystem.current.GetComponent<InputSystemUIInputModule>() : null;
            bool devicesChanged = lastPrimaryDevices == null || !lastPrimaryDevices.SequenceEqual(devices);
            if (!devicesChanged && uiModule == lastUiModule)
            {
                return;
            }
            lastPrimaryDevices = devices;
            lastUiModule = uiModule;
            primaryControls.devices = new ReadOnlyArray<InputDevice>(devices);
            if (uiModule != null && uiModule.actionsAsset != null)
            {
                uiModule.actionsAsset.devices = new ReadOnlyArray<InputDevice>(devices);
            }
            if (devicesChanged)
            {
                Plugin.Log.LogInfo($"Player 1 input: {string.Join(", ", devices.Select(d => d.displayName))}");
            }
        }

        private static InputSystemUIInputModule lastUiModule;

        public static int XInputSlot(InputDevice device)
        {
            string caps = device.description.capabilities;
            if (string.IsNullOrEmpty(caps) || device.description.interfaceName != "XInput")
            {
                return -1;
            }
            var match = UserIndexPattern.Match(caps);
            return match.Success ? int.Parse(match.Groups[1].Value) : -1;
        }

        /// <summary>Same action maps the game enables for each mode, minus the parts that touch shared UI and the cursor.</summary>
        public static void ApplyModeForJoinedPlayer()
        {
            var c = InputManager.Controls;
            if (c == null)
            {
                return;
            }
            bool gameplay = false, cartDriver = false, cartShared = false, hotkeys = false, spectate = false,
                ingame = false, camera = false, ui = false;
            switch (InputManager.CurrentMode)
            {
                case InputMode.Regular:
                    gameplay = hotkeys = ingame = camera = true;
                    break;
                case InputMode.GolfCartDriver:
                    cartDriver = cartShared = hotkeys = ingame = camera = true;
                    break;
                case InputMode.GolfCartPassenger:
                    cartShared = hotkeys = ingame = camera = true;
                    break;
                case InputMode.Spectate:
                    spectate = ingame = camera = true;
                    break;
                case InputMode.Paused:
                case InputMode.MatchSetup:
                case InputMode.FullScreenMessage:
                case InputMode.MainMenu:
                case InputMode.TextChat:
                    ui = true;
                    break;
            }
            Set(c.Gameplay, gameplay);
            Set(c.GolfCartDriver, cartDriver);
            Set(c.GolfCartShared, cartShared);
            Set(c.Hotkeys, hotkeys);
            Set(c.Spectate, spectate);
            Set(c.Ingame, ingame);
            Set(c.Camera, camera);
            Set(c.UI, ui);
            // Joined players have no microphone and do not vote.
            c.VoiceChat.Disable();
            c.Vote.Disable();
        }

        private static void Set(InputActionMap map, bool enabled)
        {
            if (enabled)
            {
                map.Enable();
            }
            else
            {
                map.Disable();
            }
        }
    }

    /// <summary>"Last device used" tracking runs for the player whose controls fired, not always player 1.</summary>
    [HarmonyPatch(typeof(InputManager), "OnActionChange")]
    internal static class ActionChangeContextPatch
    {
        private static void Prefix(object obj, out int __state)
        {
            __state = -1;
            if (Contexts.ExtraCount > 0 && obj is InputAction action)
            {
                int context = InputContexts.ContextOfAsset(action.actionMap?.asset);
                if (context >= 0)
                {
                    __state = Contexts.Enter(context);
                }
            }
        }

        private static void Finalizer(int __state) => Contexts.Restore(__state);
    }

    [HarmonyPatch(typeof(InputManager), "ApplyCurrentMode")]
    internal static class JoinedPlayerApplyModePatch
    {
        private static bool Prefix()
        {
            if (Contexts.Current == 0)
            {
                return true;
            }
            InputContexts.ApplyModeForJoinedPlayer();
            return false;
        }
    }

    /// <summary>
    /// "Last device used" for player 1 only follows player 1's own controller (and keyboard and
    /// mouse); another player's controller never becomes player 1's current gamepad.
    /// </summary>
    [HarmonyPatch(typeof(InputManager), "RefreshCurrentDeviceType")]
    internal static class PlayerOneKeepsOwnPadPatch
    {
        private static bool Prefix(InputDevice device)
        {
            return !(Contexts.Current == 0 && device is Gamepad pad && InputContexts.PrimaryPad != null && pad != InputContexts.PrimaryPad);
        }
    }

    /// <summary>The mouse cursor belongs to player 1.</summary>
    [HarmonyPatch(typeof(InputManager), "UpdateCursorLock")]
    internal static class CursorBelongsToPlayerOnePatch
    {
        private static bool Prefix() => Contexts.Current == 0;
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.EnableMode))]
    internal static class SharedModeEnablePatch
    {
        private static void Postfix(InputMode mode) => SharedModePropagation.Propagate(mode, enable: true);
    }

    [HarmonyPatch(typeof(InputManager), nameof(InputManager.DisableMode))]
    internal static class SharedModeDisablePatch
    {
        private static void Postfix(InputMode mode) => SharedModePropagation.Propagate(mode, enable: false);
    }

    internal static class SharedModePropagation
    {
        public static void Propagate(InputMode mode, bool enable)
        {
            InputMode shared = mode & InputContexts.SharedModes;
            if (shared == InputMode.None || Contexts.Current != 0 || Contexts.ExtraCount == 0)
            {
                return;
            }
            for (int c = 1; c < Contexts.MaxPlayers; c++)
            {
                if (!Contexts.IsActive(c))
                {
                    continue;
                }
                int previous = Contexts.Enter(c);
                try
                {
                    if (enable)
                    {
                        InputManager.EnableMode(shared);
                    }
                    else
                    {
                        InputManager.DisableMode(shared);
                    }
                }
                finally
                {
                    Contexts.Restore(previous);
                }
            }
        }
    }
}
