using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Localization.Components;
using UnityEngine.UI;

namespace SBGSplitscreen
{
    /// <summary>
    /// Settings > Graphics > Monitor: which screen the game is shown on. The game has no such
    /// choice, so moving it to another monitor meant switching to windowed, dragging the window
    /// across, going fullscreen again and restarting. Picking a monitor here moves the game there
    /// at once, fullscreen or windowed, and it opens there from then on. Independent of
    /// splitscreen; it works with splitscreen switched off too.
    /// </summary>
    internal static class MonitorSetting
    {
        private const string RowName = "Monitor (mod)";
        private const string Tooltip = "The screen the game is shown on. Changes right away.";

        private static readonly List<DisplayInfo> displays = new List<DisplayInfo>();
        private static AsyncOperation move;
        private static DisplayInfo moveFrom;
        private static DisplayInfo moveTo;
        private static bool startupPending;

        private static readonly AccessTools.FieldRef<GameSettings.GraphicsSettings, int> screenWidth =
            AccessTools.FieldRefAccess<GameSettings.GraphicsSettings, int>("screenWidth");
        private static readonly AccessTools.FieldRef<GameSettings.GraphicsSettings, int> screenHeight =
            AccessTools.FieldRefAccess<GameSettings.GraphicsSettings, int>("screenHeight");
        private static readonly AccessTools.FieldRef<DropdownOption, LocalizeDropdown> localized =
            AccessTools.FieldRefAccess<DropdownOption, LocalizeDropdown>("localized");

        public static void Init(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(SettingsMenu), "Start"),
                postfix: new HarmonyMethod(typeof(MonitorSetting), nameof(AddRow)));
            harmony.Patch(AccessTools.Method(typeof(SettingsMenu), "OnEnable"),
                postfix: new HarmonyMethod(typeof(MonitorSetting), nameof(RefreshRow)));
            harmony.Patch(AccessTools.Method(typeof(GameSettings.GraphicsSettings), nameof(GameSettings.GraphicsSettings.Initialize)),
                postfix: new HarmonyMethod(typeof(MonitorSetting), nameof(AfterGraphicsInitialized)));
        }

        /// <summary>Called every frame, whether or not splitscreen is on.</summary>
        public static void Update()
        {
            if (move != null)
            {
                if (!move.isDone)
                {
                    return;
                }
                move = null;
                FinishMove();
            }
            // The game has just applied its saved resolution: if a monitor was picked, go there.
            // Test runs keep their own window where it is.
            if (startupPending && Time.frameCount > 2)
            {
                startupPending = false;
                Screen.GetDisplayLayout(displays);
                Plugin.Log.LogInfo("Monitors: " + string.Join(", ", displays.Select((d, i) => $"{Label(i)} {d.width}x{d.height}{(IsCurrent(d) ? " (game is here)" : "")}")));
                int target = Find(Settings.Monitor.Value);
                if (target >= 0 && !IsCurrent(displays[target]) && Settings.TestWindow == null)
                {
                    BeginMove(displays[target]);
                }
            }
        }

        private static void AfterGraphicsInitialized()
        {
            startupPending = true;
        }

        private static int Find(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return -1;
            }
            Screen.GetDisplayLayout(displays);
            return displays.FindIndex(d => d.name == name.Trim());
        }

        private static bool IsCurrent(DisplayInfo display)
        {
            return Screen.mainWindowDisplayInfo.Equals(display);
        }

        private static string Label(int index)
        {
            var d = displays[index];
            string name = string.IsNullOrWhiteSpace(d.name) ? $"Monitor {index + 1}" : d.name;
            // Two of the same model: tell them apart.
            if (displays.Count(o => o.name == d.name) > 1)
            {
                name += $" #{displays.Take(index + 1).Count(o => o.name == d.name)}";
            }
            return name;
        }

        /// <summary>Adds the Monitor row under Fullscreen, made from a copy of the Fullscreen row.</summary>
        private static void AddRow(SettingsMenu __instance)
        {
            var fullscreen = __instance.fullscreen;
            if (fullscreen == null)
            {
                return;
            }
            var box = fullscreen.transform.parent;
            if (box.Find(RowName) == null)
            {
                var row = Object.Instantiate(fullscreen.gameObject, box, false);
                row.name = RowName;
                row.transform.SetSiblingIndex(fullscreen.transform.GetSiblingIndex() + 1);

                // Plain text: the game's translations would put the Fullscreen wording back.
                foreach (var translation in row.GetComponentsInChildren<LocalizeStringEvent>(true))
                {
                    Object.DestroyImmediate(translation);
                }
                var option = row.GetComponent<DropdownOption>();
                if (option.Localized != null)
                {
                    Object.DestroyImmediate(option.Localized);
                }
                localized(option) = null;
                var label = row.transform.Find("Label Text")?.GetComponent<TMP_Text>();
                if (label != null)
                {
                    label.text = "Monitor";
                }

                // The box around the screen options is sized for its rows; make room for one more.
                var size = box.GetComponent<LayoutElement>();
                if (size != null)
                {
                    float extra = ((RectTransform)fullscreen.transform).rect.height + (box.GetComponent<VerticalLayoutGroup>()?.spacing ?? 0f);
                    if (size.preferredHeight > 0f)
                    {
                        size.preferredHeight += extra;
                    }
                    if (size.minHeight > 0f)
                    {
                        size.minHeight += extra;
                    }
                }
                // Controller navigation is worked out from the rows; it only notices direct children on its own.
                __instance.graphicsNavigationGroup?.UpdateNavigation();
                __instance.graphicsTooltip?.RegisterTooltip(row.GetComponent<RectTransform>(), Tooltip);
            }
            RefreshRow(__instance);
        }

        /// <summary>Lists the monitors (they can change while the game runs) and selects the one the game is on.</summary>
        private static void RefreshRow(SettingsMenu __instance)
        {
            var option = __instance.fullscreen?.transform.parent.Find(RowName)?.GetComponent<DropdownOption>();
            if (option == null)
            {
                return;
            }
            Screen.GetDisplayLayout(displays);
            option.SetOptions(Enumerable.Range(0, displays.Count).Select(Label).ToList());
            option.Initialize(() => OnPicked(option));
            option.valueWithoutNotify = Mathf.Max(0, displays.FindIndex(IsCurrent));
            option.Interactable = displays.Count > 1;
        }

        private static void OnPicked(DropdownOption option)
        {
            Screen.GetDisplayLayout(displays);
            int index = option.value;
            if (index < 0 || index >= displays.Count)
            {
                return;
            }
            var target = displays[index];
            Settings.Monitor.Value = target.name;
            if (!IsCurrent(target) && move == null)
            {
                BeginMove(target);
            }
        }

        private static void BeginMove(DisplayInfo target)
        {
            moveFrom = Screen.mainWindowDisplayInfo;
            moveTo = target;
            // Fullscreen snaps to the new monitor and fills it; a window lands in its middle.
            var position = Vector2Int.zero;
            if (Screen.fullScreenMode == FullScreenMode.Windowed)
            {
                position = new Vector2Int(Mathf.Max(0, (target.width - Screen.width) / 2), Mathf.Max(0, (target.height - Screen.height) / 2));
            }
            Plugin.Log.LogInfo($"Moving the game to {target.name} ({target.width}x{target.height}).");
            move = Screen.MoveMainWindowTo(in target, position);
        }

        /// <summary>
        /// The game's resolution list belongs to the monitor it started on: rebuild it for the new
        /// one. In fullscreen, a game that was at its old monitor's full resolution goes to the new
        /// monitor's full resolution; a chosen lower resolution is kept where the new monitor has it.
        /// </summary>
        private static void FinishMove()
        {
            var graphics = GameSettings.All?.Graphics;
            if (graphics == null)
            {
                return;
            }
            var list = graphics.supportedResolutions;
            list.Clear();
            foreach (var r in Screen.resolutions)
            {
                var item = new Vector2Int(r.width, r.height);
                if (!list.Contains(item) && r.width >= r.height)
                {
                    list.Add(item);
                }
            }
            list.Reverse();

            if (graphics.Fullscreen)
            {
                var stored = new Vector2Int(screenWidth(graphics), screenHeight(graphics));
                var oldFull = new Vector2Int(moveFrom.width, moveFrom.height);
                var newFull = new Vector2Int(moveTo.width, moveTo.height);
                var size = stored.x <= 0 || stored == oldFull || !list.Contains(stored) ? newFull : stored;
                screenWidth(graphics) = size.x;
                screenHeight(graphics) = size.y;
                graphics.Apply();
            }
            GameSettings.SaveWithoutApplying();
            // Unity's own memory of which monitor to open on, so next time the game starts there
            // instead of appearing elsewhere and then moving.
            Screen.GetDisplayLayout(displays);
            int index = displays.FindIndex(IsCurrent);
            if (index >= 0)
            {
                PlayerPrefs.SetInt("UnitySelectMonitor", index);
                PlayerPrefs.Save();
            }
            Plugin.Log.LogInfo($"Game is on {Screen.mainWindowDisplayInfo.name}, {Screen.width}x{Screen.height} {Screen.fullScreenMode}.");

            foreach (var menu in Object.FindObjectsByType<SettingsMenu>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (menu.resolutions != null)
                {
                    menu.resolutions.SetOptions(list.Select(v => $"{v.x}x{v.y}").ToList());
                    menu.resolutions.valueWithoutNotify = graphics.GetCurrentResolutionIndex();
                }
                RefreshRow(menu);
            }
        }
    }
}
