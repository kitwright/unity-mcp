// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Read and write ScriptableObject assets through the same SerializedObject
    /// machinery used for components — picks up [SerializeField] private fields,
    /// supports Object references, returns per-field success.
    /// </summary>
    [ToolProvider("Asset")]
    internal static class ScriptableObjectFunctions
    {
        [Description("Get all serialized properties of a ScriptableObject asset, including [SerializeField] private fields. " +
                     "Works on any .asset file whose main object derives from ScriptableObject.")]
        [ReadOnlyTool]
        public static object GetScriptableObject(
            [ToolParam("Asset path (e.g. 'Assets/Configs/GameConfig.asset')")] string asset_path)
        {
            var resolved = LoadScriptableObject(asset_path);
            if (resolved.Error != null) return resolved.Error;

            var props = ComponentSerializer.ReadProperties(resolved.Asset, out _);
            return Response.Success($"{props.Count} properties on {resolved.Asset.GetType().Name}.", new
            {
                assetPath = asset_path,
                type = resolved.Asset.GetType().FullName,
                instanceId = ObjectIdCodec.GetSerializableId(resolved.Asset),
                properties = props
            });
        }

        [Description("Set one or more serialized properties on a ScriptableObject asset and save it. " +
                     "Pass `properties` as a JSON object: {\"maxHealth\": 100, \"displayName\": \"Boss\", \"icon\": {\"assetPath\": \"Assets/...\"}}. " +
                     "Returns per-field success so partial failures are diagnosable. Changes are Undo-able and persisted to disk.")]
        public static object SetScriptableObjectProperties(
            [ToolParam("Asset path (e.g. 'Assets/Configs/GameConfig.asset')")] string asset_path,
            [ToolParam("JSON object of property→value pairs")] string properties)
        {
            if (string.IsNullOrWhiteSpace(properties))
                return Response.Error("PROPERTIES_REQUIRED");

            var resolved = LoadScriptableObject(asset_path);
            if (resolved.Error != null) return resolved.Error;

            JObject jobj;
            try { jobj = JsonParse.Object(properties); }
            catch (Exception ex) { return Response.Error("INVALID_PROPERTIES_JSON", new { message = ex.Message }); }

            var results = ComponentSerializer.WriteProperties(resolved.Asset, jobj,
                $"Set properties on {resolved.Asset.GetType().Name}");

            AssetDatabase.SaveAssetIfDirty(resolved.Asset);

            int success = results.Count(r => r.Success);
            int fail = results.Count - success;

            if (success == 0)
                return Response.Error("PROPERTY_SET_FAILED", new { assetPath = asset_path, fields = results });

            return Response.Success(
                $"Applied {success} of {results.Count} field(s) on {resolved.Asset.GetType().Name}, asset saved.",
                new
                {
                    assetPath = asset_path,
                    successCount = success,
                    failCount = fail,
                    fields = results
                });
        }

        // -------- Helpers --------

        private struct ResolvedScriptableObject
        {
            public ScriptableObject Asset;
            public object Error;
        }

        private static ResolvedScriptableObject LoadScriptableObject(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return new ResolvedScriptableObject { Error = Response.Error("ASSET_PATH_REQUIRED") };

            var obj = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (obj == null)
                return new ResolvedScriptableObject { Error = Response.Error("ASSET_NOT_FOUND", new { asset_path = assetPath }) };

            if (!(obj is ScriptableObject so))
                return new ResolvedScriptableObject
                {
                    Error = Response.Error("NOT_A_SCRIPTABLE_OBJECT",
                        new { asset_path = assetPath, actualType = obj.GetType().FullName })
                };

            return new ResolvedScriptableObject { Asset = so };
        }
    }
}
