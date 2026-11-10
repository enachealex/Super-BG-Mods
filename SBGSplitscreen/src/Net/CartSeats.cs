using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mirror;

namespace SBGSplitscreen
{
    /// <summary>
    /// Runs a stretch of code as one player, pinned so that shared objects it calls into (a golf
    /// cart, which normally runs as whoever drives it) do not switch to their own owner. Cart
    /// commands sent from this PC arrive with no sender and work out who sent them from "the local
    /// player", so they must run as the player who sent them.
    /// </summary>
    internal static class ActAs
    {
        public const int None = -2;

        /// <summary>Enters <paramref name="slot"/> and pins it; returns the state to pass to <see cref="End"/>.</summary>
        public static int Begin(int slot)
        {
            if (Contexts.ExtraCount == 0 || slot < 0)
            {
                return None;
            }
            int state = (Contexts.Pinned ? 1 : 0) | ((Contexts.Enter(slot) + 1) << 1);
            Contexts.Pinned = true;
            return state;
        }

        public static void End(int state)
        {
            if (state == None)
            {
                return;
            }
            Contexts.Pinned = (state & 1) != 0;
            Contexts.Restore((state >> 1) - 1);
        }

        /// <summary>The slot of a player on this PC, or -1 for someone playing online (or nobody).</summary>
        public static int SlotOf(PlayerInfo player)
        {
            if (player == null)
            {
                return -1;
            }
            var conn = player.connectionToClient;
            return conn is LocalPlayerConnection local ? local.Slot : conn != null && conn == NetworkServer.localConnection ? 0 : -1;
        }

        /// <summary>
        /// The owner of a component, or of the component behind a compiler-generated closure
        /// (local functions that capture variables live on a helper object that points back at it).
        /// </summary>
        public static int OwnerOf(object instance)
        {
            for (int depth = 0; depth < 4 && instance != null; depth++)
            {
                if (instance is UnityEngine.Component)
                {
                    return Owners.Of(instance);
                }
                var outer = instance.GetType().GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                instance = outer?.GetValue(instance);
            }
            return -1;
        }
    }

    /// <summary>
    /// Putting someone in a golf cart seat is server code on the cart, which ends by telling "the
    /// local player" to take the seat: directly for the host's own player, by message for anyone
    /// else. Cart code normally runs as whoever drives the cart, so with splitscreen the seat could
    /// go to the wrong player: player 1 was pulled into a cart from anywhere on the map when player 3
    /// got in, or a driving player 2 was moved when player 1 hopped on. Seating now runs as the
    /// player being seated.
    /// </summary>
    [HarmonyPatch(typeof(GolfCartInfo), "ServerTryAssignPassengerToSeat")]
    internal static class CartSeats
    {
        private static void Prefix(PlayerInfo passenger, out int __state) => __state = ActAs.Begin(ActAs.SlotOf(passenger));

        private static void Finalizer(int __state) => ActAs.End(__state);
    }

    /// <summary>
    /// Code a player runs that talks to a golf cart: pressing interact on it (or on a ball
    /// dispenser, item dispenser or item), getting out, changing seats, driving, and climbing into
    /// a cart they just unpacked from the golf cart item. Each of these runs entirely as the
    /// player who did it. Before, getting out of a cart someone else drove was taken as player 1
    /// getting out: the cart kept the joined player in their old seat, and over time carts filled
    /// up with players who were not there and could no longer be entered. Driving input was
    /// likewise refused unless the cart happened to run as the driver.
    /// </summary>
    [HarmonyPatch]
    internal static class PlayerActsOnCart
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerInteractor), nameof(PlayerInteractor.TryInteract));
            yield return AccessTools.Method(typeof(PlayerInfo), nameof(PlayerInfo.ExitGolfCart));
            yield return AccessTools.Method(typeof(PlayerInfo), nameof(PlayerInfo.TrySwitchGolfCartSeatTo));
            foreach (var method in LocalFunctions(typeof(PlayerInput), "<Update>g__UpdateGolfCartMovementInput|")
                .Concat(LocalFunctions(typeof(PlayerInventory), "<PlaceGolfCartRoutine>g__TryEnterReservedGolfCart|")))
            {
                yield return method;
            }
        }

        private static IEnumerable<MethodBase> LocalFunctions(Type type, string prefix)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var found = new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                .SelectMany(t => t.GetMethods(all))
                .Where(m => m.Name.StartsWith(prefix))
                .Cast<MethodBase>().ToList();
            if (found.Count == 0)
            {
                Plugin.Log.LogWarning($"Could not find {type.Name}.{prefix.TrimEnd('|')}; golf carts may not work for joined players.");
            }
            return found;
        }

        private static void Prefix(object __instance, out int __state) => __state = ActAs.Begin(ActAs.OwnerOf(__instance));

        private static void Finalizer(int __state) => ActAs.End(__state);
    }

    /// <summary>
    /// Golf cart item: the cart that unfolds is reserved for the player who used it. Setting it up
    /// asks whether that player "is the local player", which on this PC is only true for whoever's
    /// turn it happens to be. A joined player's cart was set up as player 1's, so they could not
    /// drive it, and their game never learned the cart was theirs to climb into. Both now go by the
    /// player's connection.
    /// </summary>
    [HarmonyPatch]
    internal static class ReservedCarts
    {
        private static readonly Action<GolfCartInfo, SyncDirection> setSyncDirection =
            AccessTools.MethodDelegate<Action<GolfCartInfo, SyncDirection>>(AccessTools.Method(typeof(GolfCartInfo), "ServerSetMovementSyncDirectionForAllClients"));

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GolfCartInfo), nameof(GolfCartInfo.ServerReserveDriverSeatPostNetworkSpawn))]
        private static bool HandOverToReserver(GolfCartInfo __instance)
        {
            var reserver = __instance.NetworkdriverSeatReserver;
            if (!NetworkServer.active || reserver == null || ActAs.SlotOf(reserver) < 0)
            {
                return true;
            }
            if (reserver.connectionToClient is LocalPlayerConnection conn)
            {
                setSyncDirection(__instance, SyncDirection.ClientToServer);
                __instance.netIdentity.AssignClientAuthority(conn);
            }
            else
            {
                // The host's own player.
                setSyncDirection(__instance, SyncDirection.ServerToClient);
                __instance.netIdentity.RemoveClientAuthority();
            }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GolfCartInfo), nameof(GolfCartInfo.OnStartClient))]
        private static void StartAsReserver(GolfCartInfo __instance, out int __state)
        {
            __state = ActAs.Begin(ActAs.SlotOf(__instance.NetworkdriverSeatReserver));
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(GolfCartInfo), nameof(GolfCartInfo.OnStartClient))]
        private static void StartAsReserverEnd(int __state) => ActAs.End(__state);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(GolfCartInfo), "OnDriverSeatReserverChanged")]
        private static void ReserverChanged(PlayerInfo previousReserver, PlayerInfo currentReserver, out int __state)
        {
            int slot = ActAs.SlotOf(currentReserver);
            __state = ActAs.Begin(slot >= 0 ? slot : ActAs.SlotOf(previousReserver));
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(GolfCartInfo), "OnDriverSeatReserverChanged")]
        private static void ReserverChangedEnd(int __state) => ActAs.End(__state);
    }
}
