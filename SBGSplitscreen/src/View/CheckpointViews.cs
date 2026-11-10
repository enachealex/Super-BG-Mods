using System;
using System.Collections.Generic;
using System.Linq;
using FMODUnity;
using HarmonyLib;
using Mirror;
using UnityEngine;
using UnityEngine.Rendering;

namespace SBGSplitscreen
{
    /// <summary>
    /// A checkpoint turns from red to green once you reach it. It is a single object in the world,
    /// and the game colours it for "the" local player, so everyone saw player 1's progress: the
    /// others respawned at checkpoints that stayed red on their screens. Now each camera sees every
    /// checkpoint coloured for its own player (swapped in just before that camera draws), and
    /// reaching one plays its activation (raise, sparkle, chime) for players 2-4 as it does for player 1.
    /// </summary>
    internal static class CheckpointViews
    {
        private static readonly AccessTools.FieldRef<CheckpointManager, SyncDictionary<ulong, Checkpoint>> activePerPlayer =
            AccessTools.FieldRefAccess<CheckpointManager, SyncDictionary<ulong, Checkpoint>>("activeCheckpointPerPlayerGuid");
        private static readonly AccessTools.FieldRef<CheckpointManager, HashSet<Checkpoint>> allCheckpoints =
            AccessTools.FieldRefAccess<CheckpointManager, HashSet<Checkpoint>>("allCheckpoints");
        private static readonly AccessTools.FieldRef<Checkpoint, MeshRenderer> baseMesh = AccessTools.FieldRefAccess<Checkpoint, MeshRenderer>("baseMesh");
        private static readonly AccessTools.FieldRef<Checkpoint, MeshRenderer> screenMesh = AccessTools.FieldRefAccess<Checkpoint, MeshRenderer>("screenMesh");
        private static readonly AccessTools.FieldRef<Checkpoint, Animator> animator = AccessTools.FieldRefAccess<Checkpoint, Animator>("animator");
        private static readonly AccessTools.FieldRef<Checkpoint, Transform> visualCenter = AccessTools.FieldRefAccess<Checkpoint, Transform>("visualCenter");
        private static readonly Action<Checkpoint> updateColor = AccessTools.MethodDelegate<Action<Checkpoint>>(AccessTools.Method(typeof(Checkpoint), "UpdateColor"));
        private static readonly int activateHash = Animator.StringToHash("activate");

        /// <summary>Per player: the checkpoint they have reached on this hole.</summary>
        private static readonly Checkpoint[] reached = new Checkpoint[Contexts.MaxPlayers];
        private static readonly bool[] known = new bool[Contexts.MaxPlayers];
        private static CheckpointManager manager;
        private static bool hooked;
        private static bool overriding;

        public static void Update()
        {
            if (!hooked)
            {
                hooked = true;
                RenderPipelineManager.beginCameraRendering += BeforeCameraDraws;
            }
            var current = SingletonNetworkBehaviour<CheckpointManager>.HasInstance ? SingletonNetworkBehaviour<CheckpointManager>.Instance : null;
            if (current != manager)
            {
                manager = current;
                Array.Clear(reached, 0, reached.Length);
                Array.Clear(known, 0, known.Length);
                overriding = false;
            }
            if (manager == null)
            {
                return;
            }
            if (Contexts.ExtraCount == 0)
            {
                // Back to one player: the game's own colouring applies again.
                if (overriding)
                {
                    overriding = false;
                    foreach (var checkpoint in allCheckpoints(manager))
                    {
                        if (checkpoint != null)
                        {
                            updateColor(checkpoint);
                        }
                    }
                }
                return;
            }
            var active = activePerPlayer(manager);
            for (int c = 0; c < Contexts.MaxPlayers; c++)
            {
                if (!Contexts.IsActive(c))
                {
                    reached[c] = null;
                    known[c] = false;
                    continue;
                }
                var player = Viewed(c);
                Checkpoint now = null;
                if (player != null && player.PlayerId != null)
                {
                    active.TryGetValue(player.PlayerId.Guid, out now);
                }
                // Player 1's activation is played by the game itself.
                if (c > 0 && known[c] && now != null && now != reached[c])
                {
                    PlayActivation(now);
                }
                reached[c] = now;
                known[c] = true;
            }
        }

        /// <summary>For the debug report.</summary>
        public static string Describe()
        {
            return string.Join(", ", Enumerable.Range(0, Contexts.MaxPlayers).Where(c => Contexts.IsActive(c))
                .Select(c => $"p{c + 1}={(reached[c] != null ? reached[c].name : "-")}"));
        }

        /// <summary>The player whose progress this player's screen shows: themselves, or whoever they spectate.</summary>
        private static PlayerInfo Viewed(int context)
        {
            var player = Contexts.GetValue(context, "GameManager.localPlayerInfo") as PlayerInfo;
            if (player != null && player.AsSpectator != null && player.AsSpectator.IsSpectating && player.AsSpectator.TargetPlayer != null)
            {
                return player.AsSpectator.TargetPlayer;
            }
            return player;
        }

        private static void PlayActivation(Checkpoint checkpoint)
        {
            animator(checkpoint)?.SetTrigger(activateHash);
            var center = visualCenter(checkpoint);
            if (center != null)
            {
                VfxManager.PlayPooledVfxLocalOnly(VfxType.CheckpointActivation, center.position, Quaternion.identity);
                RuntimeManager.PlayOneShot(GameManager.AudioSettings.CheckpointActivationEvent, center.position);
            }
        }

        private static void BeforeCameraDraws(ScriptableRenderContext context, Camera camera)
        {
            if (Contexts.ExtraCount == 0 || manager == null)
            {
                return;
            }
            int c = ScreenEffects.ContextOfCamera(camera);
            if (c < 0)
            {
                return;
            }
            var settings = GameManager.CheckpointSettings;
            foreach (var checkpoint in allCheckpoints(manager))
            {
                if (checkpoint == null)
                {
                    continue;
                }
                bool on = checkpoint == reached[c];
                SetMaterial(baseMesh(checkpoint), on ? settings.ActiveMaterial : settings.InactiveMaterial);
                SetMaterial(screenMesh(checkpoint), on ? settings.ScreenActiveMaterial : settings.ScreenInactiveMaterial);
            }
            overriding = true;
        }

        private static void SetMaterial(MeshRenderer renderer, Material material)
        {
            if (renderer != null && renderer.sharedMaterial != material)
            {
                renderer.sharedMaterial = material;
            }
        }
    }
}
