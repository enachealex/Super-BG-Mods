using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// Dueling pistol adjustments from the settings file, for games you host only; when you join
    /// someone else's game everything is the game's own, so you play exactly like everyone there.
    ///
    /// Spread: how far a shot strays from where you aim. Each player's own game works this out,
    /// so it applies to everyone playing on this PC.
    ///
    /// Hit area: a shot counts against a player anywhere inside a generous capsule about twice as
    /// wide as their body. That is narrowed towards the body itself: for players on this PC the
    /// bullet flies past instead, and hits reported by players online are checked by the host
    /// against the same rule, so it holds for everyone in the game.
    /// </summary>
    internal static class ItemTweaks
    {
        private static readonly AccessTools.FieldRef<ItemSettings, float> pistolSpread =
            AccessTools.FieldRefAccess<ItemSettings, float>("<DuelingPistolMaxInaccuracyAngle>k__BackingField");

        private static ItemSettings items;
        private static float originalPistolSpread;
        private static bool? tweaked;

        /// <summary>Narrowing applies while hosting, with a hit area below the game's own.</summary>
        private static bool NarrowHitArea => NetworkServer.active && Settings.PistolHitArea.Value < 100;

        /// <summary>Called every frame: tweaked while hosting, the game's own values otherwise.</summary>
        public static void Update()
        {
            var current = GameManager.ItemSettings;
            if (current == null)
            {
                return;
            }
            if (current != items)
            {
                items = current;
                originalPistolSpread = pistolSpread(items);
                tweaked = null;
            }
            bool hosting = NetworkServer.active;
            if (tweaked == hosting)
            {
                return;
            }
            tweaked = hosting;
            float percent = Mathf.Clamp(Settings.PistolSpread.Value, 0, 100);
            pistolSpread(items) = hosting ? originalPistolSpread * percent / 100f : originalPistolSpread;
            Plugin.Log.LogInfo(hosting
                ? $"Hosting: dueling pistol spread {pistolSpread(items):F2} degrees ({percent:F0}% of the game's {originalPistolSpread:F2}), hit area {Mathf.Clamp(Settings.PistolHitArea.Value, 0, 100)}%, range {items.DuelingPistolMaxShotDistance:F0} m."
                : $"Not hosting: dueling pistol back to the game's own (spread {originalPistolSpread:F2} degrees, full hit area).");
        }

        /// <summary>
        /// Whether a shot that met the player's hit capsule at this point, flying this way, passes
        /// close enough to their body (standing or diving): within the body's own radius plus the
        /// allowed part of the extra room the hit capsule gives.
        /// </summary>
        public static bool PassesNearBody(PlayerInfo player, Vector3 point, Vector3 direction)
        {
            var movement = player.Movement;
            var generous = movement.HittableCollider;
            float allowed = Mathf.Clamp(Settings.PistolHitArea.Value, 0, 100) / 100f;
            Vector3 dir = direction.normalized;
            // The capsule is at most a couple of metres across, so this covers the shot's whole way through it.
            Vector3 from = point - dir * 2f, to = point + dir * 4f;
            bool any = false;
            foreach (var body in new[] { movement.UprightCollider, movement.DivingCollider })
            {
                if (body == null || !body.enabled || !body.gameObject.activeInHierarchy)
                {
                    continue;
                }
                any = true;
                Segment(body, out var a, out var b, out float radius);
                float extra = generous != null ? Mathf.Max(0f, Radius(generous) - radius) * allowed : 0f;
                if (SegmentDistance(a, b, from, to) <= radius + extra + 0.05f)
                {
                    return true;
                }
            }
            // No body shape to check against (should not happen): leave the game's decision alone.
            return !any;
        }

        /// <summary>Whether this point is on the surface of the player's hit capsule (rather than their body or a cart seat).</summary>
        private static bool OnHitCapsule(PlayerInfo player, Vector3 point)
        {
            var generous = player.Movement.HittableCollider;
            if (generous == null)
            {
                return false;
            }
            Segment(generous, out var a, out var b, out float radius);
            return Mathf.Abs(SegmentDistance(a, b, point, point) - radius) < 0.15f;
        }

        private static float Radius(CapsuleCollider capsule)
        {
            Vector3 s = capsule.transform.lossyScale;
            return capsule.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Abs(s.z));
        }

        private static void Segment(CapsuleCollider capsule, out Vector3 a, out Vector3 b, out float radius)
        {
            var t = capsule.transform;
            Vector3 axis = capsule.direction == 0 ? t.right : capsule.direction == 1 ? t.up : t.forward;
            float scale = capsule.direction == 0 ? Mathf.Abs(t.lossyScale.x) : capsule.direction == 1 ? Mathf.Abs(t.lossyScale.y) : Mathf.Abs(t.lossyScale.z);
            radius = Radius(capsule);
            float half = Mathf.Max(0f, capsule.height * scale * 0.5f - radius);
            Vector3 center = t.TransformPoint(capsule.center);
            a = center - axis * half;
            b = center + axis * half;
        }

        /// <summary>Closest distance between segments p1-q1 and p2-q2.</summary>
        private static float SegmentDistance(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), f = Vector3.Dot(d2, r);
            float s, t;
            if (a <= 1e-6f && e <= 1e-6f)
            {
                return (p1 - p2).magnitude;
            }
            if (a <= 1e-6f)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= 1e-6f)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float b = Vector3.Dot(d1, d2);
                    float denom = a * e - b * b;
                    s = denom != 0f ? Mathf.Clamp01((b * f - c * e) / denom) : 0f;
                    t = (b * s + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((b - c) / a);
                    }
                }
            }
            return ((p1 + d1 * s) - (p2 + d2 * t)).magnitude;
        }

        /// <summary>
        /// The pistol's shot, for players on this PC: what the bullet meets, minus the outer part of
        /// any player's hit capsule it only grazes. It then flies on to whatever is behind.
        /// </summary>
        public static int PistolRaycast(Ray ray, RaycastHit[] results, float maxDistance, int layerMask, QueryTriggerInteraction triggers)
        {
            int count = Physics.RaycastNonAlloc(ray, results, maxDistance, layerMask, triggers);
            if (!NarrowHitArea)
            {
                return count;
            }
            int kept = 0;
            for (int i = 0; i < count; i++)
            {
                var hit = results[i];
                var player = hit.collider is CapsuleCollider capsule ? capsule.GetComponentInParent<PlayerInfo>() : null;
                if (player != null && hit.collider == player.Movement.HittableCollider
                    && !PassesNearBody(player, hit.distance > 0f ? hit.point : ray.origin, ray.direction))
                {
                    if (Settings.DebugMode)
                    {
                        Plugin.Log.LogInfo($"Pistol shot grazed {player.name}'s hit area outside the allowed part: flies past.");
                    }
                    continue;
                }
                results[kept++] = hit;
            }
            return kept;
        }

        [HarmonyPatch]
        private static class PistolShotRaycast
        {
            private static readonly MethodInfo original = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastNonAlloc),
                new[] { typeof(Ray), typeof(RaycastHit[]), typeof(float), typeof(int), typeof(QueryTriggerInteraction) });

            /// <summary>
            /// The pistol's shot and its bounce off an electromagnet shield (local functions of its
            /// firing routine, wherever the compiler put them).
            /// </summary>
            private static List<MethodBase> Targets()
            {
                const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                var types = new List<System.Type> { typeof(PlayerInventory) };
                types.AddRange(typeof(PlayerInventory).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
                return types.SelectMany(t => t.GetMethods(all))
                    .Where(m => m.Name.Contains("<ShootDuelingPistolRoutine>g__Shoot|") || m.Name.Contains("<ShootDuelingPistolRoutine>g__ReflectShotOffElectromagnetShield|"))
                    .Cast<MethodBase>().ToList();
            }

            private static IEnumerable<MethodBase> TargetMethods() => Targets();

            private static bool Prepare(MethodBase original)
            {
                // Called once with null before the targets are known: patch only when there is something to patch.
                if (original != null || Targets().Count > 0)
                {
                    return true;
                }
                Plugin.Log.LogWarning("Could not find the dueling pistol's shot code; the PistolHitArea setting only applies to people playing online.");
                return false;
            }

            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var replacement = AccessTools.Method(typeof(ItemTweaks), nameof(PistolRaycast));
                foreach (var instruction in instructions)
                {
                    if (instruction.Calls(original))
                    {
                        yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                    }
                    else
                    {
                        yield return instruction;
                    }
                }
            }
        }

        /// <summary>A pistol hit reported by someone playing online: the host applies the same rule before it counts.</summary>
        [HarmonyPatch]
        private static class OnlinePistolHits
        {
            private static MethodBase TargetMethod()
            {
                return typeof(Hittable).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                    .First(m => m.Name.StartsWith("UserCode_CmdHitWithItem"));
            }

            private static bool Prefix(Hittable __instance, ItemType itemType, Vector3 hitLocalPosition, Vector3 direction, Vector3 localOrigin, NetworkConnectionToClient sender)
            {
                if (!NarrowHitArea || itemType != ItemType.DuelingPistol || __instance == null || !__instance.AsEntity.IsPlayer)
                {
                    return true;
                }
                // Shots from players on this PC were already narrowed where they were fired.
                if (sender == null || sender == NetworkServer.localConnection || sender is LocalPlayerConnection)
                {
                    return true;
                }
                if (AllowsReportedHit(__instance, hitLocalPosition, direction, localOrigin))
                {
                    return true;
                }
                if (Settings.DebugMode)
                {
                    Plugin.Log.LogInfo($"Online pistol hit on {__instance.name} was outside the allowed hit area: ignored.");
                }
                return false;
            }
        }

        /// <summary>Whether a pistol hit another game reported on this player is inside the allowed hit area.</summary>
        public static bool AllowsReportedHit(Hittable target, Vector3 hitLocalPosition, Vector3 direction, Vector3 localOrigin)
        {
            // Where the shot started and where it met the player both come in the player's own
            // frame as the shooter saw them, so the line between them stays true to that view
            // even if the player has moved or turned since.
            var player = target.AsEntity.PlayerInfo;
            Vector3 hit = target.transform.TransformPoint(hitLocalPosition);
            Vector3 path = hit - target.transform.TransformPoint(localOrigin);
            if (path.sqrMagnitude < 1e-6f)
            {
                path = direction;
            }
            return !OnHitCapsule(player, hit) || PassesNearBody(player, hit, path);
        }
    }
}
