// Copyright (C) KitWright. All rights reserved.
using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.PackageManager;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Package")]
    internal static class PackageFunctions
    {
        [Description("Install a Unity package by name. A git URL, tarball or local folder needs allow_remote_source: " +
                     "it is written into manifest.json, so it outlives the session and lands in version control.")]
        public static string InstallPackage(
            [ToolParam("Package identifier (e.g. 'com.unity.textmeshpro', 'com.unity.cinemachine')")] string package_id,
            [ToolParam("Allow a git URL, tarball or local path as the source", Required = false)] bool allow_remote_source = false)
        {
            if (!allow_remote_source && IsRemoteSource(package_id))
                return ToolResultFormatter.Error("REMOTE_PACKAGE_SOURCE", new { package_id },
                    "This installs from outside the package registry and writes that source into " +
                    "manifest.json, where it survives restarts and goes into version control. " +
                    "Pass allow_remote_source=true if that is what you mean.");

            // Resolve + import + domain reload stall while the editor is unfocused; keep it unthrottled.
            NoThrottleLease.Acquire(TimeSpan.FromMinutes(10));
            var request = Client.Add(package_id);
            // Note: Package installation is async in Unity, we just kick it off
            return $"Package installation initiated for '{package_id}'. Check Package Manager for status.";
        }

        /// <summary>
        /// Whether Client.Add would resolve this as something other than a registry package. A registry
        /// id is a reverse-DNS name with an optional @version; everything else - a git URL, a tarball,
        /// a file: path - fetches code Unity then compiles and runs.
        /// </summary>
        internal static bool IsRemoteSource(string packageId)
        {
            var id = (packageId ?? string.Empty).Trim();
            if (id.Length == 0)
                return false;

            foreach (var scheme in new[] { "http://", "https://", "git://", "git@", "ssh://", "file:" })
                if (id.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    return true;

            // A bare path, absolute or relative, is a local folder or tarball rather than an id.
            return id.Contains("/") || id.Contains("\\") || id.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        }

        [Description("Remove a Unity package")]
        public static string RemovePackage(
            [ToolParam("Package name to remove")] string package_name)
        {
            NoThrottleLease.Acquire(TimeSpan.FromMinutes(10));
            var request = Client.Remove(package_name);
            return $"Package removal initiated for '{package_name}'. Check Package Manager for status.";
        }

        [Description("List all installed packages")]
        [ReadOnlyTool]
        public static string ListPackages()
        {
            var manifestPath = "Packages/manifest.json";
            if (System.IO.File.Exists(manifestPath))
            {
                var content = System.IO.File.ReadAllText(manifestPath);
                if (content.Length > 5000)
                    content = content.Substring(0, 5000) + "\n... (truncated)";
                return $"Package manifest:\n{content}";
            }
            return "Package listing initiated. Check Package Manager window.";
        }
    }
}
