// Copyright (C) KitWright. All rights reserved.
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("ProjectSettings")]
    internal static class ProjectSettingsFunctions
    {
        // PlayerSettings descends to ~1200 properties.
        private const int MaxDumpedProperties = 400;

        [Description("Read project settings. Called with no argument, report a flat snapshot: QualitySettings (level names + " +
                     "current level index/name), PlayerSettings (applicationIdentifier, productName, companyName, " +
                     "Android/Standalone scripting backend, Android target/min SDK versions), and the active build target. " +
                     "Each PlayerSettings read is guarded individually so one unavailable field (e.g. Android module not " +
                     "installed) does not fail the whole call — unreadable fields are omitted from the data and listed in the " +
                     "'failures' array.\n" +
                     "Pass `singleton` instead to dump every serialized property of one Project Settings page, which is how you " +
                     "discover the real field names (the Physics window's \"Layer Collision Matrix\" is 'm_LayerCollisionMatrix', " +
                     "a 32-element array). Nested pages are descended, and each 'Name' is then the full property path you can " +
                     "hand straight to FindProperty (e.g. 'm_QualitySettings.Array.data[0].shadowResolution'); at most 400 are " +
                     "returned and 'propertyCount' reports the real total. This tool is read-only — hand the paths it " +
                     "reports straight to set_project_settings to write them back.\n" +
                     "Patching ProjectSettings/*.asset as text while the editor is open is picked up only if an " +
                     "AssetDatabase.Refresh follows. Skip that and the file and the editor's in-memory copy of the page " +
                     "disagree, and the in-memory one wins the next time Unity writes settings.")]
        [ReadOnlyTool]
        public static object GetProjectSettings(
            [ToolParam("Optional settings singleton to dump in full: 'PhysicsManager', 'Physics2DSettings', 'QualitySettings', " +
                       "'GraphicsSettings', 'TimeManager', 'AudioManager', 'InputManager', 'TagManager', 'PlayerSettings', " +
                       "'MonoManager' (script execution order), 'NavMeshProjectSettings', 'EditorSettings', 'VFXManager', " +
                       "'MemorySettings'. Omit for the flat summary.", Required = false)] string singleton = null)
        {
            if (!string.IsNullOrWhiteSpace(singleton))
                return DumpSettingsSingleton(singleton);

            var result = new Dictionary<string, object>();
            var failures = new List<string>();

            // --- QualitySettings ---
            try
            {
                var names = QualitySettings.names;
                var level = QualitySettings.GetQualityLevel();
                result["qualityLevelNames"] = names;
                result["currentQualityLevel"] = level;
                result["currentQualityName"] = (names != null && level >= 0 && level < names.Length) ? names[level] : null;
            }
            catch (Exception ex) { failures.Add("qualitySettings: " + ex.Message); }

            // --- PlayerSettings (each read guarded so one missing field doesn't sink the call) ---
            TryRead(result, failures, "applicationIdentifier", () => PlayerSettings.applicationIdentifier);
            TryRead(result, failures, "productName", () => PlayerSettings.productName);
            TryRead(result, failures, "companyName", () => PlayerSettings.companyName);
            TryRead(result, failures, "scriptingBackendAndroid", () => ReadScriptingBackend("Android"));
            TryRead(result, failures, "scriptingBackendStandalone", () => ReadScriptingBackend("Standalone"));
            TryRead(result, failures, "androidTargetSdkVersion", () => PlayerSettings.Android.targetSdkVersion.ToString());
            TryRead(result, failures, "androidMinSdkVersion", () => PlayerSettings.Android.minSdkVersion.ToString());

            // --- Active build target ---
            TryRead(result, failures, "activeBuildTarget", () => EditorUserBuildSettings.activeBuildTarget.ToString());

            result["failures"] = failures;

            return Response.Success(
                failures.Count == 0
                    ? "Project settings read."
                    : $"Project settings read ({failures.Count} field(s) unavailable, see 'failures').",
                result);
        }

        [Description("Write project settings: apply a propertyPath→value map to one Project Settings page. " +
                     "Call get_project_settings with the same `singleton` first — every 'Name' it reports is a path you " +
                     "can pass here verbatim ('m_Gravity', 'm_LayerCollisionMatrix.Array.data[8]'). Fields are applied " +
                     "independently and reported one by one, so a bad path does not sink the rest of the call.\n" +
                     "The Physics page's layer collision matrix is 'm_LayerCollisionMatrix', 32 masks where bit b of " +
                     "entry a means 'layer a collides with layer b' — Unity keeps it symmetric, so clear or set both " +
                     "entry a bit b and entry b bit a.\n" +
                     "Writes go through SerializedObject and are flushed with AssetDatabase.SaveAssets, so the editor's " +
                     "copy of the page and the file on disk move together. Patching ProjectSettings/*.asset as text " +
                     "instead is picked up only if an AssetDatabase.Refresh follows; without one the editor keeps its " +
                     "own copy and overwrites the edit the next time Unity writes settings.")]
        public static object SetProjectSettings(
            [ToolParam("Settings singleton to write — the same names get_project_settings accepts ('PhysicsManager', " +
                       "'TimeManager', 'QualitySettings', 'GraphicsSettings', ...).")] string singleton,
            [ToolParam("JSON object of propertyPath→value pairs, e.g. {\"m_Gravity\": {\"x\": 0, \"y\": -20, \"z\": 0}}")] string properties)
        {
            if (string.IsNullOrWhiteSpace(properties))
                return Response.Error("PROPERTIES_REQUIRED");

            var target = ResolveSettingsSingleton(singleton, out var resolveError);
            if (target == null) return resolveError;

            JObject parsed;
            try { parsed = JObject.Parse(properties); }
            catch (Exception ex) { return Response.Error("INVALID_PROPERTIES_JSON", new { message = ex.Message }); }

            var results = ComponentSerializer.WriteProperties(target, parsed, $"Set {singleton}");

            // The singleton is outside the AssetDatabase, so SetDirty alone leaves the change in memory
            // until Unity next decides to write ProjectSettings/*.asset.
            AssetDatabase.SaveAssets();

            var success = results.Count(r => r.Success);
            if (success == 0)
                return Response.Error("PROPERTY_SET_FAILED", new { singleton, fields = results });

            return Response.Success($"Applied {success} of {results.Count} field(s) on {singleton}.", new
            {
                singleton,
                successCount = success,
                failCount = results.Count - success,
                fields = results
            });
        }

        // --- Helpers ---

        // Settings pages are native singletons outside the AssetDatabase, keyed by the class name in ProjectSettings/*.asset.
        private static UnityEngine.Object ResolveSettingsSingleton(string singleton, out object error)
        {
            error = null;
            var target = Unsupported.GetSerializedAssetInterfaceSingleton(singleton);
            if (target != null) return target;

            error = Response.Error("SETTINGS_SINGLETON_NOT_FOUND", new
            {
                singleton,
                hint = "Name is case-sensitive and is the YAML class key of a ProjectSettings/*.asset file " +
                       "(DynamicsManager.asset -> 'PhysicsManager'). Read line 4 of the file to confirm."
            });
            return null;
        }

        private static object DumpSettingsSingleton(string singleton)
        {
            var target = ResolveSettingsSingleton(singleton, out var error);
            if (target == null) return error;

            // Nested pages (QualitySettings, TimeManager) hide every real field inside a container,
            // and settings singletons keep writable fields off the inspector entirely — without
            // includeHidden this dump omits m_LayerCollisionMatrix, which set_project_settings can write.
            var all = ComponentSerializer.ReadProperties(target, out _, descend: true, includeHidden: true);
            var shown = Math.Min(all.Count, MaxDumpedProperties);

            return Response.Success($"{singleton} read ({shown} of {all.Count} properties).", new
            {
                singleton,
                type = target.GetType().FullName,
                propertyCount = all.Count,
                properties = all.GetRange(0, shown)
            });
        }

        // Guard a single PlayerSettings/BuildSettings read: on success store under `key`, on failure record the message.
        private static void TryRead(Dictionary<string, object> result, List<string> failures, string key, Func<object> read)
        {
            try { result[key] = read(); }
            catch (Exception ex) { failures.Add(key + ": " + ex.Message); }
        }

        // Read the scripting backend for a platform using the modern NamedBuildTarget overload,
        // falling back (via reflection, to avoid a compile-time obsolete warning) to the legacy
        // BuildTargetGroup overload if the modern one throws. Throws on total failure so the caller
        // records it as a failed field.
        private static string ReadScriptingBackend(string platform)
        {
            try
            {
                var named = platform == "Android" ? NamedBuildTarget.Android : NamedBuildTarget.Standalone;
                return PlayerSettings.GetScriptingBackend(named).ToString();
            }
            catch
            {
                var group = platform == "Android" ? BuildTargetGroup.Android : BuildTargetGroup.Standalone;
                var mi = typeof(PlayerSettings).GetMethod("GetScriptingBackend", new[] { typeof(BuildTargetGroup) });
                if (mi == null)
                    throw new InvalidOperationException("No GetScriptingBackend(BuildTargetGroup) overload available in this Unity version.");
                return mi.Invoke(null, new object[] { group }).ToString();
            }
        }
    }
}
