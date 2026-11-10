using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace SBGSplitscreen
{
    /// <summary>
    /// Makes every instance method of a "per-player" type run inside the context of the player
    /// that owns the instance. Covers Unity messages, the game's BUpdate callbacks, input action
    /// callbacks, Mirror hooks and RPC bodies alike, without listing them one by one. Coroutine
    /// bodies are covered through their compiler-generated MoveNext.
    /// </summary>
    internal static class ContextPatcher
    {
        private static readonly HashSet<Type> patchedTypes = new HashSet<Type>();
        private static readonly Dictionary<Type, Func<object, object>> iteratorOwnerGetters = new Dictionary<Type, Func<object, object>>();

        private static readonly HashSet<string> SkippedNames = new HashSet<string>
        {
            "SerializeSyncVars", "DeserializeSyncVars", "Weaved", "OnSerialize", "OnDeserialize",
            "ToString", "GetHashCode", "Equals", "Finalize", "OnValidate", "Reset"
        };

        private static Harmony harmony;
        private static HarmonyMethod prefix;
        private static HarmonyMethod finalizer;
        private static HarmonyMethod iteratorPrefix;

        public static void Init(Harmony h)
        {
            harmony = h;
            prefix = new HarmonyMethod(AccessTools.Method(typeof(ContextPatcher), nameof(EnterPrefix)));
            finalizer = new HarmonyMethod(AccessTools.Method(typeof(ContextPatcher), nameof(RestoreFinalizer)));
            iteratorPrefix = new HarmonyMethod(AccessTools.Method(typeof(ContextPatcher), nameof(IteratorEnterPrefix)));
        }

        public static bool IsPatched(Type type) => patchedTypes.Contains(type);

        private static readonly string[] LifecycleMethods = { "Awake", "Start", "OnEnable", "OnDisable", "OnDestroy" };
        private static readonly HashSet<MethodBase> lifecyclePatched = new HashSet<MethodBase>();

        /// <summary>
        /// For shared objects that subscribe to per-player events when they wake up or switch on
        /// (menus, prompts, managers): run just those methods in the owner's context, which for a
        /// shared object is player 1's. Otherwise a menu switched on while player 2's context is
        /// live would subscribe to player 2's events and unsubscribe from player 1's, leaving a
        /// dangling handler behind.
        /// </summary>
        public static void PatchLifecycle(IEnumerable<Type> types)
        {
            int count = 0;
            foreach (var type in types)
            {
                if (type == null || patchedTypes.Contains(type))
                {
                    continue;
                }
                foreach (string name in LifecycleMethods)
                {
                    // Plain reflection: AccessTools logs a warning for every type that lacks the method.
                    var method = type.GetMethod(name, BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                    if (method == null || method.IsStatic || method.GetParameters().Length != 0 || !lifecyclePatched.Add(method))
                    {
                        continue;
                    }
                    try
                    {
                        harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                        count++;
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogDebug($"Could not lifecycle-patch {type.Name}.{name}: {e.Message}");
                    }
                }
            }
            Plugin.Log.LogInfo($"Lifecycle-patched {count} methods.");
        }

        public static void PatchTypes(IEnumerable<Type> types)
        {
            var watch = Stopwatch.StartNew();
            int methods = 0;
            int typeCount = 0;
            foreach (var type in types)
            {
                if (type == null || !patchedTypes.Add(type))
                {
                    continue;
                }
                typeCount++;
                methods += PatchType(type);
            }
            if (typeCount > 0)
            {
                Plugin.Log.LogInfo($"Context-patched {methods} methods on {typeCount} types in {watch.ElapsedMilliseconds} ms.");
            }
        }

        private static int PatchType(Type type)
        {
            int count = 0;
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var method in type.GetMethods(flags))
            {
                if (!ShouldPatch(method) || lifecyclePatched.Contains(method))
                {
                    continue;
                }
                try
                {
                    harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                    count++;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogDebug($"Could not context-patch {type.Name}.{method.Name}: {e.Message}");
                }
            }

            return count + PatchCompilerGenerated(type);
        }

        /// <summary>
        /// Coroutines and async methods: the code after a yield or await is resumed later by Unity
        /// or UniTask from outside any player's context, through the compiler-generated MoveNext.
        /// Closures: lambdas and local functions that capture locals live on a generated class, and
        /// a coroutine written as such a local function (the respawn routine) nests its state
        /// machine inside that class. Run all of these in the owner's context too, following
        /// "&lt;&gt;4__this" from state machine to closure to the component.
        /// </summary>
        private static int PatchCompilerGenerated(Type type)
        {
            int count = 0;
            foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (nested.IsGenericTypeDefinition)
                {
                    continue;
                }
                var thisField = nested.GetField("<>4__this", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (thisField == null)
                {
                    continue;
                }
                bool isCoroutine = nested.IsClass && typeof(IEnumerator).IsAssignableFrom(nested);
                bool isAsync = typeof(System.Runtime.CompilerServices.IAsyncStateMachine).IsAssignableFrom(nested);
                bool isClosure = nested.IsClass && !isCoroutine && nested.Name.StartsWith("<>c__DisplayClass");
                try
                {
                    if (isCoroutine || isAsync)
                    {
                        var moveNext = AccessTools.Method(nested, "MoveNext");
                        if (moveNext != null)
                        {
                            iteratorOwnerGetters[nested] = FastAccess.InstanceGetter(thisField);
                            // Async state machines are structs: the prefix has to take them by
                            // reference, which needs a prefix closed over that exact struct type.
                            var enter = nested.IsValueType
                                ? new HarmonyMethod(structEnterPrefix.MakeGenericMethod(nested))
                                : iteratorPrefix;
                            harmony.Patch(moveNext, prefix: enter, finalizer: finalizer);
                            count++;
                        }
                    }
                    else if (isClosure)
                    {
                        iteratorOwnerGetters[nested] = FastAccess.InstanceGetter(thisField);
                        const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                        foreach (var method in nested.GetMethods(flags))
                        {
                            if (ShouldPatch(method))
                            {
                                harmony.Patch(method, prefix: iteratorPrefix, finalizer: finalizer);
                                count++;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Could not context-patch {type.Name}.{nested.Name}: {e.Message}");
                }
                count += PatchCompilerGenerated(nested);
            }
            return count;
        }

        /// <summary>The component behind a state machine or closure, through any closures in between.</summary>
        private static object ResolveOwner(object obj)
        {
            for (int depth = 0; depth < 4 && obj != null; depth++)
            {
                if (!iteratorOwnerGetters.TryGetValue(obj.GetType(), out var getOwner))
                {
                    break;
                }
                obj = getOwner(obj);
            }
            return obj;
        }

        private static bool ShouldPatch(MethodInfo method)
        {
            if (method.IsAbstract || method.IsGenericMethodDefinition || method.ContainsGenericParameters)
            {
                return false;
            }
            if (method.GetMethodBody() == null || SkippedNames.Contains(method.Name))
            {
                return false;
            }
            // Accessors only read or write the instance; they never need another player's statics.
            if (method.IsSpecialName && (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")
                || method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")))
            {
                return false;
            }
            foreach (var p in method.GetParameters())
            {
                if (p.ParameterType.IsByRef && p.ParameterType.GetElementType().IsByRefLike)
                {
                    return false;
                }
            }
            return true;
        }

        private static void EnterPrefix(object __instance, out int __state)
        {
            __state = Contexts.EnterFor(__instance);
        }

        private static void IteratorEnterPrefix(object __instance, out int __state)
        {
            __state = -1;
            if (Contexts.ExtraCount == 0 || __instance == null)
            {
                return;
            }
            if (iteratorOwnerGetters.TryGetValue(__instance.GetType(), out var getOwner))
            {
                __state = Contexts.EnterFor(ResolveOwner(getOwner(__instance)));
            }
        }

        private static readonly MethodInfo structEnterPrefix = AccessTools.Method(typeof(ContextPatcher), nameof(StructEnterPrefix));

        private static void StructEnterPrefix<T>(ref T __instance, out int __state) where T : struct
        {
            __state = -1;
            if (Contexts.ExtraCount == 0)
            {
                return;
            }
            if (iteratorOwnerGetters.TryGetValue(typeof(T), out var getOwner))
            {
                // Boxing copies the state machine; only its "this" reference is read from the copy.
                __state = Contexts.EnterFor(ResolveOwner(getOwner(__instance)));
            }
        }

        private static void RestoreFinalizer(int __state)
        {
            Contexts.Restore(__state);
        }
    }
}
