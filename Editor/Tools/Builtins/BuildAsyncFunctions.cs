// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// A player build that does not hold the call: build queues it, build_status reports it. BuildPipeline.BuildPlayer pins the
    /// editor thread, so build_status reads plain in-memory state and runs off the editor thread to keep answering. The finished
    /// result is also kept in SessionState, so a domain reload during the build does not lose it.
    /// </summary>
    [ToolProvider("Build")]
    [InitializeOnLoad]
    internal static class BuildAsyncFunctions
    {
        private const string ResultKey = "KitWright.Build.LastResult";
        private const string SwitchKey = "KitWright.Build.SwitchRequest";
        private const double SwitchGiveUpSeconds = 180;
        private static readonly object Gate = new object();

        private static string status = "idle";
        private static string resultJson;

        static BuildAsyncFunctions()
        {
            var saved = SessionState.GetString(ResultKey, string.Empty);
            if (saved.Length > 0)
            {
                resultJson = saved;
                status = "completed";
            }
        }

        [Description("Start a player build and return at once; poll build_status until it is completed. The editor is busy while " +
                     "the build runs, so other tools answer only afterwards, but build_status always answers. Requires confirm=true " +
                     "(dry_run validates without building). By default builds the enabled scenes from Build Settings to the active " +
                     "build target. An output folder inside a system directory, or that is the project root, contains the project, or is " +
                     "Assets, ProjectSettings, Library, Packages, UserSettings, Temp or Logs, is refused (OUTPUT_PATH_REFUSED). " +
                     "options names BuildOptions flags (e.g. 'Development' for a development build, 'Development,AllowDebugging'); leaving it empty builds with a " +
                     "detailed report. profile_name builds with a Unity 6 Build Profile without activating it.")]
        public static object Build(
            [ToolParam("Target platform (windows64, android, ios, webgl, ...). Empty = active build target.", Required = false)] string target = null,
            [ToolParam("Output path (relative to project root or absolute). Empty = 'Builds/<target>/<product>.<ext>'.", Required = false)] string output_path = null,
            [ToolParam("Comma-separated scene paths. Empty = enabled scenes from Build Settings.", Required = false)] string scenes = null,
            [ToolParam("Comma-separated BuildOptions names. Empty = DetailedBuildReport.", Required = false)] string options = null,
            [ToolParam("Build Profile asset name (Unity 6 only); builds with it without activating it", Required = false)] string profile_name = null,
            [ToolParam("Must be true to run the build", Required = false)] bool confirm = false,
            [ToolParam("Validate the request and report the plan without building", Required = false)] bool dry_run = false)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Response.Error("BUILD_REQUIRES_EDIT_MODE", hint: "Call exit_play_mode first, then retry build.");

            lock (Gate)
            {
                if (status == "queued" || status == "building")
                    return Response.Error("BUILD_IN_PROGRESS", new { status });
            }

            if (!BuildFunctions.TryResolveTarget(target, out var buildTarget))
                return Response.Error("UNKNOWN_TARGET", new { target });

            if (!TryParseOptions(options, out var buildOptions, out var badOption))
                return Response.Error("UNKNOWN_BUILD_OPTION", new { option = badOption, valid = Enum.GetNames(typeof(BuildOptions)) });

            var profile = FindProfile(profile_name, out var profileError);
            if (profileError != null) return profileError;

            var sceneArray = string.IsNullOrWhiteSpace(scenes)
                ? EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray()
                : scenes.Split(',').Select(BuildFunctions.NormalizeScenePath).Where(s => s.Length > 0).ToArray();

            if (profile == null)
            {
                if (sceneArray.Length == 0)
                    return Response.Error("NO_SCENES", hint: "Enable scenes in Build Settings or pass scenes.");

                var missing = sceneArray.Where(p => !BuildFunctions.IsSceneAssetPath(p) || AssetDatabase.GetMainAssetTypeAtPath(p) != typeof(SceneAsset)).ToArray();
                if (missing.Length > 0)
                    return Response.Error("SCENE_NOT_FOUND", new { missing });
            }

            var outputPath = System.IO.Path.GetFullPath(string.IsNullOrWhiteSpace(output_path)
                ? BuildFunctions.DefaultOutputPath(buildTarget, PlayerSettings.productName)
                : output_path);

            if (BuildFunctions.IsSystemLocation(outputPath) || BuildFunctions.IsProtectedBuildOutput(outputPath, ApplicationPaths.ProjectRoot))
                return Response.Error("OUTPUT_PATH_REFUSED", new { outputPath },
                    "Pick a build folder that is not inside a system directory, the project root or one of the project's own folders.");

            var plan = new { target = buildTarget.ToString(), outputPath, sceneCount = profile == null ? sceneArray.Length : (int?)null, options = string.IsNullOrWhiteSpace(options) ? nameof(BuildOptions.DetailedBuildReport) : options, profile = profile_name };

            if (dry_run)
                return Response.Success("The build request is valid; nothing was built.", plan);

            if (!confirm)
                return Response.Error("CONFIRM_REQUIRED", plan, "A build blocks the editor for its whole duration. Pass confirm=true to run it, or dry_run=true to only validate.");

            lock (Gate)
            {
                status = "queued";
                resultJson = null;
            }

            SessionState.EraseString(ResultKey);

            // delayCall waits for a GUI tick that a background editor may not get; update is what the MCP pump itself runs on.
            EditorApplication.CallbackFunction start = null;
            start = () =>
            {
                EditorApplication.update -= start;
                Run(buildTarget, outputPath, sceneArray, buildOptions, profile);
            };
            EditorApplication.update += start;

            return Response.Success("Queued the build. Poll build_status.", plan);
        }

        [Description("Report the current or most recent build started with build: idle, queued, building or completed. A completed " +
                     "build carries its result (succeeded or not, size, errors, warnings, duration, output path).")]
        [ReadOnlyTool]
        [OffEditorThread]
        public static object BuildStatus()
        {
            lock (Gate)
            {
                return Response.Success($"The build is {status}.", new
                {
                    status,
                    result = resultJson != null ? JToken.Parse(resultJson) : null
                });
            }
        }

        [Description("Report the last switch_build_target: idle (none requested), switching (accepted, not made yet; Unity makes it once " +
                     "the editor is idle), completed, or failed (not made within 3 minutes), with the active build target.")]
        [ReadOnlyTool]
        public static object SwitchBuildTargetStatus()
        {
            var active = EditorUserBuildSettings.activeBuildTarget;
            var state = "idle";
            string requested = null;

            var raw = SessionState.GetString(SwitchKey, string.Empty);
            var parts = raw.Split('|');
            if (parts.Length == 2 && Enum.TryParse(parts[0], out BuildTarget want) && long.TryParse(parts[1], out var ticks))
            {
                requested = want.ToString();
                state = want == active ? "completed"
                    : (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc)).TotalSeconds > SwitchGiveUpSeconds ? "failed"
                    : "switching";
            }

            return Response.Success($"The build target switch is {state}.", new
            {
                state,
                requested,
                activeBuildTarget = active.ToString(),
                activeBuildTargetGroup = BuildPipeline.GetBuildTargetGroup(active).ToString()
            });
        }

        internal static void RecordSwitchRequest(BuildTarget target) =>
            SessionState.SetString(SwitchKey, target + "|" + DateTime.UtcNow.Ticks);

        private static void Run(BuildTarget target, string outputPath, string[] scenes, BuildOptions buildOptions, object profile)
        {
            lock (Gate) status = "building";

            string snapshot;
            try
            {
                var report = Execute(target, outputPath, scenes, buildOptions, profile);
                var summary = report.summary;
                snapshot = JsonConvert.SerializeObject(new
                {
                    result = summary.result.ToString(),
                    target = target.ToString(),
                    outputPath,
                    totalSizeMb = Math.Round(summary.totalSize / (1024.0 * 1024.0), 2),
                    totalErrors = summary.totalErrors,
                    totalWarnings = summary.totalWarnings,
                    durationSeconds = Math.Round(summary.totalTime.TotalSeconds, 1),
                    fileCount = report.GetFiles().Length,
                    errors = BuildFunctions.CollectBuildErrors(report)
                });
            }
            catch (Exception ex)
            {
                snapshot = JsonConvert.SerializeObject(new { result = "Exception", target = target.ToString(), outputPath, message = ex.Message });
            }

            SessionState.SetString(ResultKey, snapshot);
            lock (Gate)
            {
                resultJson = snapshot;
                status = "completed";
            }
        }

        private static BuildReport Execute(BuildTarget target, string outputPath, string[] scenes, BuildOptions buildOptions, object profile)
        {
#if UNITY_6000_0_OR_NEWER
            if (profile is UnityEditor.Build.Profile.BuildProfile buildProfile)
                return BuildPipeline.BuildPlayer(new BuildPlayerWithProfileOptions
                {
                    buildProfile = buildProfile,
                    locationPathName = outputPath,
                    options = buildOptions
                });
#endif
            return BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                target = target,
                targetGroup = BuildPipeline.GetBuildTargetGroup(target),
                locationPathName = outputPath,
                scenes = scenes,
                options = buildOptions
            });
        }

        private static bool TryParseOptions(string text, out BuildOptions options, out string badName)
        {
            options = BuildOptions.None;
            badName = null;

            if (string.IsNullOrWhiteSpace(text))
            {
                options = BuildOptions.DetailedBuildReport;
                return true;
            }

            foreach (var name in text.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                if (!Enum.TryParse(name, true, out BuildOptions flag) || !Enum.IsDefined(typeof(BuildOptions), flag))
                {
                    badName = name;
                    return false;
                }

                options |= flag;
            }

            return true;
        }

        private static object FindProfile(string name, out object error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(name))
                return null;

#if UNITY_6000_0_OR_NEWER
            var match = AssetDatabase.FindAssets("t:BuildProfile")
                .Select(AssetDatabase.GUIDToAssetPath)
                .FirstOrDefault(path => string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), name.Trim(), StringComparison.OrdinalIgnoreCase));

            var profile = match != null ? AssetDatabase.LoadAssetAtPath<UnityEditor.Build.Profile.BuildProfile>(match) : null;
            if (profile == null)
                error = Response.Error("BUILD_PROFILE_NOT_FOUND", new { profile_name = name }, "list_build_profiles shows the names.");
            return profile;
#else
            error = Response.Error("FEATURE_UNAVAILABLE", hint: "Build Profiles exist from Unity 6 on.");
            return null;
#endif
        }
    }
}
