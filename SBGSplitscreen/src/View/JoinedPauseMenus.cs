using System;
using Game.Rendering.Blur;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SBGSplitscreen
{
    /// <summary>
    /// Every player has the game's own pause menu, in their part of the screen (it is one of the
    /// per-player HUD parts). For players 2-4, "Exit Game" becomes "Leave Game", which removes just
    /// that player, and the host's buttons (invite, match setup) and the shared settings are hidden.
    /// Pausing never stops the game (it is an online game underneath), so the blur and muffled
    /// sound only apply to whoever is actually looking at a pause menu.
    /// </summary>
    internal static class JoinedPauseMenus
    {
        private static readonly AccessTools.FieldRef<PauseMenu, bool> isPaused = AccessTools.FieldRefAccess<PauseMenu, bool>("isPaused");
        private static readonly Action<PauseMenu> updateButtonOffsets = AccessTools.MethodDelegate<Action<PauseMenu>>(AccessTools.Method(typeof(PauseMenu), "UpdateButtonOffsets"));
        private static bool logged;

        /// <summary>Runs on a joined player's HUD copy while it is still asleep.</summary>
        public static void Customize(GameObject hud, int slot)
        {
            var menu = hud.GetComponentInChildren<PauseMenu>(true);
            if (menu == null)
            {
                return;
            }
            if (!logged)
            {
                logged = true;
                foreach (var b in menu.menuButtonContainer.GetComponentsInChildren<Button>(true))
                {
                    var calls = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < b.onClick.GetPersistentEventCount(); i++)
                    {
                        calls.Add($"{b.onClick.GetPersistentTarget(i)?.name}.{b.onClick.GetPersistentMethodName(i)}");
                    }
                    Plugin.Log.LogInfo($"Pause menu button '{b.name}' [{b.GetComponentInChildren<TMP_Text>(true)?.text}] -> {string.Join(", ", calls)}");
                }
            }

            var exit = menu.exitGameButton;
            if (exit != null)
            {
                exit.onClick = new Button.ButtonClickedEvent();
                exit.onClick.AddListener(() => Splitscreen.RequestLeave(slot));
                foreach (var component in exit.GetComponentsInChildren<Component>(true))
                {
                    // The label is normally filled in by the localization system, which would put "Exit Game" back.
                    if (component != null && component.GetType().Name.Contains("Locali"))
                    {
                        Object.DestroyImmediate(component);
                    }
                }
                var label = exit.GetComponentInChildren<TMP_Text>(true);
                if (label != null)
                {
                    label.text = "Leave Game";
                }
            }

            HideHostButtons(menu);

            // On waking, the menu makes its item-chance icons and rule boxes from templates and then
            // hides the templates. This copy was taken from player 1's menu after it woke, so its
            // templates are hidden already and everything made from them would start hidden too
            // (the joined player's menu showed "Item probability" with no items under it).
            if (menu.itemProbabilityTemplate != null)
            {
                menu.itemProbabilityTemplate.SetActive(true);
            }
            if (menu.rulesTemplate != null)
            {
                menu.rulesTemplate.SetActive(true);
            }
        }

        /// <summary>
        /// Hides the host's buttons and the shared settings, then lets the menu re-link its buttons
        /// for controllers. The links go to the nearest visible button above and below; linked while
        /// a button was still showing, pressing down on Resume went to a hidden button and nowhere,
        /// so a controller could never reach Leave Game.
        /// </summary>
        public static void HideHostButtons(PauseMenu menu)
        {
            foreach (var button in menu.menuButtonContainer.GetComponentsInChildren<Button>(true))
            {
                if (button != menu.exitGameButton && button != menu.resumeButton && IsHostOrSharedButton(button, menu))
                {
                    button.gameObject.SetActive(false);
                }
            }
            if (menu.inviteButton != null)
            {
                menu.inviteButton.SetActive(false);
            }
            if (Traverse.Create(menu).Field("allButtons").GetValue() != null)
            {
                updateButtonOffsets(menu);
            }
        }

        private static bool IsHostOrSharedButton(Button button, PauseMenu menu)
        {
            for (int i = 0; i < button.onClick.GetPersistentEventCount(); i++)
            {
                string method = button.onClick.GetPersistentMethodName(i);
                var target = button.onClick.GetPersistentTarget(i);
                var targetObject = target is Component c ? c.gameObject : target as GameObject;
                if (method == nameof(PauseMenu.OpenMatchSetup) || method == nameof(PauseMenu.InviteFriend)
                    || (targetObject != null && targetObject == menu.settingsMenu))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool IsPaused(int context)
        {
            return Contexts.GetValue(context, Contexts.SingletonField(typeof(PauseMenu))) is PauseMenu menu && menu != null && isPaused(menu);
        }
    }

    /// <summary>Joined players never see the invite button (only the host can invite).</summary>
    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.UpdateInviteButtonEnabled))]
    internal static class JoinedInviteButtonPatch
    {
        private static void Postfix(PauseMenu __instance)
        {
            if (Owners.Of(__instance) > 0)
            {
                JoinedPauseMenus.HideHostButtons(__instance);
            }
        }
    }

    /// <summary>Each time a joined player's pause menu opens, its buttons are as they should be and linked for the controller.</summary>
    [HarmonyPatch(typeof(PauseMenu), "PauseInternal")]
    internal static class JoinedPauseOpensPatch
    {
        private static void Postfix(PauseMenu __instance)
        {
            if (Owners.Of(__instance) > 0)
            {
                JoinedPauseMenus.HideHostButtons(__instance);
            }
        }
    }

    /// <summary>
    /// A leaving player's pause menu still hears about the player list changing as they go, and
    /// listing players needs that player, who is already gone. Nothing to list for them any more.
    /// </summary>
    [HarmonyPatch(typeof(PauseMenu), nameof(PauseMenu.UpdatePlayerEntries))]
    internal static class LeavingPlayerEntriesPatch
    {
        private static bool Prefix(PauseMenu __instance)
        {
            int owner = Owners.Of(__instance);
            if (owner <= 0)
            {
                return true;
            }
            var player = Contexts.IsActive(owner) ? Contexts.GetValue(owner, "GameManager.localPlayerInfo") as PlayerInfo : null;
            return player != null;
        }
    }

    /// <summary>With several players, pausing must not muffle the sound for the ones still playing.</summary>
    [HarmonyPatch(typeof(PauseMenu), "PauseInternal")]
    internal static class PauseSoundPatch
    {
        private static void Postfix(PauseMenu __instance)
        {
            if (Contexts.ExtraCount == 0)
            {
                return;
            }
            var snapshot = Traverse.Create(__instance).Field("pausedSnapshot").GetValue<FMOD.Studio.EventInstance>();
            if (snapshot.isValid())
            {
                snapshot.stop(FMOD.Studio.STOP_MODE.IMMEDIATE);
            }
        }
    }
}
