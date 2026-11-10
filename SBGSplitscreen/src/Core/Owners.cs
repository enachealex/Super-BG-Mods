using System.Runtime.CompilerServices;
using Mirror;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>Marks a GameObject hierarchy as belonging to a joined player's context.</summary>
    public sealed class SplitscreenOwner : MonoBehaviour
    {
        public int Slot;
    }

    /// <summary>
    /// Which context a component belongs to: its SplitscreenOwner (a joined player's character,
    /// camera or HUD); else whichever joined player the network currently gives control of the
    /// object to (a golf cart they drive, a rocket they fired); else player 1.
    /// </summary>
    internal static class Owners
    {
        private sealed class Info
        {
            public int Slot;
            public NetworkIdentity Identity;
            public Transform Transform;
            public Transform Root;
        }

        private static readonly ConditionalWeakTable<object, Info> cache = new ConditionalWeakTable<object, Info>();

        /// <summary>Lookups so far, for the debug status.</summary>
        public static long Lookups;

        /// <summary>Owning context, or -1 when the object is not a live component.</summary>
        public static int Of(object obj)
        {
            Lookups++;
            if (!(obj is Component component))
            {
                return -1;
            }
            if (cache.TryGetValue(obj, out Info info))
            {
                // Pooled HUD pieces (lock-on markers, button hints) are made without a parent and
                // only then moved under a player's HUD; look again once an object has changed hierarchy.
                var t = info.Transform;
                if (t != null && t.root != info.Root)
                {
                    Resolve(component, info);
                }
            }
            else
            {
                if (component == null)
                {
                    return -1;
                }
                info = new Info();
                Resolve(component, info);
                cache.Add(obj, info);
            }
            // Control of an object can move between players (a golf cart changes drivers), so this
            // part is looked up every time rather than cached.
            if (info.Identity is object && info.Identity != null && info.Identity.connectionToClient is LocalPlayerConnection conn)
            {
                return conn.Slot;
            }
            return info.Slot;
        }

        private static void Resolve(Component component, Info info)
        {
            var owner = component.GetComponentInParent<SplitscreenOwner>(true);
            info.Slot = owner != null ? owner.Slot : 0;
            info.Identity = owner != null ? null : component.GetComponentInParent<NetworkIdentity>(true);
            info.Transform = component.transform;
            info.Root = info.Transform.root;
        }

        /// <summary>For components that were looked up before their owner marker existed.</summary>
        public static void Forget(object obj)
        {
            cache.Remove(obj);
        }
    }
}
