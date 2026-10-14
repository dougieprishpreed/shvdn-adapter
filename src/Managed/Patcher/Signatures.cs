using System;
using System.Linq;
using System.Text;
using Mono.Cecil;

namespace ShvdnCompat
{
    /// <summary>
    /// Signature comparison that matches how the CLR binds MemberRefs: by name, calling convention, return
    /// type and parameter types (including custom modifiers), without resolving other assemblies.
    /// </summary>
    internal static class Signatures
    {
        public static string Of(TypeReference type)
        {
            var sb = new StringBuilder();
            Append(sb, type);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, TypeReference type)
        {
            switch (type)
            {
                case null:
                    sb.Append("<null>");
                    return;
                case GenericParameter gp:
                    sb.Append(gp.Type == GenericParameterType.Method ? "!!" : "!").Append(gp.Position);
                    return;
                case ArrayType at:
                    Append(sb, at.ElementType);
                    sb.Append(at.IsVector ? "[]" : "[" + new string(',', at.Rank - 1) + "]");
                    return;
                case ByReferenceType br:
                    Append(sb, br.ElementType);
                    sb.Append('&');
                    return;
                case PointerType pt:
                    Append(sb, pt.ElementType);
                    sb.Append('*');
                    return;
                case PinnedType pinned:
                    Append(sb, pinned.ElementType);
                    sb.Append(" pinned");
                    return;
                case RequiredModifierType req:
                    Append(sb, req.ElementType);
                    sb.Append(" modreq(");
                    Append(sb, req.ModifierType);
                    sb.Append(')');
                    return;
                case OptionalModifierType opt:
                    Append(sb, opt.ElementType);
                    sb.Append(" modopt(");
                    Append(sb, opt.ModifierType);
                    sb.Append(')');
                    return;
                case GenericInstanceType git:
                    Append(sb, git.ElementType);
                    sb.Append('<');
                    for (int i = 0; i < git.GenericArguments.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Append(sb, git.GenericArguments[i]);
                    }
                    sb.Append('>');
                    return;
                case FunctionPointerType fp:
                    sb.Append("method ");
                    Append(sb, fp.ReturnType);
                    sb.Append(" *(");
                    for (int i = 0; i < fp.Parameters.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Append(sb, fp.Parameters[i].ParameterType);
                    }
                    sb.Append(')');
                    return;
                default:
                    // Normalize nested-type separators so references and definitions compare equal.
                    sb.Append(type.FullName);
                    return;
            }
        }

        public static string Of(MethodReference method)
        {
            var sb = new StringBuilder();
            if (method.HasThis) sb.Append("instance ");
            Append(sb, method.ReturnType);
            sb.Append(' ').Append(method.Name);
            if (method.HasGenericParameters) sb.Append('`').Append(method.GenericParameters.Count);
            sb.Append('(');
            for (int i = 0; i < method.Parameters.Count; i++)
            {
                if (i > 0) sb.Append(',');
                Append(sb, method.Parameters[i].ParameterType);
            }
            sb.Append(')');
            return sb.ToString();
        }

        /// <summary>Finds a type definition in <paramref name="module"/> by CLR full name ('/' for nesting).</summary>
        public static TypeDefinition FindType(ModuleDefinition module, string fullName)
        {
            string[] parts = fullName.Split('/');
            TypeDefinition current = module.Types.FirstOrDefault(t => t.FullName == parts[0]);
            for (int i = 1; current != null && i < parts.Length; i++)
            {
                current = current.NestedTypes.FirstOrDefault(t => t.Name == parts[i]);
            }
            return current;
        }

        /// <summary>Finds the definition a type reference names inside <paramref name="module"/>.</summary>
        public static TypeDefinition FindType(ModuleDefinition module, TypeReference reference)
        {
            if (reference.DeclaringType != null)
            {
                TypeDefinition outer = FindType(module, reference.DeclaringType);
                return outer?.NestedTypes.FirstOrDefault(t => t.Name == reference.Name);
            }
            return module.Types.FirstOrDefault(t => t.Namespace == reference.Namespace && t.Name == reference.Name);
        }

        public static MethodDefinition FindMethod(TypeDefinition type, string signature)
        {
            return type.Methods.FirstOrDefault(m => Of(m) == signature);
        }

        public static string Describe(MemberReference member)
        {
            return member.FullName;
        }

        public static bool IsApiAssemblyName(string name, out int major)
        {
            major = 0;
            if (string.Equals(name, "ScriptHookVDotNet2", StringComparison.OrdinalIgnoreCase)) major = 2;
            else if (string.Equals(name, "ScriptHookVDotNet3", StringComparison.OrdinalIgnoreCase)) major = 3;
            return major != 0;
        }
    }
}
