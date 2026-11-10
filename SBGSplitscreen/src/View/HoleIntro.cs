using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// Every hole opens with a fly-over: the camera glides over the course with the HUD hidden
    /// while the hole's name is shown, slows down, then swoops down to the tee. The course manager
    /// switches cameras and the HUD for "the" local player only, so player 1 got the fly-over
    /// while everyone else was already standing at the tee. The camera and HUD calls it makes are
    /// now repeated for every player, and a player whose camera is only set up once the fly-over
    /// is under way joins it at the same point, so all views open the hole together.
    /// </summary>
    internal static class HoleIntro
    {
        /// <summary>Above zero while one of the course manager's intro methods is running.</summary>
        private static int scope;
        private static bool replaying;

        /// <summary>Per player: the course manager whose fly-over that player's view was brought into.</summary>
        private static readonly int[] caughtUp = new int[Contexts.MaxPlayers];

        private static readonly AccessTools.FieldRef<OverviewCameraModule, float> timeSinceStarted =
            AccessTools.FieldRefAccess<OverviewCameraModule, float>("timeSinceStarted");
        private static readonly AccessTools.FieldRef<OverviewCameraModule, bool> isSlowingDown =
            AccessTools.FieldRefAccess<OverviewCameraModule, bool>("isSlowingDown");
        private static readonly AccessTools.FieldRef<OverviewCameraModule, float> timeSinceStartedSlowdown =
            AccessTools.FieldRefAccess<OverviewCameraModule, float>("timeSinceStartedSlowdown");
        private static readonly AccessTools.FieldRef<OverviewCameraModule, float> slowdownDuration =
            AccessTools.FieldRefAccess<OverviewCameraModule, float>("slowdownDuration");

        /// <summary>
        /// Called every frame per joined player once their camera and HUD exist: if this hole's
        /// fly-over is still playing, put their view into it, at the same point as player 1's.
        /// </summary>
        public static void CatchUp(int context)
        {
            if (!SingletonNetworkBehaviour<CourseManager>.HasInstance || !PlayerViews.HasRig(context) || !PlayerHuds.HasHud(context))
            {
                return;
            }
            var course = SingletonNetworkBehaviour<CourseManager>.Instance;
            int id = course.GetInstanceID();
            if (caughtUp[context] == id)
            {
                return;
            }
            caughtUp[context] = id;
            if (SingletonBehaviour<DrivingRangeManager>.HasInstance || course.NetworkisHoleOverviewFinished)
            {
                return;
            }

            var leader = (Contexts.GetValue(0, PlayerViews.CameraField) as Camera)?.GetComponent<OverviewCameraModule>();
            int previous = Contexts.Enter(context);
            try
            {
                GameplayCameraManager.TransitionTo(CameraModuleType.Overview, 0f);
                HudManager.Hide(instant: true);
                if (leader != null && CameraModuleController.CurrentModule is OverviewCameraModule own && own != leader)
                {
                    timeSinceStarted(own) = timeSinceStarted(leader);
                    isSlowingDown(own) = isSlowingDown(leader);
                    timeSinceStartedSlowdown(own) = timeSinceStartedSlowdown(leader);
                    slowdownDuration(own) = slowdownDuration(leader);
                }
            }
            finally
            {
                Contexts.Restore(previous);
            }
        }

        private static void ForEveryOtherPlayer(Action action)
        {
            if (replaying || Contexts.ExtraCount == 0)
            {
                return;
            }
            int current = Contexts.Current;
            replaying = true;
            try
            {
                for (int c = 0; c < Contexts.MaxPlayers; c++)
                {
                    if (c == current || !Contexts.IsActive(c))
                    {
                        continue;
                    }
                    int previous = Contexts.Enter(c);
                    try
                    {
                        action();
                    }
                    finally
                    {
                        Contexts.Restore(previous);
                    }
                }
            }
            finally
            {
                replaying = false;
            }
        }

        /// <summary>The course manager's methods that move the camera into, along and out of the fly-over, and hide or show the HUD around it.</summary>
        [HarmonyPatch]
        private static class IntroScope
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(CourseManager), "Awake");
                yield return AccessTools.Method(typeof(CourseManager), "OnStartClient");
                yield return AccessTools.Method(typeof(CourseManager), "OnMatchStateChanged");
                yield return AccessTools.Method(typeof(CourseManager), "OnIsHoleOverviewFinishedChanged");
            }

            private static void Prefix() => scope++;

            private static void Finalizer() => scope--;
        }

        /// <summary>Camera transitions and HUD visibility asked for inside those methods: do the same for everyone else.</summary>
        [HarmonyPatch]
        private static class ReplayForEveryone
        {
            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (var method in typeof(GameplayCameraManager).GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name == nameof(GameplayCameraManager.TransitionTo))
                    {
                        yield return method;
                    }
                }
                yield return AccessTools.Method(typeof(HudManager), nameof(HudManager.Show));
                yield return AccessTools.Method(typeof(HudManager), nameof(HudManager.Hide));
            }

            private static void Postfix(MethodBase __originalMethod, object[] __args)
            {
                if (scope > 0)
                {
                    ForEveryOtherPlayer(() => __originalMethod.Invoke(null, __args));
                }
            }
        }

        /// <summary>The fly-over eases to a stop before swooping to the tee; slow every player's down together.</summary>
        [HarmonyPatch(typeof(CourseManager), "UserCode_RpcSlowDownOverviewCamera__Single")]
        private static class SlowDownForEveryone
        {
            private static void Postfix(float duration)
            {
                ForEveryOtherPlayer(() =>
                {
                    if (CameraModuleController.CurrentModuleType == CameraModuleType.Overview && CameraModuleController.CurrentModule is OverviewCameraModule overview)
                    {
                        overview.BeginSlowdown(duration);
                    }
                });
            }
        }
    }
}
