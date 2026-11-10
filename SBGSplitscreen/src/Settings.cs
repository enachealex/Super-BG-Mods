using System;
using System.Collections;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace SBGSplitscreen
{
    internal enum TwoPlayerLayout
    {
        TopBottom,
        SideBySide
    }

    /// <summary>Preferences from BepInEx/config/local.sbg.splitscreen.cfg, plus test-only command-line switches.</summary>
    internal static class Settings
    {
        public static bool Enabled;
        public static int MaxPlayers;
        public static TwoPlayerLayout Layout;
        public static ConfigEntry<string> Monitor;
        public static ConfigEntry<int> PistolSpread;
        public static ConfigEntry<int> PistolHitArea;
        private static string[] playerNames;

        // Test-only switches, used when Claude runs the game unattended.
        public static bool DebugMode;
        public static bool MuteAll;
        public static bool NoFocus;
        public static RectInt? TestWindow;

        public static void Load(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Let extra controllers join by pressing A while you are in a game you host.").Value;
            MaxPlayers = Mathf.Clamp(config.Bind("General", "MaxPlayers", 4,
                "Most players on this PC, including player 1 (2-4).").Value, 2, Contexts.MaxPlayers);
            Layout = config.Bind("Layout", "TwoPlayers", TwoPlayerLayout.TopBottom,
                "With two players: TopBottom = player 1 on top, SideBySide = player 1 on the left. Three or four players always use quarters.").Value;
            playerNames = new string[Contexts.MaxPlayers];
            for (int i = 1; i < Contexts.MaxPlayers; i++)
            {
                playerNames[i] = config.Bind("Players", $"Player{i + 1}Name", $"Player {i + 1}",
                    $"Name shown for player {i + 1}.").Value;
            }
            Monitor = config.Bind("Display", "Monitor", "",
                "The screen the game is shown on, by name (for example 24G1WG4). Easiest to pick in the game: Settings > Graphics > Monitor. Leave empty to let Windows decide.");
            PistolSpread = config.Bind("Items", "PistolSpread", 30,
                "In games you host: how far dueling pistol shots can stray from where you aim, as a percentage of the game's normal spread (100 = the game's own, lower = more accurate, 0 = dead-on). Applies to everyone playing on this PC. Not used when you join someone else's game, and people playing online from their own PC keep their game's spread.");
            PistolHitArea = config.Bind("Items", "PistolHitArea", 25,
                "In games you host: how much room around a player still counts as a dueling pistol hit. The game counts shots up to about half a metre beside someone; 100 = the game's own, 0 = only shots that touch their body. Applies to everyone in your game, including people playing online. Not used when you join someone else's game.");
            DropLeftovers(config);

            DebugMode = HasArg("--sbg-debug");
            MuteAll = HasArg("--sbg-mute");
            NoFocus = HasArg("--sbg-no-focus");
            TestWindow = ParseRect(ArgValue("--sbg-window"));
        }

        /// <summary>
        /// Settings from earlier versions of the mod that no longer exist. BepInEx keeps any entry it
        /// does not recognise and writes it back on every save, so they would never go away by themselves.
        /// </summary>
        private static void DropLeftovers(ConfigFile config)
        {
            if (AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(config) is IDictionary leftovers && leftovers.Count > 0)
            {
                leftovers.Clear();
                config.Save();
            }
        }

        public static string PlayerName(int slot)
        {
            string name = slot > 0 && slot < playerNames.Length ? playerNames[slot] : null;
            return string.IsNullOrWhiteSpace(name) ? $"Player {slot + 1}" : name.Trim();
        }

        private static RectInt? ParseRect(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }
            string[] parts = value.Split(',');
            if (parts.Length == 4 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)
                && int.TryParse(parts[2], out int w) && int.TryParse(parts[3], out int h))
            {
                return new RectInt(x, y, w, h);
            }
            return null;
        }

        private static string ArgValue(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return i + 1 < args.Length ? args[i + 1] : null;
                }
                if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                {
                    return args[i].Substring(name.Length + 1);
                }
            }
            return null;
        }

        private static bool HasArg(string name)
        {
            foreach (string a in Environment.GetCommandLineArgs())
            {
                if (a.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
