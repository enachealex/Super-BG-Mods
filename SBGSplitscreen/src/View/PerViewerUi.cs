using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// World-anchored UI that each player needs their own copy of: name tags over golfers and
    /// balls, the hole flag and ball distance markers, teammate and ball-dispenser icons. The
    /// object that owns such UI keeps it in one field and decides visibility from "the local
    /// player's" point of view. Here every such object gets one value of that field per player,
    /// and its UI methods run once per player, inside that player's context. Each copy then comes
    /// from that player's HUD and follows that player's rules (no name tag over yourself, only your
    /// own ball's distance marker, and so on).
    /// </summary>
    internal static class PerViewerUi
    {
        private sealed class Spec
        {
            public string[] Names;
            public Func<object, object>[] Get;
            public Action<object, object>[] Set;
        }

        private static readonly Dictionary<Type, Spec> specs = new Dictionary<Type, Spec>();
        /// <summary>Per instance: the UI field values of players 2-4 (player 1's live in the fields).</summary>
        private static readonly ConditionalWeakTable<object, object[][]> others = new ConditionalWeakTable<object, object[][]>();
        private static bool running;
        private static float lastErrorTime = -100f;

        public static void Init(Harmony harmony)
        {
            Add(harmony, typeof(GolfBall), new[] { "nameTag", "worldspaceIcon" },
                "UpdateNameTagEnabled", "UpdateNameTag", "RemoveNameTag", "UpdateWorldspaceIconEnabled", "RemoveWorldspaceIcon");
            Add(harmony, typeof(GolfHole), new[] { "worldspaceIcon" },
                "HideWorldspaceIcon", "UpdateWorldspaceIcon", "UpdateWorldspaceIconDistanceReference", "InitializeWorldspaceIcon");
            Add(harmony, typeof(PlayerId), new[] { "nameTag" },
                "UpdateNameTagEnabled", "UpdateNameTagOffsets", "RemoveNameTag");
            // Teammate icons and the timer bar over a player in jumbo burger giant form.
            Add(harmony, typeof(PlayerInfo), new[] { "teammateWorldSpaceIcon", "jumboBurgerGiantFormTimerUi" },
                "UpdateTeammateIcon", "ReturnTeammateIcon",
                "UpdateJumboBurgerGiantFormTimerUiEnabled", "UpdateJumboBurgerGiantFormTimerOffsets",
                "UpdateJumboBurgerGiantFormTimerUiFill", "FlashJumboBurgerGiantFormTimerInternal");
            Add(harmony, typeof(BallDispenser), new[] { "worldspaceIcon" },
                "ShowWorldspaceIcon", "HideWorldspaceIcon", "InitializeWorldspaceIcon");
            Add(harmony, typeof(MenuOpenInteraction), new[] { "worldspaceIcon" },
                "HideWorldspaceIcon", "UpdateWorldspaceIconDistanceReference", "UpdateWorldspaceIcon");
            Add(harmony, typeof(LocalSpectatorCameraFollower), new[] { "nameTag" },
                "ReturnNameTag", "UpdateNameTagVisibility");

            // A player's name can change after their tags exist; this method also fires events,
            // so only its name-tag part is repeated for the other players' tags.
            harmony.Patch(AccessTools.Method(typeof(PlayerId), "OnPlayerNameChanged"),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(PerViewerUi), nameof(RenameOtherTags))));
        }

        private static void Add(Harmony harmony, Type type, string[] fields, params string[] methods)
        {
            var spec = new Spec
            {
                Names = fields,
                Get = new Func<object, object>[fields.Length],
                Set = new Action<object, object>[fields.Length]
            };
            for (int i = 0; i < fields.Length; i++)
            {
                var field = AccessTools.Field(type, fields[i]);
                if (field == null)
                {
                    Plugin.Log.LogWarning($"Per-player UI field not found: {type.Name}.{fields[i]}");
                    return;
                }
                spec.Get[i] = FastAccess.InstanceGetter(field);
                spec.Set[i] = FastAccess.InstanceSetter(field);
            }
            specs[type] = spec;
            var prefix = new HarmonyMethod(AccessTools.Method(typeof(PerViewerUi), nameof(Prefix))) { priority = Priority.Last };
            foreach (string name in methods)
            {
                var method = AccessTools.Method(type, name);
                if (method == null)
                {
                    Plugin.Log.LogWarning($"Per-player UI method not found: {type.Name}.{name}");
                    continue;
                }
                harmony.Patch(method, prefix: prefix);
            }
        }

        private static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (running || Contexts.ExtraCount == 0 || __instance == null)
            {
                return true;
            }
            if (!specs.TryGetValue(__originalMethod.DeclaringType, out var spec))
            {
                return true;
            }
            RunForEachPlayer(__instance, spec, __originalMethod, __args);
            return false;
        }

        private static void RunForEachPlayer(object instance, Spec spec, MethodBase method, object[] args)
        {
            var store = others.GetValue(instance, _ => new object[Contexts.MaxPlayers][]);
            int fields = spec.Names.Length;
            var playerOne = new object[fields];
            int previous = Contexts.Current;
            bool wasPinned = Contexts.Pinned;
            running = true;
            Contexts.Pinned = true;
            try
            {
                Contexts.Switch(0);
                Call(instance, method, args);
                Read(instance, spec, playerOne);

                for (int c = 1; c < Contexts.MaxPlayers; c++)
                {
                    if (!Contexts.IsActive(c))
                    {
                        // That player left; their HUD and everything in it is gone with them.
                        store[c] = null;
                        continue;
                    }
                    Contexts.Switch(c);
                    Write(instance, spec, store[c]);
                    Call(instance, method, args);
                    store[c] = store[c] ?? new object[fields];
                    Read(instance, spec, store[c]);
                }
            }
            finally
            {
                Write(instance, spec, playerOne);
                Contexts.Switch(previous);
                Contexts.Pinned = wasPinned;
                running = false;
            }
        }

        private static void Call(object instance, MethodBase method, object[] args)
        {
            try
            {
                method.Invoke(instance, args);
            }
            catch (TargetInvocationException e)
            {
                if (Time.unscaledTime - lastErrorTime > 5f)
                {
                    lastErrorTime = Time.unscaledTime;
                    Plugin.Log.LogError($"{method.DeclaringType?.Name}.{method.Name} for player {Contexts.Current + 1}: {e.InnerException}");
                }
            }
        }

        private static void Read(object instance, Spec spec, object[] into)
        {
            for (int i = 0; i < spec.Get.Length; i++)
            {
                into[i] = spec.Get[i](instance);
            }
        }

        private static void Write(object instance, Spec spec, object[] values)
        {
            for (int i = 0; i < spec.Set.Length; i++)
            {
                spec.Set[i](instance, values?[i]);
            }
        }

        private static void RenameOtherTags(PlayerId __instance)
        {
            if (!others.TryGetValue(__instance, out var store))
            {
                return;
            }
            foreach (var values in store)
            {
                if (values?[0] is NameTagUi tag && tag != null)
                {
                    tag.SetName(__instance.name);
                }
            }
        }
    }
}
