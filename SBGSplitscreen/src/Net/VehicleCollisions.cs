using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// Golf carts and city traffic tell the physics engine to ignore every player except the local
    /// one: online, other people's characters are simulated on their own computers, and a
    /// collision here would only fight theirs. Splitscreen players are all simulated on this
    /// computer, but each one counts as "remote" from the others' point of view, so carts and
    /// traffic drove straight through everybody. Players on this PC now keep colliding with them;
    /// real online players are left as the game has them.
    /// </summary>
    internal static class VehicleCollisions
    {
        private static readonly HashSet<GolfCartMovement> carts = new HashSet<GolfCartMovement>();
        /// <summary>Per cart and player: whether the cart's giants-only collider currently ignores that player.</summary>
        private static readonly Dictionary<(int Cart, int Player), bool> giantColliderIgnores = new Dictionary<(int, int), bool>();

        private static readonly AccessTools.FieldRef<GolfCartMovement, Collider> giantsOnlyCollider =
            AccessTools.FieldRefAccess<GolfCartMovement, Collider>("localJumboBurgerGiantOnlyCollider");

        /// <summary>Player 1 (the host's own player) or a joined splitscreen player.</summary>
        public static bool IsOnThisPc(PlayerInfo player)
        {
            var conn = player != null ? player.connectionToClient : null;
            return conn is LocalPlayerConnection || (conn != null && conn == NetworkServer.localConnection);
        }

        [HarmonyPatch(typeof(GolfCartMovement), "IgnoreCollisionsWithRemotePlayer")]
        private static class CartIgnoresRemotePlayer
        {
            private static bool Prefix(PlayerInfo remotePlayer) => !IsOnThisPc(remotePlayer);
        }

        [HarmonyPatch(typeof(TrafficVehicle), "IgnoreCollisionsWithRemotePlayer")]
        private static class TrafficIgnoresRemotePlayer
        {
            private static bool Prefix(PlayerInfo remotePlayer) => !IsOnThisPc(remotePlayer);
        }

        [HarmonyPatch(typeof(GolfCartMovement), "Awake")]
        private static class CartAwake
        {
            private static void Postfix(GolfCartMovement __instance) => carts.Add(__instance);
        }

        /// <summary>
        /// Carts also have an extra collider that only a giant bumps into. The game switches it
        /// for its one local player; every player on this PC needs the same: on while they are a
        /// giant, off otherwise.
        /// </summary>
        public static void Update()
        {
            if (carts.Count == 0)
            {
                return;
            }
            carts.RemoveWhere(cart => cart == null);
            if (Contexts.ExtraCount == 0)
            {
                return;
            }
            foreach (var cart in carts)
            {
                var giantsOnly = giantsOnlyCollider(cart);
                if (giantsOnly == null)
                {
                    continue;
                }
                for (int c = 0; c < Contexts.MaxPlayers; c++)
                {
                    var player = Contexts.IsActive(c) ? Contexts.GetValue(c, "GameManager.localPlayerInfo") as PlayerInfo : null;
                    if (player == null || player.Movement == null)
                    {
                        continue;
                    }
                    bool ignore = !player.IsInJumboBurgerGiantForm;
                    var key = (cart.GetInstanceID(), player.GetInstanceID());
                    if (giantColliderIgnores.TryGetValue(key, out bool current) && current == ignore)
                    {
                        continue;
                    }
                    Physics.IgnoreCollision(giantsOnly, player.Movement.UprightCollider, ignore);
                    Physics.IgnoreCollision(giantsOnly, player.Movement.DivingCollider, ignore);
                    giantColliderIgnores[key] = ignore;
                }
            }
            if (giantColliderIgnores.Count > 64)
            {
                var live = new HashSet<int>(carts.Select(cart => cart.GetInstanceID()));
                foreach (var stale in giantColliderIgnores.Keys.Where(k => !live.Contains(k.Cart)).ToList())
                {
                    giantColliderIgnores.Remove(stale);
                }
            }
        }
    }
}
