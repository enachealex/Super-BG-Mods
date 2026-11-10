using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// A player's character can outlive their HUD while a hole unloads, and its flash-blindness
    /// cleanup then fades a HUD that is already gone.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(HudManager), "ApplyAlpha")]
    internal static class HudFadeAfterUnloadPatch
    {
        private static bool Prefix(CanvasGroup ___canvasGroup) => ___canvasGroup != null;
    }

    /// <summary>
    /// A tooltip keeps every element that registered with it. When a player leaves (or is kicked),
    /// their entry in the pause menu is destroyed without unregistering, and the tooltip would
    /// trip over it every frame. Forget destroyed elements first.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(UiTooltip), "LateUpdate")]
    internal static class TooltipForgetsDestroyedPatch
    {
        private static void Prefix(Dictionary<RectTransform, (string, string)> ___tooltipSources,
            Dictionary<RectTransform, (string, string)> ___pendingOverrides, ref RectTransform ___currentTooltipSource)
        {
            RemoveDestroyed(___tooltipSources);
            RemoveDestroyed(___pendingOverrides);
            if (___currentTooltipSource is object && ___currentTooltipSource == null)
            {
                ___currentTooltipSource = null;
            }
        }

        private static void RemoveDestroyed(Dictionary<RectTransform, (string, string)> sources)
        {
            if (sources == null)
            {
                return;
            }
            List<RectTransform> dead = null;
            foreach (var key in sources.Keys)
            {
                if (key == null)
                {
                    (dead ??= new List<RectTransform>()).Add(key);
                }
            }
            if (dead != null)
            {
                foreach (var key in dead)
                {
                    sources.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// Each player's HUD (hotbar, swing meter, reticles, prompts, name tags...) and character
    /// customization menu, drawn inside that player's part of the window. The gameplay canvas also
    /// holds shared menus (pause, scoreboard, match setup, chat); those stay with player 1 and keep
    /// covering the whole window.
    /// </summary>
    internal static class PlayerHuds
    {
        /// <summary>Children of the gameplay canvas that belong to one player.</summary>
        private static readonly string[] PerPlayerParts = { "HUD", "Flash Screen VFX", "Player Customization Menu", "Pause Menu" };

        private struct Layout
        {
            public Vector2 AnchorMin, AnchorMax, Pivot, AnchoredPosition, SizeDelta;
            public Vector3 Scale;
        }

        private static readonly GameObject[] huds = new GameObject[Contexts.MaxPlayers];
        private static readonly Dictionary<RectTransform, Layout> originalLayouts = new Dictionary<RectTransform, Layout>();
        private static readonly Dictionary<RectTransform, (Rect viewport, Vector2 canvasSize)> applied = new Dictionary<RectTransform, (Rect, Vector2)>();
        private static bool typesPrepared;

        public static bool HasHud(int context) => huds[context] != null;

        public static GameObject HudOf(int context) => huds[context];

        public static GameObject PlayerOneCanvas()
        {
            string field = Contexts.SingletonField(typeof(GameplayScreenspaceUiCanvas));
            var canvas = Contexts.Has(field)
                ? Contexts.GetValue(0, field) as GameplayScreenspaceUiCanvas
                : SingletonBehaviour<GameplayScreenspaceUiCanvas>.Instance;
            return canvas != null ? canvas.gameObject : null;
        }

        /// <summary>
        /// Makes every HUD singleton per-player and every HUD component context-aware. Runs once,
        /// as soon as the first hosted game's HUD exists, so the patching pause lands on a loading screen.
        /// </summary>
        public static void PrepareTypes()
        {
            if (typesPrepared)
            {
                return;
            }
            var root = PlayerOneCanvas();
            if (root == null)
            {
                return;
            }
            typesPrepared = true;
            var gameAssemblies = new HashSet<Assembly> { typeof(GameManager).Assembly, typeof(SingletonBehaviour<>).Assembly };
            var behaviours = PerPlayerParts.Select(n => root.transform.Find(n)).Where(t => t != null)
                .SelectMany(t => t.GetComponentsInChildren<MonoBehaviour>(true))
                .Concat(root.GetComponents<MonoBehaviour>())
                .ToList();
            var singletons = behaviours.OfType<SingletonBehaviourBase>().Select(b => b.GetType()).Distinct().ToList();
            foreach (var type in singletons)
            {
                Contexts.AddSingleton(type);
                AddPerPlayerStatics(type);
            }
            // Menu navigation keeps a static stack of open menus and only drives the top one; with a
            // stack per player, two players can each have a menu open and navigate it at once.
            AddPerPlayerStatics(typeof(MenuNavigation));
            Plugin.Log.LogInfo("Per-player HUD singletons: " + string.Join(", ", singletons.Select(t => t.Name)));
            var types = behaviours.Select(b => b.GetType())
                .Concat(behaviours.SelectMany(PrefabComponentTypes))
                .Where(t => gameAssemblies.Contains(t.Assembly))
                .Distinct();
            ContextPatcher.PatchTypes(types);
        }

        /// <summary>
        /// Static state of a HUD manager that belongs to one HUD: object pools and their parents,
        /// cached sizes, events. Immutable statics (animator hashes, lookup tables) stay shared.
        /// </summary>
        private static void AddPerPlayerStatics(System.Type type)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (var field in type.GetFields(flags))
            {
                if (field.IsLiteral || field.Name.Contains("<"))
                {
                    continue;
                }
                var ft = field.FieldType;
                bool isCollection = ft.IsGenericType && (ft.GetGenericTypeDefinition() == typeof(Stack<>)
                    || ft.GetGenericTypeDefinition() == typeof(List<>) || ft.GetGenericTypeDefinition() == typeof(Queue<>)
                    || ft.GetGenericTypeDefinition() == typeof(HashSet<>) || ft.GetGenericTypeDefinition() == typeof(Dictionary<,>));
                bool isUnityObject = typeof(Object).IsAssignableFrom(ft);
                bool isEvent = typeof(System.Delegate).IsAssignableFrom(ft);
                bool isMutableValue = ft.IsValueType && !field.IsInitOnly;
                if (isCollection)
                {
                    Contexts.AddStatic(type, field.Name, () => System.Activator.CreateInstance(ft));
                }
                else if (isUnityObject || isEvent || isMutableValue)
                {
                    Contexts.AddStatic(type, field.Name);
                }
            }
        }

        /// <summary>
        /// Component types of prefabs a HUD element spawns at runtime (world icons, name tags, text
        /// popups). Their instances are parented under the HUD, so they belong to its player.
        /// </summary>
        private static IEnumerable<System.Type> PrefabComponentTypes(MonoBehaviour behaviour)
        {
            if (behaviour == null)
            {
                yield break;
            }
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
            {
                foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    GameObject prefab = null;
                    if (field.FieldType == typeof(GameObject))
                    {
                        prefab = field.GetValue(behaviour) as GameObject;
                    }
                    else if (typeof(Component).IsAssignableFrom(field.FieldType))
                    {
                        prefab = (field.GetValue(behaviour) as Component)?.gameObject;
                    }
                    // Prefab assets live outside any scene.
                    if (prefab == null || prefab.scene.IsValid())
                    {
                        continue;
                    }
                    foreach (var component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (component != null)
                        {
                            yield return component.GetType();
                        }
                    }
                }
            }
        }

        public static void EnsureHud(int context)
        {
            if (huds[context] != null)
            {
                return;
            }
            var root = PlayerOneCanvas();
            if (root == null)
            {
                return;
            }
            PrepareTypes();

            var holder = new GameObject($"Splitscreen P{context + 1} HUD (holder)");
            holder.SetActive(false);
            var hud = Object.Instantiate(root, holder.transform);
            hud.name = $"Splitscreen P{context + 1} HUD";
            for (int i = hud.transform.childCount - 1; i >= 0; i--)
            {
                var child = hud.transform.GetChild(i);
                if (!PerPlayerParts.Contains(child.name))
                {
                    Object.DestroyImmediate(child.gameObject);
                }
            }
            // Networked pieces of the HUD (the tee-off, match-end and next-match countdowns, the
            // knock-out feed) are scene objects with network identities; a working copy would clash
            // with the originals. The copy becomes a look-alike that follows player 1's instead.
            foreach (var identity in hud.GetComponentsInChildren<Mirror.NetworkIdentity>(true))
            {
                if (identity != null)
                {
                    string path = PathOf(identity.transform, hud.transform);
                    if (Settings.DebugMode)
                    {
                        Plugin.Log.LogInfo($"HUD copy mirrors networked {path} [{string.Join(",", identity.GetComponents<Mirror.NetworkBehaviour>().Select(b => b.GetType().Name))}]");
                    }
                    HudMirror.Attach(identity.gameObject, path);
                }
            }
            // Name tags, world icons, popups and menu buttons that player 1's HUD spawned at runtime:
            // copies would lack their runtime state. Unity names spawned objects "(Clone)"; authored
            // HUD parts are not. Lists that held them (some are saved fields, so the copy has them
            // too) are cleaned up, and the copy's own code spawns fresh ones as needed.
            var doomed = new HashSet<Object>();
            foreach (var t in hud.GetComponentsInChildren<Transform>(true))
            {
                if (t != hud.transform && t.name.EndsWith("(Clone)"))
                {
                    doomed.Add(t.gameObject);
                    foreach (var c in t.GetComponentsInChildren<Component>(true))
                    {
                        doomed.Add(c);
                    }
                }
            }
            foreach (var behaviour in hud.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour != null && !doomed.Contains(behaviour))
                {
                    RemoveReferences(behaviour, doomed);
                }
            }
            foreach (var o in doomed)
            {
                if (o is GameObject go && go != null)
                {
                    Object.DestroyImmediate(go);
                }
            }
            CustomizationPreviews.Separate(hud, context);
            JoinedPauseMenus.Customize(hud, context);
            hud.AddComponent<SplitscreenOwner>().Slot = context;
            // Player 1's menus (pause, scoreboard) share the original canvas and must draw on top.
            var canvas = hud.GetComponent<Canvas>();
            canvas.sortingOrder = root.GetComponent<Canvas>().sortingOrder - 1;

            int previous = Contexts.Enter(context);
            try
            {
                hud.transform.SetParent(null, false);
                Object.Destroy(holder);
            }
            finally
            {
                Contexts.Restore(previous);
            }
            huds[context] = hud;
            Plugin.Log.LogInfo($"Created HUD for player {context + 1}.");
        }

        private static string PathOf(Transform t, Transform root)
        {
            string path = t.name;
            for (var p = t.parent; p != null && p != root; p = p.parent)
            {
                path = p.name + "/" + path;
            }
            return path;
        }

        /// <summary>Drops entries pointing at <paramref name="doomed"/> objects from the component's list fields.</summary>
        private static void RemoveReferences(MonoBehaviour behaviour, HashSet<Object> doomed)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var type = behaviour.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
            {
                foreach (var field in type.GetFields(flags))
                {
                    var ft = field.FieldType;
                    if (!ft.IsGenericType || ft.GetGenericTypeDefinition() != typeof(List<>)
                        || !typeof(Object).IsAssignableFrom(ft.GetGenericArguments()[0]))
                    {
                        continue;
                    }
                    if (field.GetValue(behaviour) is System.Collections.IList list)
                    {
                        for (int i = list.Count - 1; i >= 0; i--)
                        {
                            if (list[i] is Object o && doomed.Contains(o))
                            {
                                list.RemoveAt(i);
                            }
                        }
                    }
                }
            }
        }

        public static void DestroyHud(int context)
        {
            if (huds[context] != null)
            {
                int previous = Contexts.Enter(context);
                try
                {
                    Object.DestroyImmediate(huds[context]);
                }
                finally
                {
                    Contexts.Restore(previous);
                }
            }
            huds[context] = null;
            CustomizationPreviews.Release(context);
        }

        /// <summary>Fits each player's HUD into the same part of the window as their camera.</summary>
        public static void ApplyLayout(IReadOnlyList<int> order)
        {
            for (int i = 0; i < order.Count; i++)
            {
                int c = order[i];
                var root = c == 0 ? PlayerOneCanvas() : huds[c];
                if (root == null)
                {
                    continue;
                }
                Rect viewport = PlayerViews.ViewportFor(i, order.Count);
                Vector2 canvasSize = ((RectTransform)root.transform).rect.size;
                foreach (string part in PerPlayerParts)
                {
                    if (root.transform.Find(part) is RectTransform rt)
                    {
                        Fit(rt, viewport, canvasSize);
                    }
                }
                bool split = order.Count > 1;
                // In a smaller pause menu the item probabilities would cover the rules and buttons.
                Move(root.transform.Find("Pause Menu/Menu container/Item probability") as RectTransform, split,
                    new Vector2(1f, 0f), new Vector2(-40f, 80f));
            }
        }

        private static readonly Dictionary<RectTransform, Layout> movedOriginals = new Dictionary<RectTransform, Layout>();

        /// <summary>Anchors an element to a corner while the screen is split; puts it back afterwards.</summary>
        private static void Move(RectTransform rt, bool split, Vector2 corner, Vector2 offset)
        {
            if (rt == null)
            {
                return;
            }
            if (!movedOriginals.TryGetValue(rt, out var original))
            {
                original = new Layout
                {
                    AnchorMin = rt.anchorMin, AnchorMax = rt.anchorMax, Pivot = rt.pivot,
                    AnchoredPosition = rt.anchoredPosition, SizeDelta = rt.sizeDelta, Scale = rt.localScale
                };
                movedOriginals[rt] = original;
            }
            var anchor = split ? corner : original.AnchorMin;
            if (rt.anchorMin == anchor && rt.anchorMax == (split ? corner : original.AnchorMax))
            {
                return;
            }
            rt.anchorMin = anchor;
            rt.anchorMax = split ? corner : original.AnchorMax;
            rt.pivot = split ? corner : original.Pivot;
            rt.anchoredPosition = split ? offset : original.AnchoredPosition;
        }

        /// <summary>
        /// Lays the HUD out on a virtual screen with the viewport's shape, at full size, then scales
        /// it down into the viewport. Corner elements stay in corners; nothing gets squashed.
        /// </summary>
        private static void Fit(RectTransform rt, Rect viewport, Vector2 canvasSize)
        {
            if (!originalLayouts.ContainsKey(rt))
            {
                originalLayouts[rt] = new Layout
                {
                    AnchorMin = rt.anchorMin, AnchorMax = rt.anchorMax, Pivot = rt.pivot,
                    AnchoredPosition = rt.anchoredPosition, SizeDelta = rt.sizeDelta, Scale = rt.localScale
                };
                PruneDestroyed();
            }
            if (applied.TryGetValue(rt, out var last) && last.viewport == viewport && last.canvasSize == canvasSize)
            {
                return;
            }
            applied[rt] = (viewport, canvasSize);

            // Menus draw decorations past the screen edge and rely on the edge to cut them off; in
            // a part of the screen nothing would, so clip each player's UI to their own part.
            var clip = rt.GetComponent<UnityEngine.UI.RectMask2D>() ?? rt.gameObject.AddComponent<UnityEngine.UI.RectMask2D>();
            clip.enabled = viewport.width < 1f || viewport.height < 1f;

            if (viewport.width >= 1f && viewport.height >= 1f)
            {
                var o = originalLayouts[rt];
                rt.anchorMin = o.AnchorMin;
                rt.anchorMax = o.AnchorMax;
                rt.pivot = o.Pivot;
                rt.anchoredPosition = o.AnchoredPosition;
                rt.sizeDelta = o.SizeDelta;
                rt.localScale = o.Scale;
                return;
            }
            float scale = Mathf.Min(viewport.width, viewport.height);
            rt.anchorMin = viewport.min;
            rt.anchorMax = viewport.max;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(viewport.width * canvasSize.x, viewport.height * canvasSize.y) * (1f / scale - 1f);
            rt.localScale = new Vector3(scale, scale, 1f);
        }



        private static void PruneDestroyed()
        {
            foreach (var dead in originalLayouts.Keys.Where(k => k == null).ToList())
            {
                originalLayouts.Remove(dead);
                applied.Remove(dead);
            }
        }
    }
}
