using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// One camera rig per player, each drawing into its own part of the window. Player 1 uses the
    /// scene's own rig; joined players get a copy that wakes up inside their context, so it
    /// becomes that context's CameraModuleController / GameplayCameraManager singleton.
    /// </summary>
    internal static class PlayerViews
    {
        public const string CameraField = "GameManager.camera";

        /// <summary>
        /// Components that must stay unique: Unity's audio listener (the game plays everything
        /// through FMOD, whose StudioListener supports one listener per player and is kept), the
        /// planar reflection renderer, and the post-processing manager, whose volumes are global and
        /// already apply to every camera.
        /// </summary>
        private static readonly HashSet<string> StripFromCopies = new HashSet<string>
        {
            "AudioListener", "ScreenSpacePlanarReflections", "PostProcessingManager"
        };

        private static readonly GameObject[] rigs = new GameObject[Contexts.MaxPlayers];
        private static Camera backdrop;
        private static bool loggedRigContents;

        public static void RegisterFields()
        {
            Contexts.AddGameManagerField("camera");
            Contexts.AddGameManagerField("cameraLevelBoundsTracker");
            Contexts.AddSingleton(typeof(CameraModuleController));
            Contexts.AddSingleton(typeof(GameplayCameraManager));
            Contexts.AddSingleton(typeof(StealthOverlayController));
            Contexts.AddSingleton(typeof(FlashFrameController));
            Contexts.AddStaticEvent(typeof(CameraModuleController), "ModuleTypeChanged");
            Contexts.AddStaticEvent(typeof(OrbitCameraModule), "OrbitCameraAwoken");
            Contexts.AddStaticEvent(typeof(GameManager), "SetCamera");
        }

        /// <summary>Component types on player 1's camera rig, which joined players' copies run.</summary>
        public static IEnumerable<System.Type> RigComponentTypes()
        {
            var main = Contexts.GetValue(0, CameraField) as Camera;
            if (main == null)
            {
                return Enumerable.Empty<System.Type>();
            }
            return main.GetComponentsInChildren<MonoBehaviour>(true).Select(c => c.GetType())
                .Concat(new[] { typeof(GameplayCameraManager) });
        }

        public static bool HasRig(int context) => rigs[context] != null;

        public static void EnsureRig(int context)
        {
            if (rigs[context] != null)
            {
                return;
            }
            var main = Contexts.GetValue(0, CameraField) as Camera;
            if (main == null)
            {
                return;
            }

            // Build the copy under an inactive parent so nothing in it wakes up yet.
            var holder = new GameObject($"Splitscreen P{context + 1} camera (holder)");
            holder.SetActive(false);
            var rig = Object.Instantiate(main.gameObject, holder.transform);
            rig.name = $"Splitscreen P{context + 1} camera";
            rig.tag = "Untagged";
            foreach (var component in rig.GetComponentsInChildren<Component>(true))
            {
                if (component == null)
                {
                    continue;
                }
                if (component.GetType().Name == "Volume" && component.gameObject != rig)
                {
                    Object.DestroyImmediate(component.gameObject);
                }
                else if (StripFromCopies.Contains(component.GetType().Name))
                {
                    Object.DestroyImmediate(component);
                }
            }
            rig.AddComponent<SplitscreenOwner>().Slot = context;

            if (!loggedRigContents)
            {
                loggedRigContents = true;
                Plugin.Log.LogInfo("Camera rig components: " + string.Join(", ", main.GetComponentsInChildren<Component>(true).Select(c => c.GetType().Name)));
                var listener = rig.GetComponent<FMODUnity.StudioListener>();
                if (listener != null)
                {
                    var target = listener.AttenuationObject;
                    Plugin.Log.LogInfo($"Player {context + 1} audio listener attenuation: {(target == null ? "camera" : target.name + (target.transform.IsChildOf(rig.transform) ? " (in rig)" : " (outside rig)"))}");
                }
            }

            int previous = Contexts.Enter(context);
            try
            {
                // Unparenting activates it: Awake and OnEnable run now, inside this player's context.
                rig.transform.SetParent(null, true);
                Object.Destroy(holder);
                if (!SingletonBehaviour<GameplayCameraManager>.HasInstance)
                {
                    rig.AddComponent<GameplayCameraManager>();
                }
                var camera = rig.GetComponent<Camera>();
                Contexts.SetValue(context, CameraField, camera);
                Contexts.SetValue(context, "GameManager.cameraLevelBoundsTracker", rig.GetComponent<LevelBoundsTracker>());
            }
            finally
            {
                Contexts.Restore(previous);
            }
            rigs[context] = rig;
            Plugin.Log.LogInfo($"Created camera for player {context + 1}.");
        }

        public static void DestroyRig(int context)
        {
            if (rigs[context] != null)
            {
                int previous = Contexts.Enter(context);
                try
                {
                    Object.DestroyImmediate(rigs[context]);
                }
                finally
                {
                    Contexts.Restore(previous);
                }
            }
            rigs[context] = null;
        }

        /// <summary>Player 1, then every joined player in slot order: the order of the screen areas.</summary>
        public static List<int> ActiveOrder()
        {
            var order = new List<int> { 0 };
            for (int c = 1; c < Contexts.MaxPlayers; c++)
            {
                if (Contexts.IsActive(c) && !Splitscreen.IsDeparting(c))
                {
                    order.Add(c);
                }
            }
            return order;
        }

        /// <summary>Splits the window between player 1 and every joined player.</summary>
        public static void ApplyViewports(List<int> order)
        {
            for (int i = 0; i < order.Count; i++)
            {
                if (Contexts.GetValue(order[i], CameraField) is Camera camera && camera != null)
                {
                    Rect rect = ViewportFor(i, order.Count);
                    if (camera.rect != rect)
                    {
                        camera.rect = rect;
                    }
                }
            }
            UpdateBackdrop(order.Count == 3);
        }

        public static Rect ViewportFor(int index, int count)
        {
            if (count <= 1)
            {
                return new Rect(0f, 0f, 1f, 1f);
            }
            if (count == 2)
            {
                return Settings.Layout == TwoPlayerLayout.SideBySide
                    ? new Rect(index * 0.5f, 0f, 0.5f, 1f)
                    : new Rect(0f, index == 0 ? 0.5f : 0f, 1f, 0.5f);
            }
            // Quarters: top-left, top-right, bottom-left, bottom-right.
            return new Rect(index % 2 == 0 ? 0f : 0.5f, index < 2 ? 0.5f : 0f, 0.5f, 0.5f);
        }

        /// <summary>With three players one quarter is unused; keep it black instead of stale pixels.</summary>
        private static void UpdateBackdrop(bool needed)
        {
            if (!needed)
            {
                if (backdrop != null)
                {
                    backdrop.enabled = false;
                }
                return;
            }
            if (backdrop == null)
            {
                var go = new GameObject("Splitscreen backdrop");
                Object.DontDestroyOnLoad(go);
                backdrop = go.AddComponent<Camera>();
                backdrop.clearFlags = CameraClearFlags.SolidColor;
                backdrop.backgroundColor = Color.black;
                backdrop.cullingMask = 0;
                backdrop.depth = -100f;
            }
            backdrop.enabled = true;
        }
    }
}
