using System;
using System.Reflection;
using System.Reflection.Emit;

namespace SBGSplitscreen
{
    /// <summary>
    /// Compiled field getters and setters. Context switches read and write dozens of fields
    /// many times per frame, which is too slow through FieldInfo.GetValue/SetValue.
    /// </summary>
    internal static class FastAccess
    {
        public static Func<object> StaticGetter(FieldInfo field)
        {
            var dm = new DynamicMethod($"get_{field.DeclaringType.Name}_{field.Name}", typeof(object), Type.EmptyTypes, typeof(FastAccess).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldsfld, field);
            if (field.FieldType.IsValueType)
            {
                il.Emit(OpCodes.Box, field.FieldType);
            }
            il.Emit(OpCodes.Ret);
            return (Func<object>)dm.CreateDelegate(typeof(Func<object>));
        }

        public static Action<object> StaticSetter(FieldInfo field)
        {
            var dm = new DynamicMethod($"set_{field.DeclaringType.Name}_{field.Name}", null, new[] { typeof(object) }, typeof(FastAccess).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(field.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, field.FieldType);
            il.Emit(OpCodes.Stsfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<object>)dm.CreateDelegate(typeof(Action<object>));
        }

        public static Func<object, object> InstanceGetter(FieldInfo field)
        {
            var dm = new DynamicMethod($"get_{field.DeclaringType.Name}_{field.Name}", typeof(object), new[] { typeof(object) }, typeof(FastAccess).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            // Boxed structs (async state machines) are read in place through the box.
            il.Emit(field.DeclaringType.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldfld, field);
            if (field.FieldType.IsValueType)
            {
                il.Emit(OpCodes.Box, field.FieldType);
            }
            il.Emit(OpCodes.Ret);
            return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
        }

        public static Action<object, object> InstanceSetter(FieldInfo field)
        {
            var dm = new DynamicMethod($"set_{field.DeclaringType.Name}_{field.Name}", null, new[] { typeof(object), typeof(object) }, typeof(FastAccess).Module, true);
            var il = dm.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, field.DeclaringType);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(field.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, field.FieldType);
            il.Emit(OpCodes.Stfld, field);
            il.Emit(OpCodes.Ret);
            return (Action<object, object>)dm.CreateDelegate(typeof(Action<object, object>));
        }

        public static object DefaultOf(Type type)
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}
