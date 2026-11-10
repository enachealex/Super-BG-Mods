using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine.InputSystem;
using UnityEngine.Localization.Components;

namespace SBGSplitscreen
{
    /// <summary>
    /// Button hints ("[LT] Swing Stance [HOLD]") are a translated label with the button's icon put
    /// in front of it. Every hint with the same wording shares one translated-string object, and
    /// refreshing one hint rewrites the label of every other hint using that object, without its
    /// icon. With one player that never shows; in splitscreen, whoever's hint refreshed last kept
    /// the icon (usually player 1) and everyone else saw bare text. Each hint now remembers its own
    /// icon, worked out for the player whose screen it is on, and puts it back whenever its label
    /// is rewritten.
    /// </summary>
    internal static class ButtonPromptIcons
    {
        private sealed class Icon
        {
            public string Value;
        }

        private static readonly ConditionalWeakTable<ButtonPrompt, Icon> icons = new ConditionalWeakTable<ButtonPrompt, Icon>();
        private static readonly ConditionalWeakTable<LocalizeStringEvent, ButtonPrompt> promptOfLabel = new ConditionalWeakTable<LocalizeStringEvent, ButtonPrompt>();
        private static readonly AccessTools.FieldRef<ButtonPrompt, InputAction> inputAction =
            AccessTools.FieldRefAccess<ButtonPrompt, InputAction>("inputAction");

        private static void Decorate(ButtonPrompt prompt)
        {
            if (!icons.TryGetValue(prompt, out var icon) || prompt.labelObj == null)
            {
                return;
            }
            string text = prompt.labelObj.text ?? string.Empty;
            switch (prompt.alignment)
            {
                case ButtonPrompt.IconAlignment.Left:
                    if (!text.StartsWith(icon.Value))
                    {
                        prompt.labelObj.text = icon.Value + text;
                    }
                    break;
                case ButtonPrompt.IconAlignment.Top:
                    if (!text.StartsWith(icon.Value + "\n"))
                    {
                        prompt.labelObj.text = icon.Value + "\n" + text;
                    }
                    break;
                default:
                    if (!text.EndsWith(icon.Value))
                    {
                        prompt.labelObj.text = text + icon.Value;
                    }
                    break;
            }
        }

        [HarmonyPatch(typeof(ButtonPrompt), nameof(ButtonPrompt.UpdatePrompt))]
        private static class UpdatePromptPatch
        {
            private static bool Prefix(ButtonPrompt __instance)
            {
                icons.Remove(__instance);
                if (Contexts.ExtraCount == 0)
                {
                    return true;
                }
                int previous = Contexts.EnterFor(__instance);
                try
                {
                    // Same icon text the game builds, from this hint's player's controls and controller type.
                    string icon = string.Concat($"<size={__instance.iconSize}px><voffset={__instance.verticalOffset}px>"
                        + InputManager.GetCompositeInputIconRichTextTags(inputAction(__instance), string.Empty, canMerge: true), "</voffset></size>");
                    icons.Add(__instance, new Icon { Value = icon });
                    if (__instance.label != null)
                    {
                        promptOfLabel.Remove(__instance.label);
                        promptOfLabel.Add(__instance.label, __instance);
                        __instance.label.RefreshString();
                    }
                    // In case the label was not rewritten just now (its text not loaded yet).
                    Decorate(__instance);
                }
                finally
                {
                    Contexts.Restore(previous);
                }
                return false;
            }
        }

        /// <summary>A hint's label was rewritten, possibly by another player's hint: put its icon back.</summary>
        [HarmonyPatch(typeof(LocalizeStringEvent), "UpdateString")]
        private static class LabelRewrittenPatch
        {
            private static void Postfix(LocalizeStringEvent __instance)
            {
                if (Contexts.ExtraCount > 0 && promptOfLabel.TryGetValue(__instance, out var prompt) && prompt != null)
                {
                    Decorate(prompt);
                }
            }
        }
    }
}
