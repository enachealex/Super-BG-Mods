using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace SBGSplitscreen
{
    /// <summary>
    /// Objects whose world UI is per player (balls, holes, name tags, teammate icons, menu
    /// stations; see PerViewerUi) refresh that UI when "the local player" registers, starts
    /// spectating, gets a new ball, and so on. They subscribe to those notifications in whichever
    /// player's context they wake up in, so they would hear only that player's. Here their
    /// subscriptions are added to every player's notifications, including players who join later.
    /// Their handlers only refresh UI, which PerViewerUi then does for every player.
    /// </summary>
    internal static class EventBroadcast
    {
        private static readonly HashSet<Type> ListenerTypes = new HashSet<Type>
        {
            typeof(GolfBall), typeof(GolfHole), typeof(PlayerId), typeof(PlayerInfo),
            typeof(MenuOpenInteraction), typeof(BallDispenser), typeof(LocalSpectatorCameraFollower)
        };

        private static readonly Dictionary<MethodBase, string> fieldOfAccessor = new Dictionary<MethodBase, string>();
        private static bool busy;

        /// <summary>Hooks the add/remove accessors of every per-player event registered so far.</summary>
        public static void Init(Harmony harmony)
        {
            var add = new HarmonyMethod(AccessTools.Method(typeof(EventBroadcast), nameof(AddPostfix)));
            var remove = new HarmonyMethod(AccessTools.Method(typeof(EventBroadcast), nameof(RemovePostfix)));
            foreach (var (type, name, field) in Contexts.Events)
            {
                var evt = type.GetEvent(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var adder = evt?.GetAddMethod(true);
                var remover = evt?.GetRemoveMethod(true);
                if (adder == null || remover == null)
                {
                    continue;
                }
                fieldOfAccessor[adder] = field;
                fieldOfAccessor[remover] = field;
                harmony.Patch(adder, postfix: add);
                harmony.Patch(remover, postfix: remove);
            }
            Contexts.Activated += OnContextActivated;
        }

        private static void AddPostfix(Delegate value, MethodBase __originalMethod)
        {
            Apply(value, __originalMethod, Delegate.Combine);
        }

        private static void RemovePostfix(Delegate value, MethodBase __originalMethod)
        {
            Apply(value, __originalMethod, Delegate.Remove);
        }

        private static void Apply(Delegate value, MethodBase accessor, Func<Delegate, Delegate, Delegate> change)
        {
            if (busy || value == null || Contexts.ExtraCount == 0 || !IsListener(value)
                || !fieldOfAccessor.TryGetValue(accessor, out string field))
            {
                return;
            }
            busy = true;
            try
            {
                for (int c = 0; c < Contexts.MaxPlayers; c++)
                {
                    if (c != Contexts.Current && Contexts.IsActive(c))
                    {
                        Contexts.SetValue(c, field, change(Contexts.GetValue(c, field) as Delegate, value));
                    }
                }
            }
            finally
            {
                busy = false;
            }
        }

        /// <summary>A player who just joined hears everything the listeners already subscribed to elsewhere.</summary>
        private static void OnContextActivated(int context)
        {
            foreach (var (_, _, field) in Contexts.Events)
            {
                var handlers = new List<Delegate>();
                for (int c = 0; c < Contexts.MaxPlayers; c++)
                {
                    if (c == context || !Contexts.IsActive(c) || !(Contexts.GetValue(c, field) is Delegate d))
                    {
                        continue;
                    }
                    foreach (var handler in d.GetInvocationList())
                    {
                        if (IsListener(handler) && !handlers.Any(h => h.Target == handler.Target && h.Method == handler.Method))
                        {
                            handlers.Add(handler);
                        }
                    }
                }
                if (handlers.Count > 0)
                {
                    var existing = Contexts.GetValue(context, field) as Delegate;
                    Contexts.SetValue(context, field, Delegate.Combine(existing, Delegate.Combine(handlers.ToArray())));
                }
            }
        }

        private static bool IsListener(Delegate d)
        {
            foreach (var handler in d.GetInvocationList())
            {
                // Handlers can be methods of the listener or of a closure class nested inside it.
                for (var type = handler.Target?.GetType(); type != null; type = type.DeclaringType)
                {
                    if (ListenerTypes.Contains(type))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
