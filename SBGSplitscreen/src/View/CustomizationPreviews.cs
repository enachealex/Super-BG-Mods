using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// The customization menu shows a 3D preview of the character, filmed by its own camera into a
    /// render texture. Each player's menu copy needs its own preview set, placed far enough from the
    /// others that no preview camera films another player's model.
    /// </summary>
    internal static class CustomizationPreviews
    {
        private const float Spacing = 5000f;

        private static readonly GameObject[] stageCopies = new GameObject[Contexts.MaxPlayers];
        private static bool logged;

        /// <summary>Runs on a joined player's HUD copy while it is still asleep.</summary>
        public static void Separate(GameObject hud, int context)
        {
            var preview = hud.GetComponentInChildren<CustomizationMenuPreview>(true);
            if (preview == null)
            {
                return;
            }
            var fields = Traverse.Create(preview);
            var camera = fields.Field("previewCamera").GetValue<Camera>();
            var modelRoot = fields.Field("playerModelRoot").GetValue<Transform>();
            var vfxRoot = fields.Field("scoreVfxRoot").GetValue<Transform>();
            if (camera == null || modelRoot == null)
            {
                return;
            }
            Vector3 offset = Vector3.down * Spacing * context;
            bool inCopy = camera.transform.IsChildOf(hud.transform) && modelRoot.IsChildOf(hud.transform);
            if (!logged)
            {
                logged = true;
                Plugin.Log.LogInfo($"Customization preview: camera {PathOf(camera.transform)}, model {PathOf(modelRoot)} at {modelRoot.position}, {(inCopy ? "inside the menu" : "in the scene")}");
            }

            if (inCopy)
            {
                // The copy brought its own camera and stage; move them away from player 1's.
                foreach (var t in Outermost(camera.transform, modelRoot, vfxRoot))
                {
                    t.position += offset;
                }
                return;
            }

            // The menu films a stage that lives in the scene: give this player a copy of the stage.
            var stage = CommonAncestor(camera.transform, modelRoot);
            if (stage == null)
            {
                Plugin.Log.LogWarning("Customization preview camera and model have no common parent; player previews may overlap.");
                return;
            }
            var copy = Object.Instantiate(stage.gameObject, stage.parent);
            copy.name = $"{stage.name} (Splitscreen P{context + 1})";
            copy.transform.position += offset;
            fields.Field("previewCamera").SetValue(Mirror(camera.transform, stage, copy.transform).GetComponent<Camera>());
            fields.Field("playerModelRoot").SetValue(Mirror(modelRoot, stage, copy.transform));
            if (vfxRoot != null && vfxRoot.IsChildOf(stage))
            {
                fields.Field("scoreVfxRoot").SetValue(Mirror(vfxRoot, stage, copy.transform));
            }
            stageCopies[context] = copy;
        }

        public static void Release(int context)
        {
            if (stageCopies[context] != null)
            {
                Object.Destroy(stageCopies[context]);
            }
            stageCopies[context] = null;
        }

        /// <summary>The given transforms minus any that sit under another one (moving the parent moves them).</summary>
        private static IEnumerable<Transform> Outermost(params Transform[] transforms)
        {
            foreach (var t in transforms)
            {
                if (t == null)
                {
                    continue;
                }
                bool nested = false;
                foreach (var other in transforms)
                {
                    if (other != null && other != t && t.IsChildOf(other))
                    {
                        nested = true;
                        break;
                    }
                }
                if (!nested)
                {
                    yield return t;
                }
            }
        }

        private static Transform CommonAncestor(Transform a, Transform b)
        {
            for (var t = a; t != null; t = t.parent)
            {
                if (b.IsChildOf(t))
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>The transform in <paramref name="toRoot"/> at the same place <paramref name="original"/> has under <paramref name="fromRoot"/>.</summary>
        private static Transform Mirror(Transform original, Transform fromRoot, Transform toRoot)
        {
            var path = new Stack<int>();
            for (var t = original; t != fromRoot; t = t.parent)
            {
                path.Push(t.GetSiblingIndex());
            }
            var result = toRoot;
            while (path.Count > 0)
            {
                result = result.GetChild(path.Pop());
            }
            return result;
        }

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (var p = t.parent; p != null; p = p.parent)
            {
                path = p.name + "/" + path;
            }
            return path;
        }
    }
}
