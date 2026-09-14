// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.Services;

namespace KitWright.Editor.Tools.Helpers
{
    // Distinct type so the invoker can answer a policy refusal as an error instead of logging
    // it as an unexpected tool failure.
    internal sealed class PathOutsideProjectException : ArgumentException
    {
        public PathOutsideProjectException(string message, string paramName) : base(message, paramName) { }
    }

    internal static class PathSafety
    {
        public static bool IsInsideDirectory(string path, string directory)
        {
            var normalizedPath = Path.GetFullPath(path);
            var normalizedDirectory = EnsureTrailingSeparator(Path.GetFullPath(directory));
            return normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
        }

        public static string ResolveProjectPath(string path)
        {
            var root = Path.GetFullPath(ApplicationPaths.ProjectRoot);
            var resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));

            if (!string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase)
                && !IsInsideDirectory(resolved, root))
                throw new PathOutsideProjectException(
                    $"Path '{path}' resolves to '{resolved}', which escaped the project. " +
                    $"Only paths inside the project root '{root}' can be accessed.", nameof(path));

            return resolved;
        }

        /// Absolute path for an asset path written as "Assets/...". Checking the text alone is not
        /// enough: "Assets/../../x.png" starts with Assets/ and lands two directories outside the
        /// project, and a tool that writes before Unity imports writes it there.
        public static string ResolveAssetPath(string path)
        {
            var normalized = path?.Replace('\\', '/');
            if (string.IsNullOrEmpty(normalized)
                || !normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                throw new PathOutsideProjectException(
                    $"Path '{path}' must be under Assets/.", nameof(path));

            var resolved = ResolveProjectPath(normalized);
            if (!IsInsideDirectory(resolved, ApplicationPaths.AssetsPath))
                throw new PathOutsideProjectException(
                    $"Path '{path}' resolves to '{resolved}', which is outside the project's Assets folder.",
                    nameof(path));

            return resolved;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            var last = path[path.Length - 1];
            if (last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar)
                return path;

            return path + Path.DirectorySeparatorChar;
        }
    }
}
