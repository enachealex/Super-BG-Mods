using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using FMOD.Studio;
using HarmonyLib;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// A homing rocket or homing ball locked onto you beeps faster and faster as it closes in, with
    /// a warning icon over it. The projectile decides "is this coming for the local player?" and
    /// keeps a single beep and icon, so only player 1 was ever warned. Here every player gets their
    /// own: the check runs once per player, each with their own beep and icon (in their own HUD),
    /// and each beep's pace and left/right pan follow that player's distance and camera.
    /// </summary>
    internal static class HomingWarnings
    {
        private sealed class State
        {
            public WorldspaceIconUi Icon;
            public EventInstance Sound;
            public Coroutine Routine;
            public bool Playing;
        }

        private abstract class Kind
        {
            public abstract void Load(Component projectile, State state);
            public abstract void Save(Component projectile, State into);
            public abstract Hittable Target(Component projectile);
        }

        private sealed class Kind<T> : Kind where T : Component
        {
            private readonly AccessTools.FieldRef<T, WorldspaceIconUi> icon = AccessTools.FieldRefAccess<T, WorldspaceIconUi>("homingWarningWorldspaceIcon");
            private readonly AccessTools.FieldRef<T, EventInstance> sound = AccessTools.FieldRefAccess<T, EventInstance>("homingWarningSoundInstance");
            private readonly AccessTools.FieldRef<T, Coroutine> routine = AccessTools.FieldRefAccess<T, Coroutine>("homingWarningUpdateRoutine");
            private readonly AccessTools.FieldRef<T, bool> playing = AccessTools.FieldRefAccess<T, bool>("<IsPlayingHomingWarning>k__BackingField");
            private readonly Func<T, Hittable> target;

            public Kind(Func<T, Hittable> target)
            {
                this.target = target;
            }

            public override void Load(Component projectile, State state)
            {
                var p = (T)projectile;
                icon(p) = state?.Icon;
                sound(p) = state?.Sound ?? default;
                routine(p) = state?.Routine;
                playing(p) = state?.Playing ?? false;
            }

            public override void Save(Component projectile, State into)
            {
                var p = (T)projectile;
                into.Icon = icon(p);
                into.Sound = sound(p);
                into.Routine = routine(p);
                into.Playing = playing(p);
            }

            public override Hittable Target(Component projectile) => target((T)projectile);
        }

        private static readonly Dictionary<Type, Kind> kinds = new Dictionary<Type, Kind>
        {
            { typeof(Hittable), new Kind<Hittable>(h => h.NetworkhomingTargetHittable) },
            { typeof(Rocket), new Kind<Rocket>(r => r.NetworkhomingTargetHittable) }
        };

        /// <summary>Per projectile: the warning of players 2-4 (player 1's lives in the projectile's own fields).</summary>
        private static readonly ConditionalWeakTable<Component, State[]> others = new ConditionalWeakTable<Component, State[]>();
        private static readonly HashSet<Component> sounding = new HashSet<Component>();
        private static bool running;

        public static void Init(Harmony harmony)
        {
            foreach (var type in kinds.Keys)
            {
                harmony.Patch(AccessTools.Method(type, "UpdateHomingWarning"),
                    prefix: new HarmonyMethod(typeof(HomingWarnings), nameof(UpdateForEachPlayer)) { priority = Priority.Last });
                harmony.Patch(AccessTools.Method(type, "OnWillBeDestroyed"),
                    postfix: new HarmonyMethod(typeof(HomingWarnings), nameof(StopOtherPlayers)));
            }
        }

        private static bool UpdateForEachPlayer(Component __instance, MethodBase __originalMethod)
        {
            if (running || Contexts.ExtraCount == 0 || __instance == null || !kinds.TryGetValue(__originalMethod.DeclaringType, out var kind))
            {
                return true;
            }
            var store = others.GetValue(__instance, _ => new State[Contexts.MaxPlayers]);
            var playerOne = new State();
            int previous = Contexts.Current;
            bool wasPinned = Contexts.Pinned;
            running = true;
            Contexts.Pinned = true;
            try
            {
                Contexts.Switch(0);
                bool wasPlaying = Playing(__instance, kind);
                Call(__instance, __originalMethod);
                kind.Save(__instance, playerOne);
                Report(__instance, 0, wasPlaying, playerOne.Playing);
                for (int c = 1; c < Contexts.MaxPlayers; c++)
                {
                    if (!Contexts.IsActive(c))
                    {
                        Silence(store, c);
                        continue;
                    }
                    Contexts.Switch(c);
                    wasPlaying = store[c]?.Playing ?? false;
                    kind.Load(__instance, store[c]);
                    Call(__instance, __originalMethod);
                    store[c] = store[c] ?? new State();
                    kind.Save(__instance, store[c]);
                    Report(__instance, c, wasPlaying, store[c].Playing);
                    if (Settings.DebugMode && !wasPlaying && store[c].Playing)
                    {
                        Plugin.Log.LogInfo($"  player {c + 1}'s warning icon is in {(store[c].Icon != null ? store[c].Icon.transform.root.name : "nothing")}, beep {(store[c].Sound.isValid() ? "playing" : "missing")}");
                    }
                    if (store[c].Playing)
                    {
                        sounding.Add(__instance);
                    }
                }
            }
            finally
            {
                kind.Load(__instance, playerOne);
                Contexts.Switch(previous);
                Contexts.Pinned = wasPinned;
                running = false;
            }
            return false;
        }

        private static bool Playing(Component projectile, Kind kind)
        {
            var probe = new State();
            kind.Save(projectile, probe);
            return probe.Playing;
        }

        private static void Report(Component projectile, int c, bool was, bool now)
        {
            if (Settings.DebugMode && was != now)
            {
                Plugin.Log.LogInfo($"Homing warning for player {c + 1} {(now ? "on" : "off")}: {projectile.name} -> {kinds[projectile.GetType()].Target(projectile)?.name}");
            }
        }

        private static void Call(Component instance, MethodBase method)
        {
            try
            {
                method.Invoke(instance, null);
            }
            catch (TargetInvocationException e)
            {
                Plugin.Log.LogError($"{method.DeclaringType?.Name}.{method.Name} for player {Contexts.Current + 1}: {e.InnerException}");
            }
        }

        /// <summary>The projectile exploded or vanished: the game cleans up player 1's warning, this everyone else's.</summary>
        private static void StopOtherPlayers(Component __instance)
        {
            if (!others.TryGetValue(__instance, out var store))
            {
                return;
            }
            for (int c = 1; c < Contexts.MaxPlayers; c++)
            {
                if (store[c]?.Icon != null && Contexts.IsActive(c))
                {
                    int previous = Contexts.Enter(c);
                    try
                    {
                        WorldspaceIconManager.ReturnIcon(store[c].Icon);
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                }
                Silence(store, c);
            }
            sounding.Remove(__instance);
        }

        private static void Silence(State[] store, int c)
        {
            if (store[c] != null && store[c].Sound.isValid())
            {
                store[c].Sound.stop(FMOD.Studio.STOP_MODE.ALLOWFADEOUT);
            }
            store[c] = null;
        }

        /// <summary>
        /// Each frame, like the game does for player 1: the beep speeds up as the projectile closes
        /// in and pans towards the side it comes from, seen through that player's camera.
        /// </summary>
        public static void Update()
        {
            if (sounding.Count == 0)
            {
                return;
            }
            foreach (var projectile in sounding.ToList())
            {
                if (projectile == null || !others.TryGetValue(projectile, out var store) || !kinds.TryGetValue(projectile.GetType(), out var kind))
                {
                    sounding.Remove(projectile);
                    continue;
                }
                bool any = false;
                var target = kind.Target(projectile);
                for (int c = 1; c < Contexts.MaxPlayers; c++)
                {
                    var state = store[c];
                    if (state == null || !state.Sound.isValid())
                    {
                        continue;
                    }
                    if (!Contexts.IsActive(c))
                    {
                        Silence(store, c);
                        continue;
                    }
                    any = true;
                    if (target == null)
                    {
                        continue;
                    }
                    var camera = Contexts.GetValue(c, PlayerViews.CameraField) as Camera;
                    Vector3 offset = projectile.transform.position - target.transform.position;
                    float distance = offset.magnitude;
                    var settings = GameManager.AudioSettings;
                    state.Sound.setParameterByID(global::AudioSettings.DistanceId,
                        BMath.InverseLerpClamped(settings.HomingProjectileWarningMinDistance, settings.HomingProjectileWarningMaxDistance, distance));
                    if (camera != null && distance > 0f)
                    {
                        state.Sound.setParameterByID(global::AudioSettings.PanningId, Vector3.Dot(offset / distance, camera.transform.right));
                    }
                }
                if (!any)
                {
                    sounding.Remove(projectile);
                }
            }
        }

        /// <summary>For the debug report: which players are being warned about this projectile.</summary>
        public static string Describe(Component projectile)
        {
            if (!others.TryGetValue(projectile, out var store))
            {
                return "others=none";
            }
            return "others=" + string.Join(",", Enumerable.Range(1, Contexts.MaxPlayers - 1)
                .Select(c => $"p{c + 1}:{(store[c] == null ? "-" : store[c].Playing ? (store[c].Sound.isValid() ? "beeping" : "warned") : "no")}"));
        }
    }
}
