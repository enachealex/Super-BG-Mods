using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SBGSplitscreen
{
    /// <summary>
    /// The game keeps "the local player" in static fields: the local PlayerInfo, the camera rig,
    /// the input controls, the HUD singletons and the LocalPlayer* events. A context is one full
    /// set of those values. Context 0 is player 1 (the game as shipped); contexts 1-3 belong to
    /// players who joined with another controller. Switching contexts swaps every registered
    /// field, so the unmodified game code that runs next sees that player as "the local player".
    /// </summary>
    internal static class Contexts
    {
        public const int MaxPlayers = 4;

        private static readonly List<Slot> slots = new List<Slot>();
        private static readonly Dictionary<string, int> slotIndex = new Dictionary<string, int>();
        private static readonly object[][] stored = new object[MaxPlayers][];
        private static readonly bool[] active = new bool[MaxPlayers];
        private static bool frozen;

        /// <summary>The context whose values are currently live in the game's static fields.</summary>
        public static int Current { get; private set; }

        /// <summary>Number of joined players besides player 1. Zero means nothing is ever swapped.</summary>
        public static int ExtraCount { get; private set; }

        /// <summary>Runs after every switch, with the context that just became live.</summary>
        public static Action<int> AfterSwitch;

        /// <summary>
        /// While set, components do not switch to their owner's context. Used when code is run
        /// deliberately once per player (see PerViewerUi) and must stay in the chosen context.
        /// </summary>
        public static bool Pinned;

        public static bool IsActive(int context) => context >= 0 && context < MaxPlayers && active[context];

        // ---- registration (only before the first extra player joins) ----

        public static void AddStatic(Type type, string fieldName, Func<object> newContextValue = null)
        {
            var field = AccessTools.Field(type, fieldName);
            if (field == null || !field.IsStatic)
            {
                Plugin.Log.LogWarning($"Context field not found: {type.Name}.{fieldName}");
                return;
            }
            Add(new Slot($"{TypeName(type)}.{fieldName}", field.FieldType, FastAccess.StaticGetter(field), FastAccess.StaticSetter(field), newContextValue));
        }

        /// <summary>"SingletonBehaviour&lt;Hotkeys&gt;" rather than "SingletonBehaviour`1", which every singleton shares.</summary>
        public static string TypeName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }
            string name = type.Name.Substring(0, type.Name.IndexOf('`'));
            return $"{name}<{string.Join(",", Array.ConvertAll(type.GetGenericArguments(), TypeName))}>";
        }

        public static string SingletonField(Type singletonType) => $"SingletonBehaviour<{singletonType.Name}>.instance";

        /// <summary>A static auto-property, stored in its compiler-generated backing field.</summary>
        public static void AddStaticProperty(Type type, string propertyName, Func<object> newContextValue = null)
        {
            AddStatic(type, $"<{propertyName}>k__BackingField", newContextValue);
        }

        /// <summary>Per-player static events, by declaring type, event name and context field name.</summary>
        public static readonly List<(Type Type, string Event, string Field)> Events = new List<(Type, string, string)>();

        /// <summary>Runs after a context is created, with its index.</summary>
        public static Action<int> Activated;

        /// <summary>A field-like static event; its delegate lives in a static field of the same name.</summary>
        public static void AddStaticEvent(Type type, string eventName)
        {
            string field = $"{TypeName(type)}.{eventName}";
            if (!slotIndex.ContainsKey(field))
            {
                Events.Add((type, eventName, field));
            }
            AddStatic(type, eventName);
        }

        /// <summary>SingletonBehaviour&lt;T&gt;: each context gets its own instance (or none).</summary>
        public static void AddSingleton(Type singletonType)
        {
            Type closed = typeof(SingletonBehaviour<>).MakeGenericType(singletonType);
            AddStatic(closed, "instance");
            AddStaticProperty(closed, "HasInstance");
        }

        /// <summary>An instance field on the GameManager singleton, which itself is shared.</summary>
        public static void AddGameManagerField(string fieldName, Func<object> newContextValue = null)
        {
            var field = AccessTools.Field(typeof(GameManager), fieldName);
            if (field == null || field.IsStatic)
            {
                Plugin.Log.LogWarning($"GameManager field not found: {fieldName}");
                return;
            }
            var get = FastAccess.InstanceGetter(field);
            var set = FastAccess.InstanceSetter(field);
            object fallback = FastAccess.DefaultOf(field.FieldType);
            Add(new Slot($"GameManager.{fieldName}", field.FieldType,
                () => SingletonBehaviour<GameManager>.HasInstance ? get(SingletonBehaviour<GameManager>.Instance) : fallback,
                v =>
                {
                    if (SingletonBehaviour<GameManager>.HasInstance)
                    {
                        set(SingletonBehaviour<GameManager>.Instance, v);
                    }
                },
                newContextValue));
        }

        private static void Add(Slot slot)
        {
            if (slotIndex.ContainsKey(slot.Name))
            {
                return;
            }
            slotIndex[slot.Name] = slots.Count;
            slots.Add(slot);
            if (!frozen)
            {
                return;
            }
            // Registered after contexts exist (HUD types are only known once a game is loaded).
            // Until now the field was never swapped, so what it holds is player 1's value; every
            // other context starts fresh.
            object playerOneValue = slot.Get();
            for (int c = 0; c < MaxPlayers; c++)
            {
                if (stored[c] == null)
                {
                    continue;
                }
                var grown = new object[slots.Count];
                Array.Copy(stored[c], grown, stored[c].Length);
                grown[slots.Count - 1] = c == 0 ? playerOneValue : slot.NewContextValue();
                stored[c] = grown;
            }
            if (Current != 0)
            {
                slot.Set(stored[Current][slots.Count - 1]);
            }
        }

        public static bool Has(string name) => slotIndex.ContainsKey(name);

        // ---- lifecycle ----

        public static void Activate(int context)
        {
            Freeze();
            if (context <= 0 || context >= MaxPlayers || active[context])
            {
                return;
            }
            var values = new object[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                values[i] = slots[i].NewContextValue();
            }
            stored[context] = values;
            active[context] = true;
            ExtraCount++;
            Activated?.Invoke(context);
        }

        public static void Deactivate(int context)
        {
            if (context <= 0 || !IsActive(context))
            {
                return;
            }
            if (Current == context)
            {
                Switch(0);
            }
            active[context] = false;
            stored[context] = null;
            ExtraCount--;
        }

        private static void Freeze()
        {
            if (frozen)
            {
                return;
            }
            frozen = true;
            stored[0] = new object[slots.Count];
            active[0] = true;
            Plugin.Log.LogInfo($"Per-player context holds {slots.Count} fields.");
        }

        // ---- switching ----

        /// <summary>Makes <paramref name="to"/> live and returns the context that was live before.</summary>
        public static int Switch(int to)
        {
            int from = Current;
            if (to == from || !IsActive(to))
            {
                return from;
            }
            object[] save = stored[from];
            object[] load = stored[to];
            for (int i = 0; i < slots.Count; i++)
            {
                save[i] = slots[i].Get();
            }
            for (int i = 0; i < slots.Count; i++)
            {
                slots[i].Set(load[i]);
            }
            Current = to;
            AfterSwitch?.Invoke(to);
            return from;
        }

        /// <summary>Switches to the context that owns <paramref name="obj"/>. Returns -1 if nothing changed.</summary>
        public static int EnterFor(object obj)
        {
            if (ExtraCount == 0 || Pinned)
            {
                return -1;
            }
            int owner = Owners.Of(obj);
            if (owner < 0 || owner == Current || !IsActive(owner))
            {
                return -1;
            }
            return Switch(owner);
        }

        public static int Enter(int context)
        {
            if (context == Current || !IsActive(context))
            {
                return -1;
            }
            return Switch(context);
        }

        public static void Restore(int previous)
        {
            if (previous >= 0)
            {
                Switch(previous);
            }
        }

        // ---- reading and writing one field of any context ----

        public static object GetValue(int context, string name)
        {
            int i = slotIndex[name];
            return context == Current ? slots[i].Get() : stored[context]?[i];
        }

        public static void SetValue(int context, string name, object value)
        {
            int i = slotIndex[name];
            if (context == Current)
            {
                slots[i].Set(value);
            }
            else if (stored[context] != null)
            {
                stored[context][i] = value;
            }
        }

        private sealed class Slot
        {
            public readonly string Name;
            private readonly Func<object> getter;
            private readonly Action<object> setter;
            private readonly Func<object> factory;
            private readonly object defaultValue;

            public Slot(string name, Type type, Func<object> getter, Action<object> setter, Func<object> factory)
            {
                Name = name;
                this.getter = getter;
                this.setter = setter;
                this.factory = factory;
                defaultValue = FastAccess.DefaultOf(type);
            }

            public object Get() => getter();
            public void Set(object value) => setter(value);
            public object NewContextValue() => factory != null ? factory() : defaultValue;
        }
    }
}
