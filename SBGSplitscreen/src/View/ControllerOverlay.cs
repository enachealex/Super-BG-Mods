using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>A notice in a joined player's part of the screen while their controller is disconnected.</summary>
    internal static class ControllerOverlay
    {
        private static GUIStyle titleStyle;
        private static GUIStyle lineStyle;

        public static void DrawAll()
        {
            var order = PlayerViews.ActiveOrder();
            for (int i = 0; i < order.Count; i++)
            {
                int slot = order[i];
                if (slot > 0 && Splitscreen.IsWaitingForController(slot))
                {
                    Draw(slot, PlayerViews.ViewportFor(i, order.Count));
                }
            }
        }

        private static void Draw(int slot, Rect viewport)
        {
            EnsureStyles();
            // Viewport rects count from the bottom of the screen; GUI coordinates from the top.
            var area = new Rect(viewport.x * Screen.width, (1f - viewport.yMax) * Screen.height,
                viewport.width * Screen.width, viewport.height * Screen.height);
            var panel = new Rect(area.center.x - 260f, area.center.y - 55f, 520f, 110f);
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.8f);
            GUI.DrawTexture(panel, Texture2D.whiteTexture);
            GUI.color = old;
            GUI.Label(new Rect(panel.x, panel.y + 12f, panel.width, 34f), $"{Settings.PlayerName(slot)}: controller disconnected", titleStyle);
            GUI.Label(new Rect(panel.x, panel.y + 60f, panel.width, 26f), "Reconnect it, or press A on any free controller", lineStyle);
        }

        private static void EnsureStyles()
        {
            if (titleStyle != null)
            {
                return;
            }
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            titleStyle.normal.textColor = Color.white;
            lineStyle = new GUIStyle(GUI.skin.label) { fontSize = 18, alignment = TextAnchor.MiddleCenter };
            lineStyle.normal.textColor = Color.white;
        }
    }
}
