// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Rendering;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Asset, file, material, package, search and screenshot tools under the Unity CLI's names and parameter shapes. Each calls what
    /// KitWright already does where it can; the few that were missing (the typed material write, the serialized-field read, the
    /// curve write with tangents, the Unity Search query) are implemented here.
    /// </summary>
    [ToolProvider("Asset")]
    internal static class UnityCliAssetFunctions
    {
        private const int MaxSearchResults = 200;
        private const int MaxSerializedFields = 400;

        // ---- Files ----

        [Description("Read a UTF-8 text file under Assets/ (or the authoring root) and return its contents.")]
        [ReadOnlyTool]
        [ToolProvider("File")]
        public static object ReadTextFile(
            [ToolParam("File path; the Assets/ prefix is optional")] string path,
            [ToolParam("Refuse a file larger than this many bytes. Default 1048576.", Required = false)] int max_bytes = 1048576)
        {
            if (!UnityCliPaths.Resolve(path, string.Empty, out var assetPath, out var absolute, out var error))
                return error;

            if (!File.Exists(absolute))
                return Response.Error("FILE_NOT_FOUND", new { path = assetPath });

            var size = new FileInfo(absolute).Length;
            if (size > max_bytes)
                return Response.Error("FILE_TOO_LARGE", new { path = assetPath, bytes = size, max_bytes });

            return Response.Success($"Read '{assetPath}' ({size} bytes).", new { path = assetPath, bytes = size, contents = File.ReadAllText(absolute, Encoding.UTF8) });
        }

        [Description("Write UTF-8 text to a file under Assets/ (or the authoring root), then import it. Replacing a file needs confirm=true; " +
                     "dry_run reports what would be written.")]
        [ToolProvider("File")]
        public static object WriteTextFile(
            [ToolParam("File path with its extension; the Assets/ prefix is optional")] string path,
            [ToolParam("The full text to write")] string contents,
            [ToolParam("Required to replace an existing file", Required = false)] bool confirm = false,
            [ToolParam("Validate and report without writing", Required = false)] bool dry_run = false)
        {
            if (!UnityCliPaths.Resolve(path, string.Empty, out var assetPath, out var absolute, out var error))
                return error;

            if (string.IsNullOrEmpty(Path.GetExtension(assetPath)) || assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PATH", new { path }, "Give the file name with an extension, and not .meta.");

            var exists = File.Exists(absolute);
            if (exists && !confirm && !dry_run)
                return Response.Error("FILE_EXISTS", new { path = assetPath }, "A file is already there. Pass confirm=true to replace it.");

            if (dry_run)
                return Response.Success($"Would write {(contents ?? string.Empty).Length} character(s) to '{assetPath}'.", new { path = assetPath, replaces = exists, dry_run = true });

            Directory.CreateDirectory(Path.GetDirectoryName(absolute));
            AtomicFile.WriteAllText(absolute, contents ?? string.Empty);
            AssetDatabase.ImportAsset(assetPath);
            return Response.Success($"Wrote '{assetPath}'.", new { path = assetPath, replaced = exists });
        }

        // ---- Import settings, shaders, materials ----

        [Description("Read an asset's import settings, by path or GUID. KitWright reads the default platform; a platform other than Default is refused.")]
        [ReadOnlyTool]
        public static object GetImportSettings(
            [ToolParam("Asset path or GUID")] string asset,
            [ToolParam("Default (the only one supported)", Required = false)] string platform = "Default")
        {
            if (!string.Equals(platform ?? "Default", "Default", StringComparison.OrdinalIgnoreCase))
                return Response.Error("PLATFORM_UNSUPPORTED", new { platform }, "Only the Default platform's importer properties are read. Per-platform overrides are not.");

            return AssetImportFunctions.GetAssetImportSettings(AssetPath(asset));
        }

        [Description("Set import settings on an asset's importer and re-import it: settings is a JSON object of importer property names to values. " +
                     "Only the Default platform is supported. dry_run reports without writing.")]
        public static object SetImportSettings(
            [ToolParam("Asset path or GUID")] string asset,
            [ToolParam("JSON object of importer property names to values")] string settings,
            [ToolParam("Default (the only one supported)", Required = false)] string platform = "Default",
            [ToolParam("Validate and report without writing", Required = false)] bool dry_run = false)
        {
            if (!string.Equals(platform ?? "Default", "Default", StringComparison.OrdinalIgnoreCase))
                return Response.Error("PLATFORM_UNSUPPORTED", new { platform }, "Only the Default platform's importer properties are written.");

            if (dry_run)
            {
                if (!UnityCliSettingsFunctions.TryParse(settings, out var json, out var error)) return error;
                return Response.Success("Would set the listed importer properties.", new { asset = AssetPath(asset), keys = json.Properties().Select(p => p.Name).ToArray(), dry_run = true });
            }

            return AssetImportFunctions.SetAssetImportSettings(AssetPath(asset), settings);
        }

        [Description("Introspect a shader's declared properties (name, description, type, range, flags). Pass the shader's name or a material to read it from.")]
        [ReadOnlyTool]
        [ToolProvider("Materials & Shaders")]
        public static object GetShaderProperties(
            [ToolParam("Shader name, e.g. 'Universal Render Pipeline/Lit'", Required = false)] string shader = null,
            [ToolParam("Material path or GUID to read the shader from", Required = false)] string material = null)
        {
            if (string.IsNullOrWhiteSpace(shader) == string.IsNullOrWhiteSpace(material))
                return Response.Error("INVALID_PARAM", hint: "Pass the shader's name, or a material, but not both and not neither.");

            var name = shader;
            if (!string.IsNullOrWhiteSpace(material))
            {
                var loaded = AssetDatabase.LoadAssetAtPath<Material>(AssetPath(material));
                if (loaded == null) return Response.Error("MATERIAL_NOT_FOUND", new { material });
                name = loaded.shader.name;
            }

            return ShaderFunctions.GetShaderInfo(name);
        }

        [Description("Set shader properties on a material: properties is a JSON object of property name (with its leading underscore) to value " +
                     "(number; color as [r,g,b,a] or '#RRGGBB[AA]'; vector as [x,y,z,w]; texture as a path/GUID or null). It can also reassign the " +
                     "shader, set the render queue (-1 inherits) and switch keywords. Unknown names are reported, not fatal. dry_run reports only.")]
        [ToolProvider("Materials & Shaders")]
        public static object SetMaterialProperties(
            [ToolParam("Material path or GUID")] string material,
            [ToolParam("JSON object of property name to value", Required = false)] string properties = null,
            [ToolParam("Shader to assign first, by name", Required = false)] string shader = null,
            [ToolParam("Render queue, or -1 to inherit; omit to leave it", Required = false)] int? renderQueue = null,
            [ToolParam("JSON array of keywords to enable", Required = false)] string enableKeywords = null,
            [ToolParam("JSON array of keywords to disable", Required = false)] string disableKeywords = null,
            [ToolParam("Validate and report without writing", Required = false)] bool dry_run = false,
            [ToolParam("Accepted for parity; editing a material is undoable", Required = false)] bool confirm = false)
        {
            var path = AssetPath(material);
            var target = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (target == null)
                return Response.Error("MATERIAL_NOT_FOUND", new { material = path });

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
                        if (!IsNumber(value)) { unknown.Add(name + " (expected a number)"); continue; }
                        writes.Add(() => target.SetFloat(name, value.Value<float>()));
                        break;
                    case ShaderPropertyType.Int:
                        if (!IsNumber(value)) { unknown.Add(name + " (expected a number)"); continue; }
                        writes.Add(() => target.SetInteger(name, value.Value<int>()));
                        break;
                    case ShaderPropertyType.Color:
                        if (!TryColor(value, out var color)) { unknown.Add(name + " (expected [r,g,b,a] or a hex string)"); continue; }
                        writes.Add(() => target.SetColor(name, color));
                        break;
                    case ShaderPropertyType.Vector:
                        if (!(value is JArray vector) || vector.Count < 2 || vector.Count > 4) { unknown.Add(name + " (expected [x,y,z,w])"); continue; }
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
            AssetDatabase.SaveAssetIfDirty(target);
            return Response.Success($"Applied {applied.Count} propert{(applied.Count == 1 ? "y" : "ies")} to '{path}'.", new { applied, unknown });
        }

        // ---- Serialized fields, animation curves ----

        [Description("Read the serialized fields of a component or asset: name, type and value. target is a GameObject (with component naming the " +
                     "component), a component, or an asset path/GUID. field reads one property; format 'value' returns only values.")]
        [ReadOnlyTool]
        [ToolProvider("GameObject")]
        public static object GetSerializedFields(
            [ToolParam("GameObject identifier, or an asset path/GUID")] string target,
            [ToolParam("Component type on the GameObject, e.g. 'Rigidbody'", Required = false)] string component = null,
            [ToolParam("One property name or path to read", Required = false)] string field = null,
            [ToolParam("'value' for values only", Required = false)] string format = null)
        {
            UnityEngine.Object source = null;
            var go = ObjectsHelper.FindObject(target, null);
            if (go != null)
            {
                if (string.IsNullOrWhiteSpace(component))
                    return Response.Error("COMPONENT_REQUIRED", new { target }, "Name the component on the GameObject.");

                var type = TypeResolver.ResolveComponent(component);
                if (type == null) return TypeResolver.UnresolvedError(component, "COMPONENT_TYPE_NOT_FOUND", nameof(component));

                source = go.GetComponent(type);
                if (source == null) return Response.Error("COMPONENT_NOT_ON_TARGET", new { target = go.name, component });
            }
            else
            {
                var path = AssetPath(target);
                source = AssetDatabase.LoadMainAssetAtPath(path);
                if (source == null) return ObjectsHelper.NotFound(nameof(target), target);
            }

            var snapshots = ComponentSerializer.ReadProperties(source, out var total, descend: false, maxProperties: MaxSerializedFields);
            if (!string.IsNullOrWhiteSpace(field))
            {
                var one = snapshots.FirstOrDefault(s => s.Name == field);
                if (one == null) return Response.Error("FIELD_NOT_FOUND", new { field, available = snapshots.Select(s => s.Name).ToArray() });
                snapshots = new List<ComponentSerializer.PropertySnapshot> { one };
            }

            var asValues = string.Equals(format, "value", StringComparison.OrdinalIgnoreCase);
            return Response.Success($"{snapshots.Count} field(s) of {source.GetType().Name}.", new
            {
                type = source.GetType().FullName,
                total,
                fields = asValues ? (object)snapshots.ToDictionary(s => s.Name, s => s.Value) : snapshots.Select(s => new { name = s.Name, type = s.Type, value = s.Value }).ToArray()
            });
        }

#if KITWRIGHT_ANIMATION
        [Description("Add or replace one float curve on an Animation Clip: keys is a JSON array of {time, value, inTangent?, outTangent?, " +
                     "weightedMode?: None|In|Out|Both}; omitted tangents are flat (0), not Auto. dry_run validates only.")]
        [ToolProvider("Animation")]
        public static object SetAnimationCurve(
            [ToolParam("Animation Clip path or GUID")] string clip,
            [ToolParam("Component type the property lives on, e.g. 'Transform'")] string type,
            [ToolParam("Property name, e.g. 'm_LocalPosition.x'")] string property,
            [ToolParam("JSON array of keyframes")] string keys,
            [ToolParam("Child path under the animated root; empty for the root", Required = false)] string path = "",
            [ToolParam("Validate without writing", Required = false)] bool dry_run = false)
        {
            var clipPath = AssetPath(clip);
            var animation = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (animation == null) return Response.Error("ANIMATION_CLIP_NOT_FOUND", new { clip = clipPath });

            var componentType = TypeResolver.Resolve(type);
            if (componentType == null) return Response.Error("TYPE_NOT_FOUND", new { type });
            if (string.IsNullOrWhiteSpace(property)) return Response.Error("INVALID_PARAM", new { param = "property" });

            JArray parsed;
            try { parsed = JsonParse.Array(keys); }
            catch (Exception ex) { return Response.Error("INVALID_KEYS_JSON", new { message = ex.Message }); }

            if (parsed.Count == 0) return Response.Error("NO_KEYFRAMES", hint: "Pass at least one {time, value}.");

            var frames = new Keyframe[parsed.Count];
            for (var i = 0; i < parsed.Count; i++)
            {
                if (!(parsed[i] is JObject key) || !IsNumber(key["time"]) || !IsNumber(key["value"]))
                    return Response.Error("INVALID_KEYFRAME", new { index = i }, "Each key needs a numeric time and value.");

                var frame = new Keyframe(key.Value<float>("time"), key.Value<float>("value"),
                    key["inTangent"] != null && IsNumber(key["inTangent"]) ? key.Value<float>("inTangent") : 0f,
                    key["outTangent"] != null && IsNumber(key["outTangent"]) ? key.Value<float>("outTangent") : 0f);

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

            var binding = EditorCurveBinding.FloatCurve(path ?? string.Empty, componentType, property);
            Undo.RecordObject(animation, $"Set curve {property}");
            AnimationUtility.SetEditorCurve(animation, binding, new AnimationCurve(frames));
            EditorUtility.SetDirty(animation);
            AssetDatabase.SaveAssetIfDirty(animation);
            return Response.Success($"Set a {frames.Length}-key curve on '{property}' in '{clipPath}'.", new { clip = clipPath, property, keys = frames.Length });
        }
#endif

        // ---- Screenshot, search ----

        [Description("Capture the Scene or Game view as a PNG. output is a path inside the project; without it the image is returned inline.")]
        [ToolProvider("Screenshot")]
        public static string Screenshot(
            [ToolParam("'game' (default) or 'scene'", Required = false)] string view = "game",
            [ToolParam("Width in pixels; 0 uses the view's", Required = false)] int width = 0,
            [ToolParam("Height in pixels; 0 uses the view's", Required = false)] int height = 0,
            [ToolParam("Output PNG path", Required = false)] string output = "")
        {
            var save = !string.IsNullOrWhiteSpace(output);
            return string.Equals(view, "scene", StringComparison.OrdinalIgnoreCase)
                ? ScreenshotFunctions.CaptureSceneView(width, height, save, save ? output : null)
                : ScreenshotFunctions.CaptureGameView(width, height, save, save ? output : null);
        }

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

        // ---- Packages ----

        [Description("Add a package by name@version, git URL or 'file:' path. Returns at once (poll package_status) unless wait=true. Needs " +
                     "confirm=true; dry_run previews. A recompile or domain reload follows.")]
        [ToolProvider("Package")]
        public static async Task<object> PackageAdd(
            [ToolParam("'com.unity.foo@1.2.3', a git URL or 'file:../Path'")] string identifier,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false,
            [ToolParam("Block until it finishes", Required = false)] bool wait = false)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                return Response.Error("INVALID_PARAM", new { param = "identifier" });

            var plan = new { identifier, remoteSource = PackageFunctions.IsRemoteSource(identifier) };
            if (dry_run) return Response.Success("Would add the package; nothing changed.", plan);
            if (!confirm) return Response.Error("CONFIRM_REQUIRED", plan, "Adding a package edits manifest.json. Pass confirm=true to proceed, or dry_run=true to preview.");

            if (wait)
                return Wrapped(await PackageFunctions.InstallPackage(identifier.Trim(), true));

            var request = Client.Add(identifier.Trim());
            PackageFunctions.Track(request, $"installation of '{identifier.Trim()}'");
            return Response.Success("Started adding the package. Poll package_status.", new { status = "in_progress", identifier });
        }

        [Description("Remove a package by name. Returns at once (poll package_status) unless wait=true. Needs confirm=true; dry_run previews. " +
                     "A recompile or domain reload follows.")]
        [ToolProvider("Package")]
        public static async Task<object> PackageRemove(
            [ToolParam("Package name, e.g. com.unity.foo")] string name,
            [ToolParam("Apply the change", Required = false)] bool confirm = false,
            [ToolParam("Preview without applying", Required = false)] bool dry_run = false,
            [ToolParam("Block until it finishes", Required = false)] bool wait = false)
        {
            var package = (name ?? string.Empty).Trim().Split('@')[0];
            if (package.Length == 0)
                return Response.Error("INVALID_PARAM", new { param = "name" });

            if (string.Equals(package, "com.kitwright.unity.mcp", StringComparison.OrdinalIgnoreCase))
                return Response.Error("PACKAGE_PROTECTED", new { name }, "That package runs this server; removing it ends the connection mid-call.");

            if (dry_run) return Response.Success("Would remove the package; nothing changed.", new { name = package });
            if (!confirm) return Response.Error("CONFIRM_REQUIRED", new { name = package }, "Removing a package edits manifest.json. Pass confirm=true to proceed, or dry_run=true to preview.");

            if (wait)
                return Wrapped(await PackageFunctions.RemovePackage(package));

            PackageFunctions.Track(Client.Remove(package), $"removal of '{package}'");
            return Response.Success("Started removing the package. Poll package_status.", new { status = "in_progress", name = package });
        }

        [Description("List packages: scope 'installed' (default), 'available' (the registry) or 'all'. include_indirect adds transitive dependencies; " +
                     "offline queries the local cache for available and all.")]
        [ReadOnlyTool]
        [ToolProvider("Package")]
        public static async Task<object> PackageList(
            [ToolParam("installed (default), available or all", Required = false)] string scope = "installed",
            [ToolParam("Include transitive dependencies. Default true.", Required = false)] bool include_indirect = true,
            [ToolParam("Use the local cache for available/all", Required = false)] bool offline = false)
        {
            var wanted = (scope ?? "installed").Trim().ToLowerInvariant();
            if (wanted != "installed" && wanted != "available" && wanted != "all")
                return Response.Error("INVALID_PARAM", new { param = "scope", provided = scope }, "scope is installed, available or all.");

            object installed = null;
            if (wanted != "available")
            {
                var request = Client.List(offline, include_indirect);
                for (var i = 0; i < 240 && !request.IsCompleted; i++)
                    await Task.Delay(250);

                if (!request.IsCompleted) return Response.Error("PACKAGE_REQUEST_TIMEOUT");
                if (request.Status == StatusCode.Failure) return Response.Error("PACKAGE_REQUEST_FAILED", new { message = request.Error?.message });

                installed = request.Result.Select(p => new { name = p.name, version = p.version, source = p.source.ToString() }).ToArray();
            }

            object available = null;
            if (wanted != "installed")
            {
                var searched = await PackageFunctions.PackageSearch(string.Empty, offline);
                available = JObject.FromObject(searched)["data"];
            }

            return Response.Success($"Listed packages ({wanted}).", new { installed, available });
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

        // The package tools answer a JSON error object, or plain text once the request finished.
        private static object Wrapped(string text) =>
            text != null && text.TrimStart().StartsWith("{") ? (object)JObject.Parse(text) : Response.Success(text);

        private static bool IsNumber(JToken token) =>
            token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float);

        private static float Component(JArray array, int index) => index < array.Count ? array[index].Value<float>() : 0f;

        private static bool TryColor(JToken token, out Color color)
        {
            color = default;
            if (token is JArray array && array.Count >= 3 && array.Count <= 4 && array.All(IsNumber))
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
}
