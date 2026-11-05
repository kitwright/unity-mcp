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
    /// The Unity CLI's typed settings tools (get_/set_ physics, audio, time, quality, player, graphics, input, tags_layers,
    /// build, runtime_pipeline), under its names and parameter shapes. get_project_settings / set_project_settings reach the same
    /// pages by property path; these take the handful of fields the CLI names. Every set_ tool needs confirm=true and offers
    /// dry_run, and none of them is undoable.
    /// </summary>
    [ToolProvider("UnitySettings")]
    internal static class UnityCliSettingsFunctions
    {
        // ---- Physics ----

        [Description("Read Physics settings: gravity, default solver iterations, bounce threshold.")]
        [ReadOnlyTool]
        public static object GetPhysicsSettings()
        {
            var so = Singleton("PhysicsManager", out var error);
            if (so == null) return error;

            var gravity = so.FindProperty("m_Gravity").vector3Value;
            return Response.Success("Physics settings read.", new
            {
                gravityX = gravity.x,
                gravityY = gravity.y,
                gravityZ = gravity.z,
                defaultSolverIterations = so.FindProperty("m_DefaultSolverIterations").intValue,
                bounceThreshold = so.FindProperty("m_BounceThreshold").floatValue
            });
        }

        [Description("Change Physics settings: {gravityX, gravityY, gravityZ, defaultSolverIterations, bounceThreshold} as a JSON object; " +
                     "omitted fields stay. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetPhysicsSettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            return ApplySingleton("PhysicsManager", settings, confirm, dry_run, new[]
            {
                Field("gravityX", "m_Gravity.x", FieldKind.Float),
                Field("gravityY", "m_Gravity.y", FieldKind.Float),
                Field("gravityZ", "m_Gravity.z", FieldKind.Float),
                Field("defaultSolverIterations", "m_DefaultSolverIterations", FieldKind.Int),
                Field("bounceThreshold", "m_BounceThreshold", FieldKind.Float)
            });
        }

        // ---- Audio ----

        [Description("Read project Audio settings: volume, rolloff scale, doppler factor.")]
        [ReadOnlyTool]
        public static object GetAudioSettings()
        {
            var so = Singleton("AudioManager", out var error);
            if (so == null) return error;

            return Response.Success("Audio settings read.", new
            {
                volume = so.FindProperty("m_Volume").floatValue,
                rolloffScale = so.FindProperty("Rolloff Scale").floatValue,
                dopplerFactor = so.FindProperty("Doppler Factor").floatValue
            });
        }

        [Description("Change project Audio settings: {volume, rolloffScale, dopplerFactor} as a JSON object. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetAudioSettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            return ApplySingleton("AudioManager", settings, confirm, dry_run, new[]
            {
                Field("volume", "m_Volume", FieldKind.Float),
                Field("rolloffScale", "Rolloff Scale", FieldKind.Float),
                Field("dopplerFactor", "Doppler Factor", FieldKind.Float)
            });
        }

        // ---- Time ----

        [Description("Read Time settings: fixedDeltaTime, maximumDeltaTime, timeScale.")]
        [ReadOnlyTool]
        public static object GetTimeSettings() =>
            Response.Success("Time settings read.", new
            {
                fixedDeltaTime = Time.fixedDeltaTime,
                maximumDeltaTime = Time.maximumDeltaTime,
                timeScale = Time.timeScale
            });

        [Description("Change Time settings: {fixedDeltaTime, maximumDeltaTime, timeScale} as a JSON object. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetTimeSettings(
            [ToolParam("JSON object of the fields to change")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            // Fixed Timestep is a compound property in Unity 6, so the Time API is the one reading that works on every version.
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

        [Description("Read QualitySettings: current level, level names, vSync count, anti-aliasing.")]
        [ReadOnlyTool]
        public static object GetQualitySettings() =>
            Response.Success("Quality settings read.", new
            {
                level = QualitySettings.GetQualityLevel(),
                levelNames = QualitySettings.names,
                vSyncCount = QualitySettings.vSyncCount,
                antiAliasing = QualitySettings.antiAliasing
            });

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

        [Description("Read GraphicsSettings: the default render pipeline asset (null for the built-in pipeline).")]
        [ReadOnlyTool]
        public static object GetGraphicsSettings()
        {
            var asset = GraphicsSettings.defaultRenderPipeline;
            return Response.Success("Graphics settings read.", new
            {
                renderPipelineAsset = asset != null ? new { name = asset.name, path = AssetDatabase.GetAssetPath(asset) } : null
            });
        }

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

        [Description("Read the legacy Input Manager axes: names and count.")]
        [ReadOnlyTool]
        public static object GetInputSettings()
        {
            var so = Singleton("InputManager", out var error);
            if (so == null) return error;

            var axes = so.FindProperty("m_Axes");
            var names = Enumerable.Range(0, axes.arraySize).Select(i => axes.GetArrayElementAtIndex(i).FindPropertyRelative("m_Name").stringValue).ToArray();
            return Response.Success($"{names.Length} axis entr{(names.Length == 1 ? "y" : "ies")}.", new { count = names.Length, axes = names.Distinct().ToArray() });
        }

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

        [Description("Read the project's tags and its named layers.")]
        [ReadOnlyTool]
        public static object GetTagsLayers() =>
            Response.Success("Tags and layers read.", new
            {
                tags = InternalEditorUtility.tags,
                layers = Enumerable.Range(0, 32).Where(i => LayerMask.LayerToName(i).Length > 0)
                    .Select(i => new { index = i, name = LayerMask.LayerToName(i) }).ToArray()
            });

        [Description("Add or remove tags and name user layers (index 8-31): {addTags: [..], removeTags: [..], setLayers: [{index, name}]} " +
                     "as a JSON object; an empty layer name clears the slot. Needs confirm=true; dry_run previews. Not undoable.")]
        public static object SetTagsLayers(
            [ToolParam("JSON object with addTags, removeTags and setLayers")] string settings,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            var add = Strings(json["addTags"]);
            var remove = Strings(json["removeTags"]);
            var layers = (json["setLayers"] as JArray ?? new JArray()).OfType<JObject>()
                .Select(l => new { index = l.Value<int?>("index") ?? -1, name = l.Value<string>("name") ?? string.Empty }).ToArray();

            var badLayer = layers.FirstOrDefault(l => l.index < 8 || l.index > 31);
            if (badLayer != null)
                return Response.Error("INVALID_PARAM", new { param = "setLayers", index = badLayer.index }, "User layers are 8 to 31.");

            var changes = add.Select(t => "add tag " + t).Concat(remove.Select(t => "remove tag " + t))
                .Concat(layers.Select(l => $"layer {l.index} '{LayerMask.LayerToName(l.index)}' -> '{l.name}'")).ToList();

            var gate = Gate(confirm, dry_run, changes);
            if (gate != null) return gate;

            foreach (var tag in add) InternalEditorUtility.AddTag(tag);
            foreach (var tag in remove) InternalEditorUtility.RemoveTag(tag);

            if (layers.Length > 0)
            {
                var so = Singleton("TagManager", out var singletonError);
                if (so == null) return singletonError;

                var property = so.FindProperty("layers");
                foreach (var layer in layers)
                    property.GetArrayElementAtIndex(layer.index).stringValue = layer.name;
                so.ApplyModifiedProperties();
            }

            AssetDatabase.SaveAssets();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes });
        }

        [Description("Set a GameObject's tag (the tag must already exist).")]
        public static object SetTag(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Tag to assign")] string tag) =>
            GameObjectFunctions.SetTagAndLayer(target, tag: tag);

        [Description("Set a GameObject's layer by name or by index 0-31.")]
        public static object SetLayer(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Layer name or index")] string layer) =>
            GameObjectFunctions.SetTagAndLayer(target, layer: layer);

        // ---- Build settings ----

        [Description("Set EditorUserBuildSettings fields: {developmentBuild, allowDebugging, connectWithProfiler, buildScriptsOnly, " +
                     "symlinkSources, il2CppCodeGeneration: OptimizeSpeed|OptimizeSize} as a JSON object. Scenes are add_scene_to_build, " +
                     "the target is switch_build_target. Needs confirm=true; dry_run previews.")]
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

        private enum FieldKind { Float, Int }

        private sealed class SettingField
        {
            public string Name;
            public string Path;
            public FieldKind Kind;
        }

        private static SettingField Field(string name, string path, FieldKind kind) => new SettingField { Name = name, Path = path, Kind = kind };

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

        private static object ApplySingleton(string singleton, string settings, bool confirm, bool dryRun, SettingField[] fields)
        {
            if (!TryParse(settings, out var json, out var error)) return error;

            var so = Singleton(singleton, out error);
            if (so == null) return error;

            var unknown = json.Properties().Select(p => p.Name).Where(n => fields.All(f => f.Name != n)).ToArray();
            if (unknown.Length > 0)
                return Response.Error("UNKNOWN_SETTING", new { unknown, valid = fields.Select(f => f.Name).ToArray() });

            var changes = new List<string>();
            foreach (var field in fields)
            {
                if (!json.ContainsKey(field.Name)) continue;
                var property = so.FindProperty(field.Path);
                if (property == null)
                    return Response.Error("SETTING_NOT_FOUND", new { field = field.Name, path = field.Path }, "This Unity version names the property differently.");

                var wanted = Float(json, field.Name);
                if (wanted == null)
                    return Response.Error("INVALID_PARAM", new { param = field.Name, provided = json[field.Name]?.ToString() }, "Expected a number.");

                var current = field.Kind == FieldKind.Int ? property.intValue : property.floatValue;
                changes.Add($"{field.Name} {current} -> {wanted}");
            }

            var gate = Gate(confirm, dryRun, changes);
            if (gate != null) return gate;

            foreach (var field in fields)
            {
                if (!json.ContainsKey(field.Name)) continue;
                var property = so.FindProperty(field.Path);
                var wanted = Float(json, field.Name).Value;
                if (field.Kind == FieldKind.Int) property.intValue = (int)wanted;
                else property.floatValue = wanted;
            }

            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
            return Response.Success($"Applied {changes.Count} change(s).", new { applied = changes });
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
            token is JArray array ? array.Select(t => t.ToString()).Where(s => s.Length > 0).ToArray() : new string[0];

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
