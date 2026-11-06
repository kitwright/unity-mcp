// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// The Unity CLI's typed settings tools that do more than a property write (set_ time, quality, player, graphics, input,
    /// tags_layers, build, runtime_pipeline), under its names and parameter shapes. Reading a page, and writing physics or audio,
    /// is get_project_settings / set_project_settings by property path. Every set_ tool here needs confirm=true and offers
    /// dry_run, and none of them is undoable.
    /// </summary>
    [ToolProvider("ProjectSettings")]
    internal static class UnityCliSettingsFunctions
    {
        // ---- Time ----

        [Description("Change the project's Time settings (TimeManager): {fixedDeltaTime, maximumDeltaTime, timeScale} as a JSON object. " +
                     "Edit Mode only; in Play Mode use set_time_scale for the running game. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetTimeSettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            // In Play Mode Time.* is the running game's copy, reset when Play Mode ends, so the project setting would not change.
            if (EditorApplication.isPlaying)
                return Response.Error("EDIT_MODE_REQUIRED", new { settings },
                    "Call exit_play_mode first to change the project's Time settings, or set_time_scale to change the running game only.");

            // Fixed Timestep is a compound property in Unity 6, so the Time API is the one writing that works on every version;
            // in Edit Mode it writes the TimeManager page.
            if (!TryParse(settings, out var json, out var error)) return error;

            var unknown = json.Properties().Select(p => p.Name).Where(n => n != "fixedDeltaTime" && n != "maximumDeltaTime" && n != "timeScale").ToArray();
            if (unknown.Length > 0)
                return Response.Error("UNKNOWN_SETTING", new { unknown, valid = new[] { "fixedDeltaTime", "maximumDeltaTime", "timeScale" } });

            var fixedStep = Float(json, "fixedDeltaTime");
            var maxStep = Float(json, "maximumDeltaTime");
            var scale = Float(json, "timeScale");
            if (fixedStep != null && !(fixedStep > 0f) || maxStep != null && !(maxStep > 0f) || scale != null && !(scale >= 0f))
                return Response.Error("INVALID_PARAM", new { fixedDeltaTime = fixedStep, maximumDeltaTime = maxStep, timeScale = scale },
                    "The two timesteps must be above 0 and timeScale 0 or more.");

            var changes = new List<string>();
            if (fixedStep != null) changes.Add($"fixedDeltaTime {Time.fixedDeltaTime} -> {fixedStep}");
            if (maxStep != null) changes.Add($"maximumDeltaTime {Time.maximumDeltaTime} -> {maxStep}");
            if (scale != null) changes.Add($"timeScale {Time.timeScale} -> {scale}");

            var gate = Gate(confirm, dry_run, changes);
            if (gate != null) return gate;

            if (fixedStep != null) Time.fixedDeltaTime = fixedStep.Value;
            if (maxStep != null) Time.maximumDeltaTime = maxStep.Value;
            if (scale != null) Time.timeScale = scale.Value;
            AssetDatabase.SaveAssets();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes });
        }

        // ---- Quality ----

        [Description("Change QualitySettings: {level, vSyncCount, antiAliasing} as a JSON object. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetQualitySettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            var changes = new List<string>();
            var level = Int(json, "level");
            var vSync = Int(json, "vSyncCount");
            var aa = Int(json, "antiAliasing");

            if (level != null && (level < 0 || level >= QualitySettings.names.Length))
                return Response.Error("INVALID_PARAM", new { param = "level", provided = level, count = QualitySettings.names.Length });
            if (vSync != null && (vSync < 0 || vSync > 4)) return Response.Error("INVALID_PARAM", new { param = "vSyncCount", provided = vSync });
            if (aa != null && Array.IndexOf(new[] { 0, 2, 4, 8 }, aa.Value) < 0) return Response.Error("INVALID_PARAM", new { param = "antiAliasing", provided = aa }, "Use 0, 2, 4 or 8.");

            if (level != null) changes.Add($"level {QualitySettings.GetQualityLevel()} -> {level}");
            if (vSync != null) changes.Add($"vSyncCount {QualitySettings.vSyncCount} -> {vSync}");
            if (aa != null) changes.Add($"antiAliasing {QualitySettings.antiAliasing} -> {aa}");

            var gate = Gate(confirm, dry_run, changes);
            if (gate != null) return gate;

            if (level != null) QualitySettings.SetQualityLevel(level.Value, true);
            if (vSync != null) QualitySettings.vSyncCount = vSync.Value;
            if (aa != null) QualitySettings.antiAliasing = aa.Value;
            AssetDatabase.SaveAssets();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes });
        }

        // ---- Player ----

        [Description("Read PlayerSettings: company, product, bundle version, scripting backend and API compatibility level of the selected platform.")]
        [ReadOnlyTool]
        public static object GetPlayerSettings()
        {
            var named = SelectedTarget();
            return Response.Success("Player settings read.", new
            {
                companyName = PlayerSettings.companyName,
                productName = PlayerSettings.productName,
                bundleVersion = PlayerSettings.bundleVersion,
                scriptingBackend = PlayerSettings.GetScriptingBackend(named).ToString(),
                apiCompatibilityLevel = PlayerSettings.GetApiCompatibilityLevel(named).ToString(),
                buildTargetGroup = EditorUserBuildSettings.selectedBuildTargetGroup.ToString()
            });
        }

        [Description("Change PlayerSettings: {companyName, productName, bundleVersion, scriptingBackend, apiCompatibilityLevel} as a JSON object. " +
                     "A scripting backend or API level change reloads the domain. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetPlayerSettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            var named = SelectedTarget();
            var changes = new List<string>();
            ScriptingImplementation backend = default;
            ApiCompatibilityLevel api = default;

            var backendText = Text(json, "scriptingBackend");
            if (backendText != null && !Enum.TryParse(backendText, true, out backend))
                return Response.Error("INVALID_PARAM", new { param = "scriptingBackend", provided = backendText, valid = Enum.GetNames(typeof(ScriptingImplementation)) });

            var apiText = Text(json, "apiCompatibilityLevel");
            if (apiText != null && !Enum.TryParse(apiText, true, out api))
                return Response.Error("INVALID_PARAM", new { param = "apiCompatibilityLevel", provided = apiText, valid = Enum.GetNames(typeof(ApiCompatibilityLevel)) });

            if (Text(json, "companyName") != null) changes.Add($"companyName {PlayerSettings.companyName} -> {Text(json, "companyName")}");
            if (Text(json, "productName") != null) changes.Add($"productName {PlayerSettings.productName} -> {Text(json, "productName")}");
            if (Text(json, "bundleVersion") != null) changes.Add($"bundleVersion {PlayerSettings.bundleVersion} -> {Text(json, "bundleVersion")}");
            if (backendText != null) changes.Add($"scriptingBackend {PlayerSettings.GetScriptingBackend(named)} -> {backend}");
            if (apiText != null) changes.Add($"apiCompatibilityLevel {PlayerSettings.GetApiCompatibilityLevel(named)} -> {api}");

            var gate = Gate(confirm, dry_run, changes);
            if (gate != null) return gate;

            if (Text(json, "companyName") != null) PlayerSettings.companyName = Text(json, "companyName");
            if (Text(json, "productName") != null) PlayerSettings.productName = Text(json, "productName");
            if (Text(json, "bundleVersion") != null) PlayerSettings.bundleVersion = Text(json, "bundleVersion");
            if (backendText != null) PlayerSettings.SetScriptingBackend(named, backend);
            if (apiText != null) PlayerSettings.SetApiCompatibilityLevel(named, api);
            AssetDatabase.SaveAssets();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes });
        }

        // ---- Graphics ----

        [Description("Set the default render pipeline asset: {renderPipelineAsset: '<path or guid>'}, or an empty value / '{}' for the " +
                     "built-in pipeline. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetGraphicsSettings(
            [ToolParam("JSON object with renderPipelineAsset")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;
            if (!json.ContainsKey("renderPipelineAsset"))
                return Response.Success("Nothing to change.", new { applied = new string[0] });

            RenderPipelineAsset target = null;
            var token = json["renderPipelineAsset"];
            var reference = token == null || token.Type == JTokenType.Null ? null
                : token.Type == JTokenType.Object ? (token["path"] ?? token["guid"])?.ToString()
                : token.ToString();

            if (!string.IsNullOrWhiteSpace(reference))
            {
                var path = reference.Length == 32 && !reference.Contains("/") ? AssetDatabase.GUIDToAssetPath(reference) : reference;
                target = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(path);
                if (target == null)
                    return Response.Error("ASSET_NOT_FOUND", new { reference }, "renderPipelineAsset must be a RenderPipelineAsset, by path or guid.");
            }

            var change = $"renderPipelineAsset {GraphicsSettings.defaultRenderPipeline?.name ?? "built-in"} -> {target?.name ?? "built-in"}";
            var gate = Gate(confirm, dry_run, new List<string> { change });
            if (gate != null) return gate;

            GraphicsSettings.defaultRenderPipeline = target;
            AssetDatabase.SaveAssets();
            return Response.Success("Applied 1 change.", new { applied = new[] { change } });
        }

        // ---- Input (legacy Input Manager) ----

        [Description("Tune a legacy Input Manager axis by name: {axis, sensitivity, gravity, dead} as a JSON object; omitted numbers stay. " +
                     "Every axis of that name changes. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetInputSettings(
            [ToolParam("JSON object: axis plus the numbers to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            var axisName = Text(json, "axis");
            if (string.IsNullOrWhiteSpace(axisName))
                return Response.Error("INVALID_PARAM", new { param = "axis" }, "axis names the axis to change, e.g. 'Horizontal'.");

            var so = Singleton("InputManager", out var singletonError);
            if (so == null) return singletonError;

            var axes = so.FindProperty("m_Axes");
            var matches = Enumerable.Range(0, axes.arraySize)
                .Select(i => axes.GetArrayElementAtIndex(i))
                .Where(a => a.FindPropertyRelative("m_Name").stringValue == axisName).ToArray();
            if (matches.Length == 0)
                return Response.Error("AXIS_NOT_FOUND", new { axis = axisName });

            var changes = new List<string>();
            foreach (var name in new[] { "sensitivity", "gravity", "dead" })
            {
                var value = Float(json, name);
                if (value != null)
                    changes.Add($"{axisName}.{name} {matches[0].FindPropertyRelative(name).floatValue} -> {value}");
            }

            var gate = Gate(confirm, dry_run, changes);
            if (gate != null) return gate;

            foreach (var axis in matches)
                foreach (var name in new[] { "sensitivity", "gravity", "dead" })
                {
                    var value = Float(json, name);
                    if (value != null) axis.FindPropertyRelative(name).floatValue = value.Value;
                }

            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes });
        }

        // ---- Tags and layers ----

        [Description("Read the project's tags and its named layers. include_empty_layers lists all 32 layer slots, an unused one with name null.")]
        [ReadOnlyTool]
        public static object GetTagsLayers(
            [ToolParam("List all 32 layer slots, the unnamed ones too", Required = false)] bool include_empty_layers = false) =>
            Response.Success("Tags and layers read.", new
            {
                tags = InternalEditorUtility.tags,
                layers = Enumerable.Range(0, 32).Where(i => include_empty_layers || LayerMask.LayerToName(i).Length > 0)
                    .Select(i => new { index = i, name = LayerMask.LayerToName(i).Length > 0 ? LayerMask.LayerToName(i) : null }).ToArray()
            });

        [Description("Add or remove tags and name user layers (index 8-31): {addTags: [..], removeTags: [..], setLayers: [{index, name}]} " +
                     "as a JSON object. Adding an existing tag does nothing; a built-in tag cannot be removed. A layer given without an " +
                     "index takes the first free user slot (none if the name already is a layer); an index with an empty name clears that " +
                     "slot. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetTagsLayers(
            [ToolParam("JSON object with addTags, removeTags and setLayers")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            var add = Strings(json["addTags"]);
            var remove = Strings(json["removeTags"]);
            if (add.Concat(remove).Any(t => t.Length == 0))
                return Response.Error("INVALID_TAG", new { addTags = add, removeTags = remove }, "Tag names cannot be empty.");

            var tagManager = Singleton("TagManager", out var singletonError);
            if (tagManager == null) return singletonError;

            // TagManager's tags array holds only the custom tags; InternalEditorUtility.tags adds the built-in ones.
            var allTags = InternalEditorUtility.tags;
            var tagsProperty = tagManager.FindProperty("tags");
            var customTags = Enumerable.Range(0, tagsProperty.arraySize).Select(i => tagsProperty.GetArrayElementAtIndex(i).stringValue).ToList();
            var unremovable = remove.FirstOrDefault(t => !customTags.Contains(t));
            if (unremovable != null)
                return allTags.Contains(unremovable)
                    ? Response.Error("CANNOT_REMOVE_BUILTIN_TAG", new { tag = unremovable }, "Built-in tags cannot be removed.")
                    : Response.Error("TAG_NOT_FOUND", new { tag = unremovable });
            add = add.Where(t => !allTags.Contains(t)).Distinct().ToArray();

            var slots = tagManager.FindProperty("layers");
            var original = Enumerable.Range(0, 32).Select(i => slots.GetArrayElementAtIndex(i).stringValue).ToArray();
            var names = (string[])original.Clone();
            var layers = new List<(int index, string name)>();
            foreach (var entry in (json["setLayers"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var name = (entry.Value<string>("name") ?? string.Empty).Trim();
                var index = entry.Value<int?>("index");
                if (index == null)
                {
                    if (name.Length == 0)
                        return Response.Error("INVALID_LAYER", new { param = "setLayers" },
                            "A layer without an index needs a name. To clear a slot, give its index and an empty name.");
                    if (Array.IndexOf(names, name) >= 0) continue;

                    index = Array.FindIndex(names, 8, n => n.Length == 0);
                    if (index < 0)
                        return Response.Error("NO_FREE_LAYER", new { layer = name }, "All user layer slots (8-31) are taken.");
                }
                else if (index < 8 || index > 31)
                    return Response.Error("INVALID_PARAM", new { param = "setLayers", index }, "User layers are 8 to 31.");

                layers.Add((index.Value, name));
                names[index.Value] = name;
            }

            var changes = add.Select(t => "add tag " + t).Concat(remove.Select(t => "remove tag " + t))
                .Concat(layers.Select(l => $"layer {l.index} '{original[l.index]}' -> '{l.name}'")).ToList();

            var gate = Gate(confirm, dry_run, changes);
            if (gate != null) return gate;

            foreach (var tag in add) InternalEditorUtility.AddTag(tag);
            foreach (var tag in remove) InternalEditorUtility.RemoveTag(tag);

            if (layers.Count > 0)
            {
                // The tag calls above wrote TagManager behind this SerializedObject's back.
                tagManager.Update();
                slots = tagManager.FindProperty("layers");
                foreach (var layer in layers)
                    slots.GetArrayElementAtIndex(layer.index).stringValue = layer.name;
                tagManager.ApplyModifiedProperties();
            }

            AssetDatabase.SaveAssets();
            var renamed = layers.Where(l => original[l.index].Length > 0 && original[l.index] != l.name).Select(l => l.index).Distinct().ToArray();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes },
                renamed.Length == 0 ? null
                    : $"GameObjects, camera culling masks and the collision matrix refer to layer slots ({string.Join(", ", renamed)}) by " +
                      "number, so they keep pointing at the renamed or cleared slot; they are not cleaned up.");
        }

        // ---- Build settings ----

        [Description("Set EditorUserBuildSettings fields: {developmentBuild, allowDebugging, connectWithProfiler, buildScriptsOnly, " +
                     "symlinkSources, il2CppCodeGeneration: OptimizeSpeed|OptimizeSize} as a JSON object. Scenes are modify_build_scenes, " +
                     "the target is switch_build_target. Needs confirm=true; dry_run previews.")]
        [ToolProvider("Build")]
        public static object SetBuildSettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            Il2CppCodeGeneration generation = default;
            var generationText = Text(json, "il2CppCodeGeneration");
            if (generationText != null && !Enum.TryParse(generationText, true, out generation))
                return Response.Error("INVALID_PARAM", new { param = "il2CppCodeGeneration", provided = generationText, valid = Enum.GetNames(typeof(Il2CppCodeGeneration)) });

            var flags = new (string name, Func<bool> read, Action<bool> write)[]
            {
                ("developmentBuild", () => EditorUserBuildSettings.development, v => EditorUserBuildSettings.development = v),
                ("allowDebugging", () => EditorUserBuildSettings.allowDebugging, v => EditorUserBuildSettings.allowDebugging = v),
                ("connectWithProfiler", () => EditorUserBuildSettings.connectProfiler, v => EditorUserBuildSettings.connectProfiler = v),
                ("buildScriptsOnly", () => EditorUserBuildSettings.buildScriptsOnly, v => EditorUserBuildSettings.buildScriptsOnly = v)
            };

            var changes = new List<string>();
            foreach (var flag in flags)
                if (Bool(json, flag.name) is bool wanted)
                    changes.Add($"{flag.name} {flag.read()} -> {wanted}");
            if (Bool(json, "symlinkSources") is bool symlink)
                changes.Add($"symlinkSources -> {symlink}");
            if (generationText != null)
                changes.Add($"il2CppCodeGeneration -> {generation}");

            var gate = Gate(confirm, dry_run, changes);
            if (gate != null) return gate;

            foreach (var flag in flags)
                if (Bool(json, flag.name) is bool wanted)
                    flag.write(wanted);
#pragma warning disable CS0618 // symlinkSources was renamed in Unity 6; the old name still works on every version
            if (Bool(json, "symlinkSources") is bool link) EditorUserBuildSettings.symlinkSources = link;
#pragma warning restore CS0618
            if (generationText != null) PlayerSettings.SetIl2CppCodeGeneration(SelectedTarget(), generation);

            AssetDatabase.SaveAssets();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes });
        }

        // ---- Pipeline runtime settings (com.unity.pipeline) ----

        [Description("Read the Unity Pipeline package's runtime settings (enableInBuilds, port, requestTimeoutMs, enableAuditLogging, " +
                     "autoStart, maxWorkItemsPerFrame). Needs com.unity.pipeline.")]
        [ReadOnlyTool]
        [RequiresPackage("com.unity.pipeline")]
        public static object GetRuntimePipelineSettings() => InvokePipeline("Get", new object[0]);

        [Description("Change the Unity Pipeline package's runtime settings: {enableInBuilds, port, requestTimeoutMs, enableAuditLogging, " +
                     "autoStart, maxWorkItemsPerFrame} as a JSON object. Refused in Play Mode. Needs confirm=true; dry_run previews. " +
                     "Needs com.unity.pipeline.")]
        [RequiresPackage("com.unity.pipeline")]
        public static object SetRuntimePipelineSettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            var inputType = PipelineType("Unity.Pipeline.Editor.Commands.ProjectSettings.RuntimePipelineSettingsInput");
            return InvokePipeline("Set", new[] { inputType != null ? json.ToObject(inputType) : null, confirm, dry_run });
        }

        // ---- shared ----

        private static SerializedObject Singleton(string name, out object error)
        {
            error = null;
            var target = Unsupported.GetSerializedAssetInterfaceSingleton(name);
            if (target == null)
            {
                error = Response.Error("SETTINGS_SINGLETON_NOT_FOUND", new { singleton = name });
                return null;
            }

            return new SerializedObject(target);
        }

        internal static object Gate(bool confirm, bool dryRun, List<string> changes)
        {
            if (changes.Count == 0)
                return Response.Success("Nothing to change.", new { applied = new string[0] });

            if (dryRun)
                return Response.Success($"Would apply {changes.Count} change(s); nothing was written.", new { wouldApply = changes, dry_run = true });

            return confirm ? null : Response.Error("CONFIRM_REQUIRED", new { wouldApply = changes },
                "This writes a project setting and cannot be undone. Pass confirm=true to apply it, or dry_run=true to preview.");
        }

        internal static bool TryParse(string text, out JObject json, out object error)
        {
            json = null;
            error = null;
            try
            {
                json = JsonParse.Object(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                return true;
            }
            catch (Exception ex)
            {
                error = Response.Error("INVALID_SETTINGS_JSON", new { message = ex.Message });
                return false;
            }
        }

        private static float? Float(JObject json, string name)
        {
            var token = json[name];
            if (token == null || token.Type == JTokenType.Null) return null;
            return token.Type == JTokenType.Integer || token.Type == JTokenType.Float
                ? token.Value<float>()
                : ValueConverter.TryFiniteFloat(token.ToString(), out var parsed) ? parsed : (float?)null;
        }

        private static int? Int(JObject json, string name) => Float(json, name) is float f ? (int)f : (int?)null;

        private static bool? Bool(JObject json, string name)
        {
            var token = json[name];
            return token != null && token.Type == JTokenType.Boolean ? token.Value<bool>() : (bool?)null;
        }

        private static string Text(JObject json, string name)
        {
            var token = json[name];
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static string[] Strings(JToken token) =>
            token is JArray array ? array.Select(t => t.ToString().Trim()).ToArray() : new string[0];

        private static NamedBuildTarget SelectedTarget() =>
            NamedBuildTarget.FromBuildTargetGroup(EditorUserBuildSettings.selectedBuildTargetGroup);

        private static Type PipelineType(string fullName) =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Unity.Pipeline.Editor")?.GetType(fullName, false);

        private static object InvokePipeline(string method, object[] args)
        {
            var type = PipelineType("Unity.Pipeline.Editor.Commands.ProjectSettings.RuntimePipelineSettingsCommands");
            var target = type?.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null)
                return Response.Error("PACKAGE_NOT_INSTALLED", new { package = "com.unity.pipeline" }, "Install com.unity.pipeline to use the Pipeline runtime settings tools.");

            try
            {
                var result = JObject.FromObject(target.Invoke(null, args));
                var success = (result["Success"] ?? result["success"])?.Value<bool>() ?? true;
                return success ? Response.Success("Pipeline runtime settings.", result) : Response.Error("PIPELINE_SETTINGS_FAILED", result);
            }
            catch (TargetInvocationException ex)
            {
                return Response.Error("PIPELINE_SETTINGS_FAILED", new { message = (ex.InnerException ?? ex).Message });
            }
        }
    }
}
