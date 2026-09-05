// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KitWright.Editor.Tools
{
    /// Which registered tools cannot actually run here because an optional package is missing.
    ///
    /// A provider marked with <see cref="RequiresPackageAttribute"/> keeps compiling without its
    /// package -- its methods answer with a "not installed" error so a direct caller gets a hint
    /// rather than "unknown tool". Those tools must still stay out of the exported surface and out
    /// of the Tool Exposure switches, or a profile happily turns on tools that can only fail.
    internal static class ToolPackageGate
    {
        // Built on the main thread; tools/list can read it from the server's thread, which only
        // ever sees a finished dictionary.
        private static Dictionary<string, string> _missing = new Dictionary<string, string>();

        public static bool IsUnavailable(string toolName)
        {
            return toolName != null && _missing.ContainsKey(toolName);
        }

        public static string MissingPackageFor(string toolName)
        {
            return toolName != null && _missing.TryGetValue(toolName, out var package) ? package : null;
        }

        /// Runs on load and after every domain reload; call it directly once a package is installed
        /// so the gate reopens without waiting for one.
        [InitializeOnLoadMethod]
        public static void Invalidate()
        {
            var installed = new HashSet<string>(
                PackageInfo.GetAllRegisteredPackages().Select(package => package.name),
                StringComparer.OrdinalIgnoreCase);

            _missing = Compute(ToolRegistry.MethodCache.Keys, installed);
        }

        internal static Dictionary<string, string> Compute(
            IEnumerable<string> toolNames, ICollection<string> installedPackages)
        {
            var missing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var toolName in toolNames)
            {
                if (!ToolRegistry.MethodCache.TryGetValue(toolName, out var method))
                    continue;

                // A method's own requirement wins: a provider can be mostly package-free and still
                // have a handful of calls that go through the package.
                var required = method.GetCustomAttribute<RequiresPackageAttribute>()?.PackageId
                    ?? method.DeclaringType?.GetCustomAttribute<RequiresPackageAttribute>()?.PackageId;

                if (!string.IsNullOrEmpty(required) && !installedPackages.Contains(required))
                    missing[toolName] = required;
            }

            return missing;
        }
    }
}
