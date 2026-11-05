// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Editor")]
    internal static class AuthoringRootFunctions
    {
        [Description("Report the authoring root: the folder under Assets/ that bare asset paths (no Assets/ prefix) resolve against and " +
                     "that the asset tools are confined to. 'Assets' (the default) means the whole project and leaves bare paths refused.")]
        [ReadOnlyTool]
        public static object GetAuthoringRoot()
        {
            var explicitRoot = AuthoringRoot.Explicit;
            return Response.Success(explicitRoot == null ? "The authoring root is Assets (the whole project)." : $"The authoring root is {explicitRoot}.",
                new { root = explicitRoot ?? AuthoringRoot.Default, confined = explicitRoot != null });
        }

        [Description("Set the authoring root: the folder under Assets/ (e.g. 'Assets/AgentWork') that bare asset paths resolve against, " +
                     "and that every asset tool taking a path is then confined to, including its reads: a path outside it is refused " +
                     "as INVALID_PATH. The folder must exist. 'Assets' lifts the confinement. The setting is kept per project and " +
                     "per user (Library/EditorUserSettings), not in version control.")]
        public static object SetAuthoringRoot(
            [ToolParam("Project-relative folder under Assets/, or 'Assets' for the whole project")] string root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return Response.Error("INVALID_PARAM", new { param = "root", provided = root });

            var segments = root.Replace('\\', '/').Split('/').Where(s => s.Length > 0).ToArray();
            if (segments.Length == 0 || !string.Equals(segments[0], AuthoringRoot.Default, StringComparison.OrdinalIgnoreCase) ||
                segments.Any(s => s == ".." || s == "."))
                return Response.Error("INVALID_ROOT", new { root }, "root must be 'Assets' or a folder under Assets/ with no '..' segments.");

            segments[0] = AuthoringRoot.Default;
            var normalized = string.Join("/", segments);

            if (segments.Length > 1 && !AssetDatabase.IsValidFolder(normalized))
                return Response.Error("ROOT_NOT_FOUND", new { root = normalized }, "The folder must exist. create_folder makes it.");

            AuthoringRoot.Explicit = segments.Length == 1 ? null : normalized;

            return Response.Success(segments.Length == 1 ? "The authoring root is Assets again; asset tools are not confined." : $"The authoring root is now {normalized}.",
                new { root = normalized, confined = segments.Length > 1 });
        }
    }

    /// <summary>
    /// Shared by every path helper so a tool does not reimplement it. Left unset it changes nothing: bare paths stay refused and
    /// nothing is confined.
    /// </summary>
    internal static class AuthoringRoot
    {
        internal const string Default = "Assets";
        private const string ConfigKey = "KitWright.AuthoringRoot";

        internal static string Explicit
        {
            get
            {
                var value = EditorUserSettings.GetConfigValue(ConfigKey);
                return string.IsNullOrEmpty(value) ? null : value;
            }
            set => EditorUserSettings.SetConfigValue(ConfigKey, value ?? string.Empty);
        }

        /// <summary>A bare path becomes a path under the explicit root; anything starting with Assets/ or Packages/ is left alone.</summary>
        internal static string Resolve(string path)
        {
            var root = Explicit;
            if (root == null || string.IsNullOrWhiteSpace(path))
                return path;

            var text = path.Replace('\\', '/').TrimStart('/');
            return text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
                ? path
                : root + "/" + text;
        }

        internal static bool Contains(string normalizedPath)
        {
            var root = Explicit;
            return root == null || normalizedPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
