// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
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
                     "By default builds the enabled scenes from Build Settings to the active build target. This can take a long time for large projects.")]
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
                : scenes.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();

            if (sceneArray.Length == 0)
                return Response.Error("NO_SCENES", new { message = "No scenes to build. Enable scenes in Build Settings or pass the scenes parameter." });

            var missing = sceneArray.Where(p => !System.IO.File.Exists(p)).ToArray();
            if (missing.Length > 0)
                return Response.Error("SCENE_NOT_FOUND", new { missing });

            var outputPath = string.IsNullOrWhiteSpace(output_path)
                ? DefaultOutputPath(buildTarget, PlayerSettings.productName)
                : output_path;

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
                    durationSeconds = Math.Round(summary.totalTime.TotalSeconds, 1)
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

        [Description("Add, remove, or set the enabled/disabled state of a scene in the Build Settings scene list.")]
        public static object ModifyBuildScenes(
            [ToolParam("Action: 'add', 'remove', 'enable', 'disable'")] string action,
            [ToolParam("Scene asset path, e.g. 'Assets/Scenes/Main.unity'")] string scene_path)
        {
            action = action?.ToLowerInvariant();
            var list = EditorBuildSettings.scenes.ToList();
            int idx = list.FindIndex(s => string.Equals(s.path, scene_path, StringComparison.OrdinalIgnoreCase));

            switch (action)
            {
                case "add":
                    if (!System.IO.File.Exists(scene_path))
                        return Response.Error("SCENE_NOT_FOUND", new { scene_path });
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
                enabledSceneCount = list.Count(s => s.enabled)
            });
        }

        [Description("Switch the active build target platform (triggers a platform switch + recompile). Use before building for a different platform.")]
        public static object SwitchBuildTarget(
            [ToolParam("Target platform: windows64, windows32, osx, linux64, android, ios, webgl, uwp, tvos")] string target)
        {
            if (!TryResolveTarget(target, out var buildTarget))
                return Response.Error("UNKNOWN_TARGET", new { target, valid = "windows64, windows32, osx, linux64, android, ios, webgl, uwp, tvos" });

            var group = BuildPipeline.GetBuildTargetGroup(buildTarget);
            if (EditorUserBuildSettings.activeBuildTarget == buildTarget)
                return Response.Success($"Active build target already {buildTarget}.");

            // Target switch reimports assets + recompiles; both stall while the editor is unfocused.
            NoThrottleLease.Acquire(TimeSpan.FromMinutes(30));
            bool ok = EditorUserBuildSettings.SwitchActiveBuildTarget(group, buildTarget);
            return ok
                ? Response.Success($"Switched active build target to {buildTarget}.")
                : Response.Error("SWITCH_FAILED", new { target = buildTarget.ToString(), hint = "the platform build support module may not be installed" });
        }

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
