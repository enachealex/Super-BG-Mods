using HarmonyLib;
using Mirror;

namespace SBGSplitscreen
{
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
        private static void Prefix(PlayerInfo passenger, out int __state)
        {
            __state = -2;
            if (Contexts.ExtraCount == 0 || passenger == null)
            {
                return;
            }
            var conn = passenger.connectionToClient;
            int slot = conn is LocalPlayerConnection local ? local.Slot : conn != null && conn == NetworkServer.localConnection ? 0 : -1;
            if (slot < 0)
            {
                // Someone playing online: the game's own message reaches them.
                return;
            }
            __state = (Contexts.Pinned ? 1 : 0) | ((Contexts.Enter(slot) + 1) << 1);
            Contexts.Pinned = true;
        }

        private static void Finalizer(int __state)
        {
            if (__state == -2)
            {
                return;
            }
            Contexts.Pinned = (__state & 1) != 0;
            Contexts.Restore((__state >> 1) - 1);
        }
    }

    /// <summary>
    /// Pressing the interact button on a cart, ball dispenser, item dispenser or item calls that
    /// object's "the local player interacts" code. Shared objects normally run as their owner
    /// (player 1 for an empty cart), so player 3 asking to get in was taken as player 1 asking,
    /// and player 1 was pulled into the cart from wherever they were. The interaction now runs
    /// entirely as the player who pressed the button.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInteractor), nameof(PlayerInteractor.TryInteract))]
    internal static class InteractAsPlayer
    {
        private static void Prefix(PlayerInteractor __instance, out int __state)
        {
            __state = -2;
            if (Contexts.ExtraCount == 0)
            {
                return;
            }
            int slot = Owners.Of(__instance);
            if (slot < 0)
            {
                return;
            }
            __state = (Contexts.Pinned ? 1 : 0) | ((Contexts.Enter(slot) + 1) << 1);
            Contexts.Pinned = true;
        }

        private static void Finalizer(int __state)
        {
            if (__state == -2)
            {
                return;
            }
            Contexts.Pinned = (__state & 1) != 0;
            Contexts.Restore((__state >> 1) - 1);
        }
    }
}