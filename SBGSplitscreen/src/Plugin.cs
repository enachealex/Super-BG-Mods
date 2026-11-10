using System;
using BepInEx;
using BepInEx.Logging;
using FMODUnity;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace SBGSplitscreen
{
    /// <summary>
    /// Local splitscreen in a single copy of the game. Player 1 plays as normal on keyboard, mouse
    /// and controller 1; once player 1 is hosting a game, any other controller presses A to join
    /// with their own character, camera and controls (up to four players).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.sbg.splitscreen";
        public const string Name = "SBG Splitscreen";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;

        private static float lastErrorTime = -100f;

        private void Awake()
        {
            Log = Logger;
            Settings.Load(Config);
            var harmony = new Harmony(Guid);
            MonitorSetting.Init(harmony);
            if (!Settings.Enabled)
            {
                Log.LogInfo("Splitscreen is disabled in config.");
                return;
            }
            Splitscreen.Init(harmony);
            harmony.PatchAll(typeof(Plugin).Assembly);
            if (Settings.DebugMode)
            {
                DebugCommands.Init();
            }
            Log.LogInfo($"Ready: up to {Settings.MaxPlayers} players.");
        }

        private void Update()
        {
            Guard(MonitorSetting.Update, "monitor");
            Guard(ItemTweaks.Update, "items");
            if (!Settings.Enabled)
            {
                return;
            }
            if (Contexts.Current != 0)
            {
                // Something switched contexts without switching back; never let that leak into the next frame.
                Log.LogWarning($"Context {Contexts.Current} was still live at frame start; restoring player 1.");
                Contexts.Switch(0);
            }
            Guard(FocusGuard.Update, "focus");
            Guard(WindowLayout.Update, "window");
            Guard(Splitscreen.Update, "splitscreen");
            if (Settings.MuteAll && RuntimeManager.IsInitialized)
            {
                Guard(() => RuntimeManager.MuteAllEvents(true), "mute");
            }
            if (Settings.NoFocus)
            {
                Guard(KeepInputWithoutFocus, "input");
            }
            if (Settings.DebugMode)
            {
                Guard(DebugCommands.Update, "debug");
            }
        }

        private void OnGUI()
        {
            if (Settings.Enabled)
            {
                JoinPrompt.Draw();
                ControllerOverlay.DrawAll();
            }
        }

        /// <summary>
        /// Unattended test runs never have focus, and Unity drops controller input from unfocused
        /// windows by default. They also must not react to the real controllers, keyboard and mouse,
        /// which belong to whoever is using the PC (maybe playing their own copy of the game), so
        /// only the test's virtual gamepads stay switched on. When you play, none of this runs.
        /// </summary>
        private static void KeepInputWithoutFocus()
        {
            var settings = InputSystem.settings;
            if (settings.backgroundBehavior != InputSettings.BackgroundBehavior.IgnoreFocus)
            {
                settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            }
            foreach (var device in InputSystem.devices)
            {
                bool virtualPad = device.name.StartsWith("SplitscreenTestPad");
                if (virtualPad && !device.enabled)
                {
                    InputSystem.EnableDevice(device);
                }
                else if (!virtualPad && device.enabled && (device is Gamepad || device is Keyboard || device is Mouse))
                {
                    InputSystem.DisableDevice(device);
                }
            }
        }

        private static void Guard(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                if (Time.unscaledTime - lastErrorTime > 5f)
                {
                    lastErrorTime = Time.unscaledTime;
                    Log.LogError($"[{what}] {e}");
                }
            }
        }
    }
}
