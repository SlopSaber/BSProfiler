using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace BSProfiler
{
    // Read managed delegate construction, without invoking mod code or walking live object graphs.
    internal static class CallbackDiscovery
    {
        private static readonly OpCode[] Codes = CreateCodes();

        public static void Collect(Type[] types, HashSet<MethodBase> targets, ref int failures)
        {
            foreach (Type type in types)
            {
                var methods = new List<MethodBase>();
                try
                {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    methods.AddRange(type.GetMethods(flags));
                    methods.AddRange(type.GetConstructors(flags));
                }
                catch { failures++; continue; }
                foreach (MethodBase method in methods)
                {
                    try
                    {
                        byte[]? il = method.GetMethodBody()?.GetILAsByteArray();
                        if (il == null) continue;
                        for (int offset = 0; offset < il.Length;)
                        {
                            int code = il[offset++];
                            if (code == 0xfe) code = 256 + il[offset++];
                            OpCode op = Codes[code];
                            if (op.Size == 0) throw new BadImageFormatException();
                            int size = OperandSize(op.OperandType, il, offset);
                            if (size < 0 || size > il.Length - offset) throw new BadImageFormatException();
                            if (op == OpCodes.Ldftn || op == OpCodes.Ldvirtftn)
                            {
                                MethodBase? target = method.Module.ResolveMethod(BitConverter.ToInt32(il, offset),
                                    type.GetGenericArguments(), method is MethodInfo source ? source.GetGenericArguments() : Type.EmptyTypes);
                                if (target is MethodInfo info && !info.ContainsGenericParameters &&
                                    info.DeclaringType?.ContainsGenericParameters == false) targets.Add(info);
                            }
                            offset += size;
                        }
                    }
                    catch { failures++; }
                }
            }
        }

        public static bool IsNamedHandler(MethodInfo method) => !method.IsSpecialName &&
            (method.Name.StartsWith("Handle", StringComparison.Ordinal) ||
             method.Name.StartsWith("On", StringComparison.Ordinal) ||
             method.Name.StartsWith("Refresh", StringComparison.Ordinal));

        private static OpCode[] CreateCodes()
        {
            var result = new OpCode[512];
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode op)
                {
                    int value = unchecked((ushort)op.Value);
                    result[value < 256 ? value : 256 + (value & 255)] = op;
                }
            return result;
        }

        private static int OperandSize(OperandType type, byte[] il, int offset)
        {
            switch (type)
            {
                case OperandType.InlineNone: return 0;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: return 1;
                case OperandType.InlineVar: return 2;
                case OperandType.InlineI8:
                case OperandType.InlineR: return 8;
                case OperandType.InlineSwitch: return checked(4 + 4 * BitConverter.ToInt32(il, offset));
                default: return 4;
            }
        }
    }
}
