// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// The Unity CLI-named tools that do something KitWright's own tools do not: the typed multi-property material write, the curve
    /// write with tangents and the Unity Search query.
    /// </summary>
    [ToolProvider("Asset")]
    internal static class UnityCliAssetFunctions
    {
        private const int MaxSearchResults = 200;

        [Description("Set shader properties on a material: properties is a JSON object of property name (with its leading underscore) to value " +
                     "(number; color as [r,g,b,a] or '#RRGGBB[AA]'; vector as [x,y,z,w]; texture as a path/GUID or null). It can also reassign the " +
                     "shader, set the render queue (-1 inherits) and switch keywords. Unknown names are reported, not fatal. dry_run reports only. " +
                     "The edit lands on the shared material, so every renderer using it changes; built-in defaults, materials in immutable " +
                     "packages and read-only ones (a model's embedded material) are refused with MATERIAL_READ_ONLY.")]
        [ToolProvider("Materials & Shaders")]
        public static object SetMaterialProperties(
            [ToolParam("Material path or GUID, or a scene GameObject id/name/path whose Renderer.sharedMaterial is edited")] string material,
            [ToolParam("JSON object of property name to value", Required = false)] string properties = null,
            [ToolParam("Shader to assign first, by name", Required = false)] string shader = null,
            [ToolParam("Render queue, or -1 to inherit; omit to leave it", Required = false)] int? renderQueue = null,
            [ToolParam("JSON array of keywords to enable", Required = false)] string enableKeywords = null,
            [ToolParam("JSON array of keywords to disable", Required = false)] string disableKeywords = null,
            [ToolParam("Validate and report without writing", Required = false)] bool dry_run = false,
            [ToolParam("Accepted for parity; editing a material is undoable", Required = false)] bool confirm = false)
        {
            var target = AssetDatabase.LoadAssetAtPath<Material>(AssetPath(material));
            string source = null;
            if (target == null)
                target = MaterialFunctions.ResolveMaterial(material, out source);
            if (target == null)
                return Response.Error("MATERIAL_NOT_FOUND", new { material },
                    "Pass a .mat asset path or GUID, or a scene GameObject with a Renderer that has a sharedMaterial.");

            var path = AssetDatabase.GetAssetPath(target);
            var persistent = EditorUtility.IsPersistent(target);
            if (!MaterialFunctions.IsEditableMaterial(path, persistent, (target.hideFlags & HideFlags.NotEditable) != 0, MaterialFunctions.IsWritablePackageAsset(path)))
                return Response.Error("MATERIAL_READ_ONLY", new { material = target.name, assetPath = path, source },
                    "This material is a built-in default, lives in an immutable package, or is read-only (such as one " +
                    "embedded in a model): it cannot be saved, and an edit would change every renderer using it. " +
                    "Create a project material with create_material, assign it with assign_material, then edit that one.");

            Shader newShader = null;
            if (!string.IsNullOrWhiteSpace(shader) && (newShader = Shader.Find(shader.Trim())) == null)
                return Response.Error("SHADER_NOT_FOUND", new { shader });

            if (!UnityCliSettingsFunctions.TryParse(properties, out var json, out var error))
                return error;

            var effective = newShader ?? target.shader;
            var applied = new List<string>();
            var unknown = new List<string>();
            var writes = new List<Action>();

            foreach (var property in json.Properties())
            {
                var index = effective.FindPropertyIndex(property.Name);
                if (index < 0) { unknown.Add(property.Name + " (no such property)"); continue; }

                var value = property.Value;
                var name = property.Name;
                switch (effective.GetPropertyType(index))
                {
                    case ShaderPropertyType.Float:
                    case ShaderPropertyType.Range:
                        if (!IsFinite(value)) { unknown.Add(name + " (expected a finite number within float range)"); continue; }
                        writes.Add(() => target.SetFloat(name, value.Value<float>()));
                        break;
                    case ShaderPropertyType.Int:
                        if (!IsNumber(value) || Math.Abs(value.Value<double>()) > int.MaxValue) { unknown.Add(name + " (expected a number within int range)"); continue; }
                        writes.Add(() => target.SetInteger(name, value.Value<int>()));
                        break;
                    case ShaderPropertyType.Color:
                        if (!TryColor(value, out var color)) { unknown.Add(name + " (expected [r,g,b,a] or a hex string)"); continue; }
                        writes.Add(() => target.SetColor(name, color));
                        break;
                    case ShaderPropertyType.Vector:
                        if (!(value is JArray vector) || vector.Count < 2 || vector.Count > 4 || !vector.All(IsFinite)) { unknown.Add(name + " (expected [x,y,z,w] of finite numbers)"); continue; }
                        writes.Add(() => target.SetVector(name, new Vector4(Component(vector, 0), Component(vector, 1), Component(vector, 2), Component(vector, 3))));
                        break;
                    case ShaderPropertyType.Texture:
                        Texture texture = null;
                        if (value.Type != JTokenType.Null)
                        {
                            texture = AssetDatabase.LoadAssetAtPath<Texture>(AssetPath(value.Type == JTokenType.Object ? (value["path"] ?? value["guid"])?.ToString() : value.ToString()));
                            if (texture == null) { unknown.Add(name + " (texture not found)"); continue; }
                        }
                        writes.Add(() => target.SetTexture(name, texture));
                        break;
                    default:
                        unknown.Add(name + " (unsupported type)");
                        continue;
                }

                applied.Add(name);
            }

            var enable = Keywords(enableKeywords);
            var disable = Keywords(disableKeywords);

            if (dry_run)
                return Response.Success("Validated; nothing was written.", new { applied, unknown, dry_run = true });

            Undo.RecordObject(target, "Set material properties");
            if (newShader != null) target.shader = newShader;
            foreach (var write in writes) write();
            foreach (var keyword in enable) target.EnableKeyword(keyword);
            foreach (var keyword in disable) target.DisableKeyword(keyword);
            if (renderQueue != null) target.renderQueue = renderQueue.Value;

            EditorUtility.SetDirty(target);
            var message = $"Applied {applied.Count} propert{(applied.Count == 1 ? "y" : "ies")} to material '{target.name}'.";
            if (persistent)
            {
                AssetDatabase.SaveAssetIfDirty(target);
                return Response.Success(message, new { applied, unknown, assetPath = path, source,
                    note = $"Edited the material asset '{path}'; every renderer using it changed too." });
            }

            // A scene-embedded material has no asset to save: the scene has to show as modified, or the edit vanishes on reload.
            var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
            if (scene.IsValid() && !EditorApplication.isPlaying)
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(scene);
            return Response.Success(message, new { applied, unknown, assetPath = (string)null, source,
                note = "The material is scene-embedded (not an asset); the scene was marked dirty. Save the scene to persist." });
        }

#if KITWRIGHT_ANIMATION
        [Description("Add or replace one float curve on an Animation Clip: keys is a JSON array of {time, value, inTangent?, outTangent?, " +
                     "weightedMode?: None|In|Out|Both}. tangents shapes the keys that give none: 'linear' (default, straight lines between " +
                     "keys) or 'flat' (0). property is the serialized field, for a Transform a single axis: 'm_LocalPosition.x'. Re-running for " +
                     "the same binding replaces it. dry_run validates only.")]
        [ToolProvider("Animation")]
        public static object SetAnimationCurve(
            [ToolParam("Animation Clip path or GUID")] string clip,
            [ToolParam("Component type the property lives on, e.g. 'Transform'")] string type,
            [ToolParam("Property name, e.g. 'm_LocalPosition.x'")] string property,
            [ToolParam("JSON array of keyframes")] string keys,
            [ToolParam("Child path under the animated root; empty for the root", Required = false)] string path = "",
            [ToolParam("Validate without writing", Required = false)] bool dry_run = false,
            [ToolParam("'linear' (default) or 'flat': the tangents of keys that give none", Required = false)] string tangents = "linear")
        {
            var clipPath = AssetPath(clip);
            var animation = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (animation == null) return Response.Error("ANIMATION_CLIP_NOT_FOUND", new { clip = clipPath });

            var componentType = TypeResolver.Resolve(type);
            if (componentType == null) return Response.Error("TYPE_NOT_FOUND", new { type });
            if (string.IsNullOrWhiteSpace(property)) return Response.Error("INVALID_PARAM", new { param = "property" });

            var linear = string.IsNullOrWhiteSpace(tangents) || string.Equals(tangents.Trim(), "linear", StringComparison.OrdinalIgnoreCase);
            if (!linear && !string.Equals(tangents.Trim(), "flat", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PARAM", new { param = "tangents", provided = tangents }, "tangents is 'linear' or 'flat'.");

            JArray parsed;
            try { parsed = JsonParse.Array(keys); }
            catch (Exception ex) { return Response.Error("INVALID_KEYS_JSON", new { message = ex.Message }); }

            if (parsed.Count == 0) return Response.Error("NO_KEYFRAMES", hint: "Pass at least one {time, value}.");

            var frames = new Keyframe[parsed.Count];
            var explicitIn = new bool[parsed.Count];
            var explicitOut = new bool[parsed.Count];
            for (var i = 0; i < parsed.Count; i++)
            {
                if (!(parsed[i] is JObject key) || !IsFinite(key["time"]) || !IsFinite(key["value"]))
                    return Response.Error("INVALID_KEYFRAME", new { index = i }, "Each key needs a finite numeric time and value.");

                explicitIn[i] = IsFinite(key["inTangent"]);
                explicitOut[i] = IsFinite(key["outTangent"]);
                var frame = new Keyframe(key.Value<float>("time"), key.Value<float>("value"),
                    explicitIn[i] ? key.Value<float>("inTangent") : 0f,
                    explicitOut[i] ? key.Value<float>("outTangent") : 0f);

                var mode = key.Value<string>("weightedMode");
                if (mode != null)
                {
                    if (!Enum.TryParse(mode, true, out WeightedMode weighted))
                        return Response.Error("INVALID_KEYFRAME", new { index = i, weightedMode = mode }, "weightedMode is None, In, Out or Both.");
                    frame.weightedMode = weighted;
                }

                frames[i] = frame;
            }

            if (frames.Select(f => f.time).Distinct().Count() != frames.Length)
                return Response.Error("INVALID_KEYFRAME", hint: "Key times must be distinct.");

            if (dry_run)
                return Response.Success("Validated; nothing was written.", new { keys = frames.Length, dry_run = true });

            var curve = new AnimationCurve(frames);
            if (linear)
            {
                // AnimationCurve sorts its keys by time, so the explicit flags are matched back by time, not by index.
                for (var i = 0; i < curve.length; i++)
                {
                    var original = Array.FindIndex(frames, f => f.time == curve.keys[i].time);
                    if (!explicitIn[original]) AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                    if (!explicitOut[original]) AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                }
            }

            var binding = EditorCurveBinding.FloatCurve(path ?? string.Empty, componentType, property);
            Undo.RecordObject(animation, $"Set curve {property}");
            AnimationUtility.SetEditorCurve(animation, binding, curve);
            EditorUtility.SetDirty(animation);
            AssetDatabase.SaveAssetIfDirty(animation);
            return Response.Success($"Set a {curve.length}-key curve on '{property}' in '{clipPath}'. Clip length is now {animation.length:0.###}s.",
                new { clip = clipPath, property, keys = curve.length, length = animation.length });
        }
#endif

        [Description("Run a Unity Search query and return the results, e.g. 't:Material', 'p: my asset', 'h: Main Camera'.")]
        [ReadOnlyTool]
        public static object Search(
            [ToolParam("Unity Search query")] string query,
            [ToolParam("Most results, up to 200. Default 50.", Required = false)] int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(query))
                return Response.Error("INVALID_PARAM", new { param = "query" });

            limit = Math.Max(1, Math.Min(limit, MaxSearchResults));
            using (var context = UnityEditor.Search.SearchService.CreateContext(query))
            {
                var items = UnityEditor.Search.SearchService.GetItems(context, UnityEditor.Search.SearchFlags.Synchronous).Take(limit)
                    .Select(i => new { id = i.id, label = i.GetLabel(context, true), description = i.GetDescription(context, true), provider = i.provider?.id }).ToArray();
                return Response.Success($"{items.Length} result(s).", new { count = items.Length, results = items });
            }
        }

        // ---- helpers ----

        private static string AssetPath(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference)) return reference;
            var text = reference.Trim();
            if (text.Length == 32 && text.All(Uri.IsHexDigit))
            {
                var fromGuid = AssetDatabase.GUIDToAssetPath(text);
                if (!string.IsNullOrEmpty(fromGuid)) return fromGuid;
            }

            return UnityCliPaths.Resolve(text, string.Empty, out var resolved, out _, out _) ? resolved : text;
        }

        private static bool IsNumber(JToken token) =>
            token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);

        // Read as double: 1e40 read as float is already Infinity, and that would be stored.
        private static bool IsFinite(JToken token) =>
            IsNumber(token) && Math.Abs(token.Value<double>()) <= float.MaxValue;

        private static float Component(JArray array, int index) => index < array.Count ? array[index].Value<float>() : 0f;

        private static bool TryColor(JToken token, out Color color)
        {
            color = default;
            if (token is JArray array && array.Count >= 3 && array.Count <= 4 && array.All(IsFinite))
            {
                color = new Color(Component(array, 0), Component(array, 1), Component(array, 2), array.Count == 4 ? Component(array, 3) : 1f);
                return true;
            }

            return token != null && token.Type == JTokenType.String && ColorUtility.TryParseHtmlString(token.ToString(), out color);
        }

        private static string[] Keywords(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new string[0];
            try { return JsonParse.Array(json).Select(t => t.ToString()).Where(k => k.Length > 0).ToArray(); }
            catch { return new string[0]; }
        }
    }

    /// <summary>Asset paths as the Unity CLI writes them: the Assets/ prefix and the extension are optional, bare paths follow the authoring root.</summary>
    internal static class UnityCliPaths
    {
        internal static bool Resolve(string path, string extension, out string assetPath, out string absolutePath, out object error)
        {
            assetPath = null;
            absolutePath = null;
            error = null;

            var text = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (text.Length > 0 && !text.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(System.IO.Path.GetExtension(text)))
                text += extension;

            var bare = !text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
            if (bare && AuthoringRoot.Explicit == null && text.Length > 0)
                text = "Assets/" + text.TrimStart('/');

            if (!AssetFunctions.TryResolveUnderAssetsOrPackages(text, out assetPath, out absolutePath))
            {
                error = Response.Error("INVALID_PATH", new { path }, "path must be a file under Assets/ (or under the authoring root) with no '..' segments.");
                return false;
            }

            return true;
        }
    }
}
