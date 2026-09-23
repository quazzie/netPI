using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace NetPI.Abstractions
{
    /// <summary>
    /// Deterministic identity of an assembly's PUBLIC API surface (the types a
    /// consumer can see and the signatures it can call). This is the shared
    /// contract: it does NOT depend on the git commit hash, the assembly
    /// version string, or the PDB — only on what plugins can actually call.
    /// A real contract change changes the id; a commit that touches nothing
    /// in the API does not.
    ///
    /// The host computes this for its loaded netPI.Abstractions assembly at
    /// plugin load time; the publisher (tools/publish-plugins.ps1) computes
    /// the SAME function against the host's Abstractions dll when publishing a
    /// plugin, and records the result in the artifact manifest. One source
    /// file is compiled into both sides (the Abstractions project and the
    /// publisher's Add-Type) so the two can never drift apart.
    ///
    /// NOTE: this file is compiled standalone by the publisher's Add-Type, so
    /// keep it dependency-free (System.Reflection + Cryptography only) and
    /// conservative in syntax.
    /// </summary>
    public static class ContractId
    {
        /// <summary>12-hex id of the assembly's public API surface.</summary>
        public static string Compute(Assembly assembly)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException rtle)
            {
                types = rtle.Types.Where(t => t != null).ToArray();
            }

            var lines = new List<string>();
            foreach (var t in types.Where(t => t.IsPublic).OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                lines.Add("T|" + Kind(t) + "|" + CanonicalName(t));
                var members = new List<string>();
                foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(m => Sig(m), StringComparer.Ordinal))
                    members.Add("C|" + (c.IsStatic ? "s" : "i") + "|" + Params(c));
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Where(m => !m.IsSpecialName).OrderBy(m => Sig(m), StringComparer.Ordinal))
                    members.Add("M|" + m.Name + "|" + (m.IsStatic ? "s" : "i") + "|" + F(m.ReturnType) + "|" + Params(m) + "|g" + (m.IsGenericMethod ? m.GetGenericMethodDefinition().GetGenericArguments().Length : 0));
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(p => p.Name, StringComparer.Ordinal))
                    members.Add("P|" + p.Name + "|" + F(p.PropertyType) + "|" + (p.CanRead ? "r" : "") + (p.CanWrite ? "w" : "") + (p.GetIndexParameters().Length > 0 ? "[" + string.Join(",", p.GetIndexParameters().Select(x => F(x.ParameterType))) + "]" : ""));
                foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(e => e.Name, StringComparer.Ordinal))
                    members.Add("E|" + e.Name + "|" + F(e.EventHandlerType));
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(f => f.Name, StringComparer.Ordinal))
                    members.Add("F|" + f.Name + "|" + F(f.FieldType) + "|" + (f.IsStatic ? "s" : "i") + (f.IsLiteral ? "lit" : ""));
                lines.AddRange(members);
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
            return Convert.ToHexString(hash).ToLowerInvariant().Substring(0, 12);
        }

        /// <summary>Compute against a dll file (publisher path). Framework refs resolve against the default ALC.</summary>
        public static string ComputeFromFile(string path)
        {
            return Compute(Assembly.LoadFile(new System.IO.FileInfo(path).FullName));
        }

        private static string Kind(Type t)
        {
            var k = t.IsInterface ? "iface" : t.IsEnum ? "enum" : typeof(Delegate).IsAssignableFrom(t) ? "delegate" : t.IsValueType ? "struct" : "class";
            if (t.IsAbstract && t.IsSealed) k += "+sealed";
            else if (t.IsAbstract && !t.IsValueType && !t.IsInterface) k += "+abstract";
            else if (!t.IsInterface && !t.IsEnum && !t.IsValueType && t.IsSealed) k += "+sealed";
            return k;
        }

        /// <summary>
        /// Canonical, version-free name: namespace + nested chain (with '+'),
        /// generic arity plus rendered arguments. Assembly identity is never
        /// part of it (that would embed the version/commit — the thing this
        /// id exists to exclude).
        /// </summary>
        private static string CanonicalName(Type t)
        {
            Type outer = t;
            while (outer.DeclaringType != null) outer = outer.DeclaringType;
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(outer.Namespace)) sb.Append(outer.Namespace).Append('.');
            var chain = new List<Type>();
            for (var c = t; c != null; c = c.DeclaringType) chain.Insert(0, c);
            for (var i = 0; i < chain.Count; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append(GenericName(chain[i]));
            }
            return sb.ToString();
        }

        private static string GenericName(Type t)
        {
            var n = t.Name;
            if (t.IsGenericType)
                n += "<" + string.Join(",", t.GetGenericArguments().Select(GenericName)) + ">";
            return n;
        }

        private static string F(Type t)
        {
            if (t == null) return "?";
            if (t == typeof(void)) return "void";
            if (t.IsByRef) return F(t.GetElementType()) + "&";
            if (t.IsArray) return F(t.GetElementType()) + "[" + new string(',', Math.Max(0, t.GetArrayRank() - 1)) + "]";
            return CanonicalName(t);
        }

        private static string Sig(MethodBase m) => m.Name + "|" + Params(m);

        private static string Params(MethodBase m)
        {
            return string.Join(",", m.GetParameters().Select(p =>
                F(p.ParameterType) + (p.IsOut ? "!" : "") + ":" + p.Name));
        }
    }
}
