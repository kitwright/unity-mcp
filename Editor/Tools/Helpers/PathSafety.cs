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

            RefuseLinkedSegment(resolved, root, path);
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

        /// <summary>
        /// Refuses a path that reaches its target through a junction or symlink. Path.GetFullPath
        /// resolves the text, not the filesystem: a link inside the project pointing anywhere on disk
        /// leaves the resolved path looking local, and the tool then reads or writes at the far end
        /// of it. Every existing directory between the root and the target is checked, since the link
        /// can be any segment of the path, and the check runs on paths that do not exist yet so a
        /// write is refused before it creates anything.
        ///
        /// Links that stay inside the project are refused too. Following one to decide would mean
        /// resolving the real target, which needs per-platform interop; over-refusing here costs a
        /// project that deliberately links a shared folder into Assets the use of these tools on it,
        /// and the message says which segment did it so that is diagnosable rather than mysterious.
        /// </summary>
        private static void RefuseLinkedSegment(string resolved, string root, string requested)
        {
            var rootLength = EnsureTrailingSeparator(root).Length;

            for (var current = resolved;
                 !string.IsNullOrEmpty(current) && current.Length >= rootLength;
                 current = Path.GetDirectoryName(current))
            {
                if (!IsLink(current))
                    continue;

                throw new PathOutsideProjectException(
                    $"Path '{requested}' reaches its target through '{current}', which is a junction or " +
                    "symlink. Where that link points is not checked, so the path is refused: pass a path " +
                    "that stays on the project's own filesystem.", nameof(requested));
            }
        }

        private static bool IsLink(string path)
        {
            try
            {
                // Attributes of the entry itself; a path that does not exist yet cannot be a link.
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
            }
            catch (Exception)
            {
                // Missing, or unreadable metadata: not something to refuse a path over.
                return false;
            }
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
