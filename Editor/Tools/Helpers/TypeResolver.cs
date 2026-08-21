// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using KitWright.Editor.Tools.Builtins;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Helpers
{
    /// <summary>
    /// O(1) type lookup for UnityEngine.Object-derived types (components, assets, scriptable objects).
    /// Built once from <see cref="TypeCache"/> on first access — replaces per-call AppDomain scans.
    /// Names lookup is case-insensitive.
    /// </summary>
    public static class TypeResolver
    {
        private static Dictionary<string, List<Type>> s_byName;
        private static Dictionary<string, Type> s_byFullName;
        private static readonly object s_lock = new object();

        private static void EnsureBuilt()
        {
            if (s_byName != null)
                return;

            lock (s_lock)
            {
                if (s_byName != null)
                    return;

                var byName = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
                var byFullName = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

                foreach (var type in TypeCache.GetTypesDerivedFrom<UnityEngine.Object>())
                {
                    if (type == null)
                        continue;

                    if (!string.IsNullOrEmpty(type.Name))
                    {
                        if (!byName.TryGetValue(type.Name, out var sameName))
                        {
                            sameName = new List<Type>();
                            byName.Add(type.Name, sameName);
                        }

                        sameName.Add(type);
                    }

                    if (!string.IsNullOrEmpty(type.FullName) && !byFullName.ContainsKey(type.FullName))
                        byFullName.Add(type.FullName, type);
                }

                s_byName = byName;
                s_byFullName = byFullName;
            }
        }

        /// <summary>
        /// Resolve a type name (short or fully qualified) to a UnityEngine.Object-derived <see cref="Type"/>.
        /// Returns null if not found, or if a short name is shared by types no preference rule settles between.
        /// </summary>
        public static Type Resolve(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            EnsureBuilt();

            if (s_byFullName.TryGetValue(typeName, out var t))
                return t;
            if (s_byName.TryGetValue(typeName, out var sameName))
            {
                if (sameName.Count == 1)
                    return sameName[0];

                // Same preference rules as reflect_api (top-level over nested, Unity's own type over
                // a namesake) rather than a third copy of them; a pick from outside this index — it
                // covers every public type, not just UnityEngine.Object ones — settles nothing here.
                var picked = ReflectionFunctions.Resolve(typeName, out _, out var ambiguous);
                return !ambiguous && picked != null && sameName.Contains(picked) ? picked : null;
            }

            // UnityEngine.X shorthand
            if (!typeName.Contains("."))
            {
                if (s_byFullName.TryGetValue("UnityEngine." + typeName, out t))
                    return t;
                if (s_byFullName.TryGetValue("UnityEngine.UI." + typeName, out t))
                    return t;
                if (s_byFullName.TryGetValue("UnityEngine.EventSystems." + typeName, out t))
                    return t;
            }

            return null;
        }

        /// <summary>
        /// Resolve a Component type by name. Returns null if the name resolves to a non-Component type.
        /// </summary>
        public static Type ResolveComponent(string typeName)
        {
            var type = Resolve(typeName);
            return type != null && typeof(Component).IsAssignableFrom(type) ? type : null;
        }

        /// <summary>
        /// The error for a name <see cref="Resolve"/> returned null for. A short name shared by
        /// several loaded types is refused as ambiguous with the full names listed, so the caller
        /// is not told a type it can see in its own project does not exist.
        /// </summary>
        internal static object UnresolvedError(string typeName, string notFoundCode, string paramName)
        {
            var candidates = AmbiguousCandidates(typeName);
            if (candidates != null)
            {
                return Response.Error(
                    "AMBIGUOUS_TYPE",
                    new { param = paramName, value = typeName, candidates },
                    "Pass one of the candidates as the fully qualified name.");
            }

            return Response.Error(notFoundCode, new { param = paramName, value = typeName });
        }

        /// <summary>
        /// The full names a short name is shared by, or null when it is unambiguous or unknown.
        /// For callers that cannot return a <see cref="Response"/> and have to raise instead.
        /// </summary>
        internal static string[] AmbiguousCandidates(string typeName)
        {
            EnsureBuilt();

            if (!string.IsNullOrEmpty(typeName) &&
                s_byName.TryGetValue(typeName, out var sameName) &&
                sameName.Count > 1)
            {
                return sameName.Select(t => t.FullName).OrderBy(n => n).ToArray();
            }

            return null;
        }
    }
}
