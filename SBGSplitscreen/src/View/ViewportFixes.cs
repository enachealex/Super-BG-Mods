using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// World-anchored UI (ball and flag markers, name tags, text popups, the giant-form timer) asks
    /// "is this screen point inside the camera?" by comparing it with 0..pixelWidth and
    /// 0..pixelHeight, which only holds for a camera that starts at the window's bottom-left
    /// corner. Screen points are measured across the whole window, so for any view that is not
    /// bottom-left (player 1 on top, the right-hand quarters) everything looked off-screen and was
    /// either hidden or pushed into another player's part of the screen. Measure from the view's
    /// own corner instead.
    /// </summary>
    internal static class ViewportFixes
    {
        public static void Init(Harmony harmony)
        {
            var shiftIn = new HarmonyMethod(AccessTools.Method(typeof(ViewportFixes), nameof(ToViewportPixels)));
            var shiftOut = new HarmonyMethod(AccessTools.Method(typeof(ViewportFixes), nameof(ToScreenPixels)));
            const BindingFlags all = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            foreach (var type in new[] { typeof(WorldspaceIconUi), typeof(NameTagUi), typeof(TextPopupUi), typeof(JumboBurgerGiantFormTimerUi) })
            {
                foreach (var method in type.GetMethods(all))
                {
                    // Local functions compile to methods named like "<LateUpdate>g__IsScreenPointVisible|18_4".
                    if (method.Name.Contains("IsScreenPointVisible") && method.GetParameters().Any(p => p.Name == "screenPoint"))
                    {
                        harmony.Patch(method, prefix: shiftIn);
                    }
                    else if (method.Name.Contains("GetOffscreenPosition") && method.ReturnType == typeof(Vector3))
                    {
                        harmony.Patch(method, postfix: shiftOut);
                    }
                }
            }
        }

        private static Vector2 ViewOrigin()
        {
            var camera = GameManager.Camera;
            return camera != null ? camera.pixelRect.min : Vector2.zero;
        }

        /// <summary>Window pixels to pixels inside the live player's view, before the 0..pixelWidth test.</summary>
        private static void ToViewportPixels(ref Vector2 screenPoint)
        {
            screenPoint -= ViewOrigin();
        }

        /// <summary>The off-screen marker position is computed inside the view; put it back on the window.</summary>
        private static void ToScreenPixels(ref Vector3 __result)
        {
            Vector2 origin = ViewOrigin();
            __result += new Vector3(origin.x, origin.y, 0f);
        }
    }
}
