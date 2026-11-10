using System;
using System.Collections.Generic;
using Game.Rendering.Blur;
using Game.Rendering.CRT;
using Game.Rendering.FlashFrame;
using Game.Rendering.ImpactFrame;
using Game.Rendering.StealthFrame;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace SBGSplitscreen
{
    /// <summary>
    /// The game's full-screen effects (flash-camera blinding, hit impact frames, the stealth
    /// overlay, the pause blur, the CRT look) are render features switched by static flags, so
    /// one player's effect showed on every camera. Each player now has their own copy of those
    /// flags, and when a camera is about to render, its player's flags are put in place for the
    /// duration of that feature's setup.
    /// </summary>
    internal static class ScreenEffects
    {
        private sealed class Effect
        {
            public string[] Names;
            public Func<object>[] Get;
            public Action<object>[] Set;
            public object[] Off;
            public object[] Saved;
        }

        private static readonly Dictionary<Type, Effect> effects = new Dictionary<Type, Effect>();

        public static void Init(Harmony harmony)
        {
            // Momentary effects start off for a new player; the CRT look is a display setting and is copied from player 1.
            Add(harmony, typeof(BlurFeature), false, "Enabled");
            Add(harmony, typeof(FlashFrameFeature), false, "Enabled");
            Add(harmony, typeof(ImpactFrameFeature), false, "Enabled", "DistortionCenter");
            Add(harmony, typeof(StealthFrameFeature), false, "Enabled");
            Add(harmony, typeof(CRTFeature), true, "Enabled", "BulgeEnabled", "BulgeIntensity", "AberrationEnabled", "ScanlineEnabled", "RollingLinesEnabled");
        }

        private static void Add(Harmony harmony, Type feature, bool copyFromPlayerOne, params string[] properties)
        {
            var effect = new Effect
            {
                Names = new string[properties.Length],
                Get = new Func<object>[properties.Length],
                Set = new Action<object>[properties.Length],
                Off = new object[properties.Length],
                Saved = new object[properties.Length]
            };
            for (int i = 0; i < properties.Length; i++)
            {
                var field = AccessTools.Field(feature, $"<{properties[i]}>k__BackingField");
                if (field == null)
                {
                    Plugin.Log.LogWarning($"Screen effect switch not found: {feature.Name}.{properties[i]}");
                    return;
                }
                string name = $"{feature.Name}.{field.Name}";
                effect.Names[i] = name;
                effect.Off[i] = FastAccess.DefaultOf(field.FieldType);
                effect.Get[i] = FastAccess.StaticGetter(field);
                effect.Set[i] = FastAccess.StaticSetter(field);
                Contexts.AddStatic(feature, field.Name, copyFromPlayerOne ? () => Contexts.GetValue(0, name) : (Func<object>)null);
            }
            effects[feature] = effect;
            harmony.Patch(AccessTools.Method(feature, "AddRenderPasses"),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(ScreenEffects), nameof(Prefix))),
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(ScreenEffects), nameof(Finalizer))));
        }

        private static void Prefix(object __instance, ref RenderingData renderingData, out bool __state)
        {
            __state = false;
            if (Contexts.ExtraCount == 0 || !effects.TryGetValue(__instance.GetType(), out var effect))
            {
                return;
            }
            int context = ContextOfCamera(renderingData.cameraData.camera);
            if (context == Contexts.Current)
            {
                return;
            }
            // Cameras that belong to nobody (the black backdrop behind an empty quarter, the
            // customization preview) get no full-screen effects at all.
            for (int i = 0; i < effect.Names.Length; i++)
            {
                effect.Saved[i] = effect.Get[i]();
                effect.Set[i](context >= 0 ? Contexts.GetValue(context, effect.Names[i]) : effect.Off[i]);
            }
            __state = true;
        }

        private static void Finalizer(object __instance, bool __state)
        {
            if (!__state || !effects.TryGetValue(__instance.GetType(), out var effect))
            {
                return;
            }
            for (int i = 0; i < effect.Names.Length; i++)
            {
                effect.Set[i](effect.Saved[i]);
            }
        }

        /// <summary>The player whose camera this is, or -1 (preview cameras, the backdrop...).</summary>
        public static int ContextOfCamera(Camera camera)
        {
            for (int c = 0; c < Contexts.MaxPlayers; c++)
            {
                if (Contexts.IsActive(c) && Contexts.GetValue(c, PlayerViews.CameraField) as Camera == camera)
                {
                    return c;
                }
            }
            return -1;
        }
    }
}
