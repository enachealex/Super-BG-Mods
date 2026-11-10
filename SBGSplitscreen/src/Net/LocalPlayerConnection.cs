using System;
using Mirror;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// Server-side connection for a player sitting at this same PC. Host-mode objects are shared
    /// in-process, so nothing ever needs to go over a wire: sends are dropped, and targeted RPCs
    /// for this connection are run locally instead (see TargetRpcForwardPatch).
    /// </summary>
    internal sealed class LocalPlayerConnection : NetworkConnectionToClient
    {
        /// <summary>Player GUIDs for local players. Far below real Steam IDs, which start at 7656119...</summary>
        public const ulong GuidBase = 0x00F0_0000_0000_0000UL;

        public readonly int Slot;
        public readonly ulong Guid;

        public LocalPlayerConnection(int slot)
            : base(-slot, $"splitscreen-p{slot + 1}")
        {
            Slot = slot;
            Guid = GuidBase + (ulong)slot;
            isAuthenticated = true;
        }

        public static bool IsLocalGuid(ulong guid) => guid > GuidBase && guid < GuidBase + Contexts.MaxPlayers;

        public static int SlotOfGuid(ulong guid) => IsLocalGuid(guid) ? (int)(guid - GuidBase) : -1;

        protected override void SendToTransport(ArraySegment<byte> segment, int channelId = 0)
        {
        }

        protected override void UpdatePing()
        {
            // No pings will ever come back; keep the inactivity timeout from kicking this player.
            lastMessageTime = Time.time;
        }

        public override void Disconnect()
        {
            isReady = false;
            Splitscreen.OnConnectionDisconnected(this);
        }
    }
}
