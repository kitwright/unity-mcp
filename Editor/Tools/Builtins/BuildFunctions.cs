// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Build")]
    internal static class BuildFunctions
    {
        [Description("Build a standalone/player build with Unity's BuildPipeline. Runs synchronously and blocks until the build finishes, then returns the result summary (size, errors, warnings, output path). " +
                     "By default builds the enabled scenes from Build Settings to the active build target. This can take a long time for large projects. " +
                     "An output folder that is the project root, contains the project, or is Assets, ProjectSettings, Library, Packages, UserSettings, Temp or Logs is refused (OUTPUT_PATH_REFUSED).")]
        [LongRunningTool(1800)]
        public static object BuildPlayer(
            [ToolParam("Target platform: windows64, windows32, osx, linux64, android, ios, webgl, uwp, tvos. Empty = active build target.", Required = false)] string target = null,
            [ToolParam("Output path (relative to project root or absolute). Empty = 'Builds/<target>/<product>.<ext>'.", Required = false)] string output_path = null,
            [ToolParam("Comma-separated scene paths to include. Empty = enabled scenes from Build Settings.", Required = false)] string scenes = null,
            [ToolParam("Development build (adds Development flag, debug symbols).", Required = false)] bool development = false)
        {
            if (!TryResolveTarget(target, out var buildTarget))
                return Response.Error("UNKNOWN_TARGET", new { target, valid = "windows64, windows32, osx, linux64, android, ios, webgl, uwp, tvos" });

            var sceneArray = string.IsNullOrWhiteSpace(scenes)
                ? EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray()
                : scenes.Split(',').Select(NormalizeScenePath).Where(s => s.Length > 0).ToArray();

            if (sceneArray.Length == 0)
                return Response.Error("NO_SCENES", new { message = "No scenes to build. Enable scenes in Build Settings or pass the scenes parameter." });

            var missing = sceneArray.Where(p => !SceneExists(p)).ToArray();
            if (missing.Length > 0)
                return Response.Error("SCENE_NOT_FOUND", new { missing }, SceneHint);

            var outputPath = string.IsNullOrWhiteSpace(output_path)
                ? DefaultOutputPath(buildTarget, PlayerSettings.productName)
                : output_path;

            // Deliberately NOT confined to the project: builds land in C:\Builds or a CI output
            // directory as a matter of course, and that is the tool's job. What it must never do is
            // write a player over the operating system or over the project's own folders.
            outputPath = System.IO.Path.GetFullPath(outputPath);
            if (IsSystemLocation(outputPath))
                return Response.Error("OUTPUT_PATH_REFUSED", new { outputPath },
                    "That path is inside a system directory. Pick a build folder.");

            if (IsProtectedBuildOutput(outputPath, ApplicationPaths.ProjectRoot))
                return Response.Error("OUTPUT_PATH_REFUSED", new { outputPath },
                    "A build fills its output folder, and this one is the project root, a folder that contains the " +
                    "project, or Assets, ProjectSettings, Library, Packages, UserSettings, Temp or Logs. Pick a build folder such as 'Builds'.");

            var options = new BuildPlayerOptions
            {
                target = buildTarget,
                targetGroup = BuildPipeline.GetBuildTargetGroup(buildTarget),
                locationPathName = outputPath,
                scenes = sceneArray,
                options = development ? BuildOptions.Development : BuildOptions.None
            };

            try
            {
                var report = BuildPipeline.BuildPlayer(options);
                var summary = report.summary;
                bool ok = summary.result == BuildResult.Succeeded;

                var data = new
                {
                    result = summary.result.ToString(),
                    target = buildTarget.ToString(),
                    outputPath,
                    sceneCount = sceneArray.Length,
                    totalSizeMb = Math.Round(summary.totalSize / (1024.0 * 1024.0), 2),
                    totalErrors = summary.totalErrors,
                    totalWarnings = summary.totalWarnings,
                    durationSeconds = Math.Round(summary.totalTime.TotalSeconds, 1),
                    // A failure count with no message sends the caller to Editor.log to find out
                    // what "1 error" was, which is the one thing this response should already know.
                    errors = CollectBuildErrors(report)
                };

                return ok
                    ? Response.Success($"Build succeeded: {buildTarget} → {outputPath}", data)
                    : Response.Error("BUILD_FAILED", data);
            }
            catch (Exception ex)
            {
                return Response.Error("BUILD_EXCEPTION", new { target = buildTarget.ToString(), message = ex.Message });
            }
        }

        private const int MaxReportedBuildErrors = 5;

        private static string[] CollectBuildErrors(BuildReport report)
        {
            if (report?.steps == null)
                return Array.Empty<string>();

            return report.steps
                .Where(step => step.messages != null)
                .SelectMany(step => step.messages)
                .Where(message => message.type == LogType.Error ||
                                  message.type == LogType.Exception ||
                                  message.type == LogType.Assert)
                .Select(message => message.content?.Trim())
                .Where(content => !string.IsNullOrEmpty(content))
                .Take(MaxReportedBuildErrors)
                .ToArray();
        }

        [Description("Add, remove, or set the enabled/disabled state of a scene in the Build Settings scene list. " +
                     "Build Settings has no Undo, so reverse a change by calling this tool again with the opposite action.")]
        public static object ModifyBuildScenes(
            [ToolParam("Action: 'add', 'remove', 'enable', 'disable'")] string action,
            [ToolParam("Scene asset path, e.g. 'Assets/Scenes/Main.unity'")] string scene_path)
        {
            action = action?.ToLowerInvariant();
            scene_path = NormalizeScenePath(scene_path);
            var list = EditorBuildSettings.scenes.ToList();
            int idx = list.FindIndex(s => string.Equals(s.path, scene_path, StringComparison.OrdinalIgnoreCase));

            switch (action)
            {
                case "add":
                    if (!SceneExists(scene_path))
                        return Response.Error("SCENE_NOT_FOUND", new { scene_path }, SceneHint);
                    if (idx >= 0)
                        return Response.Error("SCENE_ALREADY_LISTED", new { scene_path });
                    list.Add(new EditorBuildSettingsScene(scene_path, true));
                    break;
                case "remove":
                    if (idx < 0) return Response.Error("SCENE_NOT_LISTED", new { scene_path });
                    list.RemoveAt(idx);
                    break;
                case "enable":
                case "disable":
                    if (idx < 0) return Response.Error("SCENE_NOT_LISTED", new { scene_path });
                    list[idx] = new EditorBuildSettingsScene(list[idx].path, action == "enable");
                    break;
                default:
                    return Response.Error("INVALID_ACTION", new { action, valid = new[] { "add", "remove", "enable", "disable" } });
            }

            EditorBuildSettings.scenes = list.ToArray();
            return Response.Success($"Build scene list updated ({action}: {scene_path}).", new
            {
                sceneCount = list.Count,
                enabledSceneCount = list.Count(s => s.enabled),
                undoable = false
            });
        }

        [Description("Switch the active build target platform (triggers a platform switch + recompile). Use before building for a different platform.")]
        [LongRunningTool(1800)]
        public static object SwitchBuildTarget(
            [ToolParam("Target platform: windows64, windows32, osx, linux64, android, ios, webgl, uwp, tvos")] string target)
        {
            if (!TryResolveTarget(target, out var buildTarget))
                return Response.Error("UNKNOWN_TARGET", new { target, valid = "windows64, windows32, osx, linux64, android, ios, webgl, uwp, tvos" });

            var group = BuildPipeline.GetBuildTargetGroup(buildTarget);
            if (EditorUserBuildSettings.activeBuildTarget == buildTarget)
                return Response.Success($"Active build target already {buildTarget}.");

            bool accepted = EditorUserBuildSettings.SwitchActiveBuildTarget(group, buildTarget);
            var active = EditorUserBuildSettings.activeBuildTarget;
            switch (ClassifySwitch(accepted, buildTarget, active))
            {
                case SwitchOutcome.Switched:
                    return Response.Success($"Switched active build target to {buildTarget}.");
                case SwitchOutcome.Queued:
                    return Response.Success(
                        $"Unity accepted the switch to {buildTarget} but has not made it: the active build target is still {active}.",
                        new { queued = true, requested = buildTarget.ToString(), activeBuildTarget = active.ToString() },
                        "Unity runs a queued switch once the editor is idle (after a compile or import). Read the active target again" +
                        (MCPToolExportPolicy.IsToolExposed("get_build_settings") ? " with get_build_settings" : string.Empty) +
                        " before building for it.");
                default:
                    return Response.Error("SWITCH_FAILED", new { target = buildTarget.ToString(), hint = "the platform build support module may not be installed" });
            }
        }

        internal enum SwitchOutcome { Switched, Queued, Failed }

        // The call answers true for a switch it only queued, so the active target is what says it happened.
        internal static SwitchOutcome ClassifySwitch(bool accepted, BuildTarget requested, BuildTarget active) =>
            !accepted ? SwitchOutcome.Failed : active == requested ? SwitchOutcome.Switched : SwitchOutcome.Queued;

        private const string SceneHint =
            "A scene is an existing .unity asset under Assets/ or Packages/, written like 'Assets/Scenes/Main.unity'.";

        internal static string NormalizeScenePath(string path) => (path ?? string.Empty).Trim().Replace('\\', '/');

        // Shape first, so a path outside the project or to some other kind of file is refused the same way as a
        // missing scene instead of being answered by whether a file of that name exists on disk.
        internal static bool IsSceneAssetPath(string path)
        {
            var p = NormalizeScenePath(path);
            return (p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)) &&
                   p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) &&
                   !p.Split('/').Contains("..");
        }

        private static bool SceneExists(string path) =>
            IsSceneAssetPath(path) && AssetDatabase.GetMainAssetTypeAtPath(path) == typeof(SceneAsset);

        internal static bool TryResolveTarget(string name, out BuildTarget target)
        {
            if (string.IsNullOrEmpty(name))
            {
                target = EditorUserBuildSettings.activeBuildTarget;
                return true;
            }
            switch (name.ToLowerInvariant())
            {
                case "windows64": target = BuildTarget.StandaloneWindows64; return true;
                case "windows": case "windows32": target = BuildTarget.StandaloneWindows; return true;
                case "osx": case "macos": target = BuildTarget.StandaloneOSX; return true;
                case "linux64": case "linux": target = BuildTarget.StandaloneLinux64; return true;
                case "android": target = BuildTarget.Android; return true;
                case "ios": target = BuildTarget.iOS; return true;
                case "webgl": target = BuildTarget.WebGL; return true;
                case "uwp": target = BuildTarget.WSAPlayer; return true;
                case "tvos": target = BuildTarget.tvOS; return true;
                default:
                    return Enum.TryParse(name, true, out target) && Enum.IsDefined(typeof(BuildTarget), target);
            }
        }

        /// <summary>
        /// Whether a build would be written into the operating system's own directories. This is the
        /// only containment the tool applies: a build output lives wherever the team keeps builds,
        /// which is routinely outside the project, so confining it to the project root would break
        /// the ordinary case to guard against a caller that can already run arbitrary code.
        /// </summary>
        internal static bool IsSystemLocation(string fullPath)
        {
            var path = (fullPath ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (path.Length == 0)
                return false;

            foreach (var folder in new[]
                     {
                         Environment.SpecialFolder.Windows,
                         Environment.SpecialFolder.System,
                         Environment.SpecialFolder.SystemX86,
                         Environment.SpecialFolder.ProgramFiles,
                         Environment.SpecialFolder.ProgramFilesX86
                     })
            {
                var root = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(root) && IsInside(path, root))
                    return true;
            }

            // macOS and Linux have no SpecialFolder for these.
            foreach (var root in new[] { "/System", "/usr", "/bin", "/sbin", "/etc", "/Library" })
                if (IsInside(path, root))
                    return true;

            return false;
        }

        private static bool IsInside(string path, string root)
        {
            var normalized = root.Replace('\\', '/').TrimEnd('/');
            return normalized.Length > 0 &&
                   (path.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static readonly string[] ProtectedProjectFolders =
            { "Assets", "ProjectSettings", "Library", "Packages", "UserSettings", "Temp", "Logs" };

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint GetLongPathNameW(string shortPath, System.Text.StringBuilder longPath, uint length);

        // Windows resolves these spellings to the folder they name: a \\?\ or \\.\ prefix, trailing dots
        // and spaces on a segment, an administrative share of this machine, and 8.3 short names.
        internal static string CanonicalBuildPath(string fullPath)
        {
            var path = fullPath;
            if (System.IO.Path.DirectorySeparatorChar == '\\')
            {
                if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    path = @"\\" + path.Substring(8);
                else if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
                    path = path.Substring(4);

                var share = System.Text.RegularExpressions.Regex.Match(path,
                    @"^\\\\(localhost|127\.0\.0\.1|" + System.Text.RegularExpressions.Regex.Escape(Environment.MachineName) + @")\\([A-Za-z])\$(\\.*)?$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (share.Success)
                    path = share.Groups[2].Value + ":" + (share.Groups[3].Success ? share.Groups[3].Value : "\\");

                path = string.Join("\\", path.Split('\\', '/').Select(s => s == "." || s == ".." ? s : s.TrimEnd('.', ' ')));
            }

            path = System.IO.Path.GetFullPath(path);
            return System.IO.Path.DirectorySeparatorChar == '\\' ? ExpandShortNames(path) : path;
        }

        private static string ExpandShortNames(string path)
        {
            try
            {
                var tail = new System.Collections.Generic.Stack<string>();
                var existing = path;
                while (!string.IsNullOrEmpty(existing) && !System.IO.Directory.Exists(existing) && !System.IO.File.Exists(existing))
                {
                    tail.Push(System.IO.Path.GetFileName(existing));
                    existing = System.IO.Path.GetDirectoryName(existing);
                }

                if (string.IsNullOrEmpty(existing))
                    return path;

                var buffer = new System.Text.StringBuilder(1024);
                var length = GetLongPathNameW(existing, buffer, (uint)buffer.Capacity);
                var expanded = length > 0 && length < buffer.Capacity ? buffer.ToString() : existing;
                foreach (var part in tail)
                    expanded = System.IO.Path.Combine(expanded, part);
                return expanded;
            }
            catch (Exception)
            {
                return path;
            }
        }

        /// <summary>
        /// Whether a build written here would land in the project root, a folder that contains the
        /// project (a drive root included), or one of the project folders Unity owns.
        /// </summary>
        internal static bool IsProtectedBuildOutput(string fullPath, string projectRoot)
        {
            var root = CanonicalBuildPath(projectRoot);
            var output = CanonicalBuildPath(fullPath);
            // With an extension the path is the player file and the build fills the folder around it.
            var folder = System.IO.Path.HasExtension(output) ? System.IO.Path.GetDirectoryName(output) ?? output : output;

            foreach (var candidate in new[] { output, folder })
            {
                if (SameOrInside(root, candidate))
                    return true;

                foreach (var name in ProtectedProjectFolders)
                    if (SameOrInside(candidate, System.IO.Path.Combine(root, name)))
                        return true;
            }

            return false;
        }

        private static bool SameOrInside(string path, string directory) =>
            PathSafety.IsInsideDirectory(path, directory) ||
            string.Equals(
                path.TrimEnd('\\', '/'), directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        internal static string DefaultOutputPath(BuildTarget target, string productName)
        {
            string basePath = $"Builds/{target}";
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return $"{basePath}/{productName}.exe";
                case BuildTarget.StandaloneOSX:
                    return $"{basePath}/{productName}.app";
                case BuildTarget.StandaloneLinux64:
                    return $"{basePath}/{productName}.x86_64";
                case BuildTarget.Android:
                    return EditorUserBuildSettings.buildAppBundle
                        ? $"{basePath}/{productName}.aab"
                        : $"{basePath}/{productName}.apk";
                default:
                    return $"{basePath}/{productName}";
            }
        }
    }
}
