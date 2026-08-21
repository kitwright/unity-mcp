// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("AssemblyDefinition")]
    internal static class AssemblyDefinitionFunctions
    {
        [Description("Create a new Assembly Definition (.asmdef) file. name defaults to the file name. references accept assembly names (auto-resolved to GUID) or 'GUID:xxx'.")]
        public static object CreateAssemblyDef(
            [ToolParam("Asset path for the .asmdef, e.g. 'Assets/Scripts/Runtime/MyGame.Runtime.asmdef'")] string path,
            [ToolParam("Assembly name (defaults to file name)", Required = false)] string name = null,
            [ToolParam("Root namespace", Required = false)] string root_namespace = null,
            [ToolParam("Comma-separated assembly references (names or 'GUID:xxx')", Required = false)] string references = null,
            [ToolParam("Comma-separated include platforms (e.g. 'Editor')", Required = false)] string include_platforms = null,
            [ToolParam("Comma-separated exclude platforms", Required = false)] string exclude_platforms = null,
            [ToolParam("Allow unsafe code", Required = false)] bool allow_unsafe_code = false,
            [ToolParam("Auto-referenced by predefined assemblies", Required = false)] bool auto_referenced = true,
            [ToolParam("Do not reference UnityEngine (pure C# library)", Required = false)] bool no_engine_references = false)
        {
            if (!IsUnderAssets(path)) return Response.Error("INVALID_PATH", new { path, hint = "path must be under Assets/" });
            path = path.Replace('\\', '/');
            if (!path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) path += ".asmdef";
            if (File.Exists(path)) return Response.Error("ASMDEF_EXISTS", new { path });

            var asmName = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(path) : name;

            var obj = new JObject { ["name"] = asmName };
            if (!string.IsNullOrEmpty(root_namespace)) obj["rootNamespace"] = root_namespace;
            obj["references"] = new JArray(ResolveReferences(SplitCsv(references)));
            obj["includePlatforms"] = new JArray(SplitCsv(include_platforms));
            obj["excludePlatforms"] = new JArray(SplitCsv(exclude_platforms));
            obj["allowUnsafeCode"] = allow_unsafe_code;
            obj["autoReferenced"] = auto_referenced;
            obj["noEngineReferences"] = no_engine_references;
            obj["overrideReferences"] = false;
            obj["precompiledReferences"] = new JArray();
            obj["defineConstraints"] = new JArray();
            obj["versionDefines"] = new JArray();

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            AtomicFile.WriteAllText(path, obj.ToString(Formatting.Indented));
            AssetDatabase.ImportAsset(path);

            return Response.Success($"Assembly definition '{asmName}' created at '{path}'.", new { path, name = asmName });
        }

        [Description("Get detailed info about an .asmdef file: name, references, platforms, and flags.")]
        [ReadOnlyTool]
        public static object GetAssemblyDefInfo(
            [ToolParam("Asset path of the .asmdef file")] string path)
        {
            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;
            return Response.Success($"Assembly definition '{obj["name"]}'.", new
            {
                path,
                name = (string)obj["name"],
                rootNamespace = (string)obj["rootNamespace"],
                references = obj["references"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                includePlatforms = obj["includePlatforms"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                excludePlatforms = obj["excludePlatforms"]?.ToObject<string[]>() ?? Array.Empty<string>(),
                allowUnsafeCode = (bool?)obj["allowUnsafeCode"] ?? false,
                autoReferenced = (bool?)obj["autoReferenced"] ?? true,
                noEngineReferences = (bool?)obj["noEngineReferences"] ?? false,
                defineConstraints = obj["defineConstraints"]?.ToObject<string[]>() ?? Array.Empty<string>()
            });
        }

        [Description("List all Assembly Definition files, optionally scoped to a folder and including Packages/.")]
        [ReadOnlyTool]
        public static object ListAssemblyDefs(
            [ToolParam("Folder to search in (default 'Assets')", Required = false)] string folder = "Assets",
            [ToolParam("Also list asmdefs under Packages/", Required = false)] bool include_packages = false)
        {
            var folders = new List<string> { string.IsNullOrEmpty(folder) ? "Assets" : folder.Replace('\\', '/').TrimEnd('/') };
            if (include_packages) folders.Add("Packages");

            var guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", folders.ToArray());
            var list = guids.Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p).Select(p =>
            {
                var obj = TryParse(p);
                return new
                {
                    path = p,
                    name = (string)obj?["name"],
                    referenceCount = (obj?["references"] as JArray)?.Count ?? 0,
                    editorOnly = (obj?["includePlatforms"] as JArray)?.Any(t => (string)t == "Editor") ?? false
                };
            }).ToList();

            return Response.Success($"Found {list.Count} assembly definition(s).", new { count = list.Count, assemblies = list });
        }

        [Description("Add assembly references to an existing .asmdef. Names are auto-resolved to GUID form; existing refs are kept.")]
        public static object AddAssemblyReferences(
            [ToolParam("Asset path of the .asmdef file")] string path,
            [ToolParam("Comma-separated assembly names or 'GUID:xxx' to add")] string references)
        {
            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;

            var current = (obj["references"] as JArray) ?? new JArray();
            var existing = current.Select(t => (string)t).ToHashSet();
            var toAdd = ResolveReferences(SplitCsv(references)).Where(r => !existing.Contains(r)).ToList();
            foreach (var r in toAdd) current.Add(r);
            obj["references"] = current;

            Save(path, obj);
            return Response.Success($"Added {toAdd.Count} reference(s) to '{obj["name"]}'.", new { added = toAdd, referenceCount = current.Count });
        }

        [Description("Remove assembly references from an existing .asmdef. Accepts names (resolved to GUID) or 'GUID:xxx'.")]
        public static object RemoveAssemblyReferences(
            [ToolParam("Asset path of the .asmdef file")] string path,
            [ToolParam("Comma-separated assembly names or 'GUID:xxx' to remove")] string references)
        {
            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;

            var remove = ResolveReferences(SplitCsv(references)).ToHashSet();
            var current = (obj["references"] as JArray) ?? new JArray();
            var kept = new JArray(current.Where(t => !remove.Contains((string)t)));
            int removed = current.Count - kept.Count;
            obj["references"] = kept;

            Save(path, obj);
            return Response.Success($"Removed {removed} reference(s) from '{obj["name"]}'.", new { removed, referenceCount = kept.Count });
        }

        [Description("Set include/exclude platform lists on an .asmdef. Setting include_platforms clears exclude_platforms and vice versa.")]
        public static object SetAssemblyPlatforms(
            [ToolParam("Asset path of the .asmdef file")] string path,
            [ToolParam("Comma-separated include platforms (e.g. 'Editor'). Empty to clear.", Required = false)] string include_platforms = null,
            [ToolParam("Comma-separated exclude platforms. Empty to clear.", Required = false)] string exclude_platforms = null)
        {
            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;

            if (include_platforms != null)
            {
                obj["includePlatforms"] = new JArray(SplitCsv(include_platforms));
                if (((JArray)obj["includePlatforms"]).Count > 0) obj["excludePlatforms"] = new JArray();
            }
            if (exclude_platforms != null)
            {
                obj["excludePlatforms"] = new JArray(SplitCsv(exclude_platforms));
                if (((JArray)obj["excludePlatforms"]).Count > 0) obj["includePlatforms"] = new JArray();
            }

            Save(path, obj);
            return Response.Success($"Platforms updated for '{obj["name"]}'.", new
            {
                includePlatforms = obj["includePlatforms"].ToObject<string[]>(),
                excludePlatforms = obj["excludePlatforms"].ToObject<string[]>()
            });
        }

        [Description("Update settings on an .asmdef: name, rootNamespace, allowUnsafeCode, autoReferenced, noEngineReferences, defineConstraints. Only supplied fields change.")]
        public static object UpdateAssemblyDefSettings(
            [ToolParam("Asset path of the .asmdef file")] string path,
            [ToolParam("New assembly name", Required = false)] string name = null,
            [ToolParam("New root namespace", Required = false)] string root_namespace = null,
            [ToolParam("Allow unsafe code", Required = false)] bool? allow_unsafe_code = null,
            [ToolParam("Auto-referenced by predefined assemblies", Required = false)] bool? auto_referenced = null,
            [ToolParam("No UnityEngine references", Required = false)] bool? no_engine_references = null,
            [ToolParam("Comma-separated define constraints (symbols required to compile)", Required = false)] string define_constraints = null)
        {
            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;

            if (!string.IsNullOrEmpty(name)) obj["name"] = name;
            if (root_namespace != null) obj["rootNamespace"] = root_namespace;
            if (allow_unsafe_code.HasValue) obj["allowUnsafeCode"] = allow_unsafe_code.Value;
            if (auto_referenced.HasValue) obj["autoReferenced"] = auto_referenced.Value;
            if (no_engine_references.HasValue) obj["noEngineReferences"] = no_engine_references.Value;
            if (define_constraints != null) obj["defineConstraints"] = new JArray(SplitCsv(define_constraints));

            Save(path, obj);
            return Response.Success($"Settings updated for '{obj["name"]}'.");
        }

        // Resolve each token to the asmdef reference form. 'GUID:xxx' passes through; a name is
        // resolved to 'GUID:<guid>' when the assembly exists, else kept as a plain name (still valid).
        internal static IEnumerable<string> ResolveReferences(IEnumerable<string> tokens)
        {
            foreach (var token in tokens)
            {
                if (token.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase)) { yield return token; continue; }
                var asmPath = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(token);
                if (!string.IsNullOrEmpty(asmPath))
                {
                    var guid = AssetDatabase.AssetPathToGUID(asmPath);
                    yield return string.IsNullOrEmpty(guid) ? token : $"GUID:{guid}";
                }
                else yield return token;
            }
        }

        internal static string[] SplitCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            return csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        private static JObject LoadAsmdef(string path, out object error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            {
                error = Response.Error("ASMDEF_NOT_FOUND", new { path });
                return null;
            }
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch (Exception e) { error = Response.Error("ASMDEF_PARSE_FAILED", new { path, message = e.Message }); return null; }
        }

        private static JObject TryParse(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch { return null; }
        }

        private static void Save(string path, JObject obj)
        {
            AtomicFile.WriteAllText(path, obj.ToString(Formatting.Indented));
            AssetDatabase.ImportAsset(path);
        }

        private static bool IsUnderAssets(string path)
        {
            return !string.IsNullOrEmpty(path) && path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
