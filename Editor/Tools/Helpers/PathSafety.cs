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
