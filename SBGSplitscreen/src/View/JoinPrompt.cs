using System.Linq;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// "Press A to join" while a free controller could join: in the empty quarter when three
    /// people play, otherwise at the top centre. Hidden while anyone has a menu open, so it never
    /// covers a menu header.
    /// </summary>
    internal static class JoinPrompt
    {
        private const string ModeMaskField = "InputManager.<CurrentModeMask>k__BackingField";

        private static GUIStyle style;

        public static void Draw()
        {
            if (!Splitscreen.CanJoin() || !Splitscreen.FreePads().Any() || AnyMenuOpen())
            {
                return;
            }
            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                style.normal.textColor = Color.white;
            }
            var content = new GUIContent($"Player {Splitscreen.JoinedCount + 2}: press A to join");
            Vector2 size = style.CalcSize(content) + new Vector2(24f, 10f);
            Vector2 center = PlayerViews.ActiveOrder().Count == 3
                ? new Vector2(Screen.width * 0.75f, Screen.height * 0.75f)
                : new Vector2(Screen.width * 0.5f, 8f + size.y / 2f);
            var rect = new Rect(center - size / 2f, size);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.65f);
            GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(rect, content, style);
        }

        private static bool AnyMenuOpen()
        {
            for (int c = 0; c < Contexts.MaxPlayers; c++)
            {
                if (Contexts.IsActive(c) && Contexts.GetValue(c, ModeMaskField) is InputMode mask
                    && (mask & (InputMode.Paused | InputMode.MatchSetup | InputMode.TextChat)) != 0)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
