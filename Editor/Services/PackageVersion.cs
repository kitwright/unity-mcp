// Copyright (C) KitWright. All rights reserved.

using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KitWright.Editor.Services
{
    internal static class PackageVersion
    {
        private const string PackageName = "com.kitwright.unity.mcp";
        private const string AssetInstallRoot = "Assets/unity-mcp";
        private const string PackageInstallRoot = "Packages/com.kitwright.unity.mcp";
        private const string FallbackVersion = "0.0.0";
        private static string _cachedVersion;

        public static string Current
        {
            get
            {
                if (!string.IsNullOrEmpty(_cachedVersion))
                    return _cachedVersion;

                var version = ResolveVersion();
                // Never cache the fallback: an early call can run before the asset
                // database is queryable, and caching it would pin 0.0.0 for the session.
                if (version != FallbackVersion)
                    _cachedVersion = version;

                return version;
            }
        }

        private static string ResolveVersion()
        {
            var projectRoot = ApplicationPaths.ProjectRoot;
            var packageInfo = PackageInfo.FindForAssetPath(PackageInstallRoot);
            if (packageInfo != null &&
                string.Equals(packageInfo.name, PackageName, System.StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(packageInfo.resolvedPath))
            {
                var resolvedPackageJsonPath = Path.Combine(packageInfo.resolvedPath, "package.json");
                var resolvedVersion = TryReadVersionFromPackageJson(resolvedPackageJsonPath);
                if (!string.IsNullOrEmpty(resolvedVersion))
                    return resolvedVersion;
            }

            // Installed from the Asset Store the package sits under Assets/ at a path the
            // buyer can rename, so ask the asset database for our own package.json instead
            // of guessing install roots.
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("package"))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (!assetPath.EndsWith("/package.json", System.StringComparison.Ordinal))
                        continue;

                    if (!File.Exists(assetPath) || !File.ReadAllText(assetPath).Contains("\"" + PackageName + "\""))
                        continue;

                    var ownVersion = TryReadVersionFromPackageJson(assetPath);
                    if (!string.IsNullOrEmpty(ownVersion))
                        return ownVersion;
                }
            }
            catch
            {
            }

            var candidates = new[]
            {
                Path.Combine(projectRoot, AssetInstallRoot, "package.json"),
                Path.Combine(projectRoot, PackageInstallRoot, "package.json"),
                Path.Combine(projectRoot, "package.json")
            };

            for (int i = 0; i < candidates.Length; i++)
            {
                var version = TryReadVersionFromPackageJson(candidates[i]);
                if (!string.IsNullOrEmpty(version))
                    return version;
            }

            return FallbackVersion;
        }

        private static string TryReadVersionFromPackageJson(string path)
        {
            if (!File.Exists(path))
                return null;

            var json = File.ReadAllText(path);
            var match = Regex.Match(json, "\"version\"\\s*:\\s*\"(?<version>[^\"]+)\"");
            return match.Success ? match.Groups["version"].Value : null;
        }
    }
}
