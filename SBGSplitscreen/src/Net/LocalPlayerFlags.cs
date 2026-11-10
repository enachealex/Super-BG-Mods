using Mirror;

namespace SBGSplitscreen
{
    /// <summary>
    /// "Is this the local player?" answered from the point of view of the live context. Each
    /// context has its own NetworkClient.localPlayer; on every switch, each local player's
    /// isLocalPlayer flag is set to whether its context is the live one. So in player 1's context
    /// player 2's golfer looks like any other golfer, and the other way round. That is exactly what
    /// the game's "my ball / my teammate / not me" checks need.
    /// The flag itself is rewritten rather than its getter patched: the getter is tiny and gets
    /// inlined into callers, where a patch would never be seen.
    /// </summary>
    internal static class LocalPlayerFlags
    {
        public const string Field = "NetworkClient.<localPlayer>k__BackingField";

        public static void Register()
        {
            Contexts.AddStaticProperty(typeof(NetworkClient), "localPlayer");
            Contexts.AfterSwitch += Apply;
        }

        /// <summary>Makes <paramref name="identity"/> the local player of <paramref name="context"/>.</summary>
        public static void Assign(int context, NetworkIdentity identity)
        {
            Contexts.SetValue(context, Field, identity);
            IdentityFlags.SetIsLocalPlayer(identity, Contexts.Current == context);
        }

        private static void Apply(int live)
        {
            for (int c = 0; c < Contexts.MaxPlayers; c++)
            {
                if (Contexts.IsActive(c) && Contexts.GetValue(c, Field) is NetworkIdentity identity && identity != null)
                {
                    IdentityFlags.SetIsLocalPlayer(identity, c == live);
                }
            }
        }
    }
}
