// Copyright (C) KitWright. All rights reserved.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Services;
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
        [Description("Create a new Assembly Definition (.asmdef) file. name defaults to the file name and must not be used by another assembly definition. " +
                     "references accept assembly names (auto-resolved to GUID) or 'GUID:xxx'; a name no assembly definition carries is kept as written and listed in unresolvedReferences. " +
                     "Give include_platforms or exclude_platforms, not both.")]
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

            string fullPath;
            try { fullPath = PathSafety.ResolveAssetPath(path); }
            catch (PathOutsideProjectException ex)
            { return Response.Error("INVALID_PATH", new { path, message = ex.Message }); }

            if (File.Exists(fullPath)) return Response.Error("ASMDEF_EXISTS", new { path });

            var asmName = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(path) : name;

            var includes = SplitCsv(include_platforms);
            var excludes = SplitCsv(exclude_platforms);
            var bothLists = BothPlatformListsRefusal(include_platforms, exclude_platforms);
            if (bothLists != null) return bothLists;

            var nameTaken = NameTakenRefusal(asmName, null);
            if (nameTaken != null) return nameTaken;

            var referenceTokens = SplitCsv(references);
            var unresolved = UnresolvedReferences(referenceTokens);

            var obj = new JObject { ["name"] = asmName };
            if (!string.IsNullOrEmpty(root_namespace)) obj["rootNamespace"] = root_namespace;
            obj["references"] = new JArray(ResolveReferences(referenceTokens));
            obj["includePlatforms"] = new JArray(includes);
            obj["excludePlatforms"] = new JArray(excludes);
            obj["allowUnsafeCode"] = allow_unsafe_code;
            obj["autoReferenced"] = auto_referenced;
            obj["noEngineReferences"] = no_engine_references;
            obj["overrideReferences"] = false;
            obj["precompiledReferences"] = new JArray();
            obj["defineConstraints"] = new JArray();
            obj["versionDefines"] = new JArray();

            var dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Save(path, obj);

            return Response.Success($"Assembly definition '{asmName}' created at '{path}'." + UnresolvedNote(unresolved),
                new { path, name = asmName, unresolvedReferences = unresolved });
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

        [Description("List all Assembly Definition files, optionally scoped to a folder and including Packages/. " +
                     "editable is false for an asmdef in a package that is not embedded in the project (a file: or registry package), which the other assembly definition tools cannot read or change.")]
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
                    editorOnly = (obj?["includePlatforms"] as JArray)?.Any(t => (string)t == "Editor") ?? false,
                    editable = IsEditableLocation(p, PackageSourceOf(p))
                };
            }).ToList();

            return Response.Success($"Found {list.Count} assembly definition(s).", new { count = list.Count, assemblies = list });
        }

        [Description("Add assembly references to an existing .asmdef. Names are auto-resolved to GUID form; existing refs are kept, and one already listed (as a name or as its GUID) is not added twice. " +
                     "A name no assembly definition carries is added as written and listed in unresolvedReferences.")]
        public static object AddAssemblyReferences(
            [ToolParam("Asset path of the .asmdef file")] string path,
            [ToolParam("Comma-separated assembly names or 'GUID:xxx' to add")] string references)
        {
            var tokens = SplitCsv(references);
            if (tokens.Length == 0)
                return Response.Error("NO_PARAMS", new { hint = "references is empty: list at least one assembly name or 'GUID:xxx' to add." });

            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;

            var current = (obj["references"] as JArray) ?? new JArray();
            var toAdd = NewReferences(current, tokens, Canonical);
            var unresolved = UnresolvedReferences(tokens);
            if (toAdd.Count == 0)
                return Response.Success($"'{obj["name"]}' already references all of them; nothing added." + UnresolvedNote(unresolved),
                    new { added = toAdd, referenceCount = current.Count, unresolvedReferences = unresolved });

            foreach (var r in toAdd) current.Add(r);
            obj["references"] = current;

            Save(path, obj);
            return Response.Success($"Added {toAdd.Count} reference(s) to '{obj["name"]}'." + UnresolvedNote(unresolved),
                new { added = toAdd, referenceCount = current.Count, unresolvedReferences = unresolved });
        }

        [Description("Remove assembly references from an existing .asmdef. Accepts names or 'GUID:xxx'; an entry written as a name matches its GUID and the other way round. " +
                     "notFound lists what the file did not reference.")]
        public static object RemoveAssemblyReferences(
            [ToolParam("Asset path of the .asmdef file")] string path,
            [ToolParam("Comma-separated assembly names or 'GUID:xxx' to remove")] string references)
        {
            var tokens = SplitCsv(references);
            if (tokens.Length == 0)
                return Response.Error("NO_PARAMS", new { hint = "references is empty: list at least one assembly name or 'GUID:xxx' to remove." });

            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;

            var current = (obj["references"] as JArray) ?? new JArray();
            var kept = WithoutReferences(current, tokens, Canonical, out var notFound);
            int removed = current.Count - kept.Count;
            if (removed > 0)
            {
                obj["references"] = kept;
                Save(path, obj);
            }

            return Response.Success(
                $"Removed {removed} reference(s) from '{obj["name"]}'." +
                (notFound.Count > 0 ? $" Not referenced: {string.Join(", ", notFound)}." : string.Empty),
                new { removed, notFound, referenceCount = kept.Count });
        }

        [Description("Set include/exclude platform lists on an .asmdef. A non-empty include_platforms clears exclude_platforms and vice versa; both non-empty in one call is refused.")]
        public static object SetAssemblyPlatforms(
            [ToolParam("Asset path of the .asmdef file")] string path,
            [ToolParam("Comma-separated include platforms (e.g. 'Editor'). Empty to clear.", Required = false)] string include_platforms = null,
            [ToolParam("Comma-separated exclude platforms. Empty to clear.", Required = false)] string exclude_platforms = null)
        {
            var obj = LoadAsmdef(path, out var error);
            if (obj == null) return error;

            var bothLists = BothPlatformListsRefusal(include_platforms, exclude_platforms);
            if (bothLists != null) return bothLists;

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

        [Description("Update settings on an .asmdef: name, rootNamespace, allowUnsafeCode, autoReferenced, noEngineReferences, defineConstraints. Only supplied fields change; a name another assembly definition already uses is refused.")]
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

            if (string.IsNullOrEmpty(name) && root_namespace == null && !allow_unsafe_code.HasValue &&
                !auto_referenced.HasValue && !no_engine_references.HasValue && define_constraints == null)
                return Response.Error("NO_PARAMS",
                    new { hint = "Provide at least one of name, root_namespace, allow_unsafe_code, auto_referenced, no_engine_references, define_constraints." });

            if (!string.IsNullOrEmpty(name))
            {
                var nameTaken = NameTakenRefusal(name, PathSafety.ResolveProjectPath(path));
                if (nameTaken != null) return nameTaken;
            }

            var changed = new List<string>();
            void Apply(string key, JToken value)
            {
                if (JToken.DeepEquals(obj[key], value)) return;
                obj[key] = value;
                changed.Add(key);
            }

            if (!string.IsNullOrEmpty(name)) Apply("name", name);
            if (root_namespace != null) Apply("rootNamespace", root_namespace);
            if (allow_unsafe_code.HasValue) Apply("allowUnsafeCode", allow_unsafe_code.Value);
            if (auto_referenced.HasValue) Apply("autoReferenced", auto_referenced.Value);
            if (no_engine_references.HasValue) Apply("noEngineReferences", no_engine_references.Value);
            if (define_constraints != null) Apply("defineConstraints", new JArray(SplitCsv(define_constraints)));

            if (changed.Count == 0)
                return Response.Success($"No change to '{obj["name"]}': those values are already set.", new { changed });

            Save(path, obj);
            return Response.Success($"Settings updated for '{obj["name"]}': {string.Join(", ", changed)}.", new { changed });
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

        // One form per reference, so a name and the GUID of the same assembly definition compare equal.
        internal static string Canonical(string reference) =>
            string.IsNullOrEmpty(reference) ? reference : ResolveReferences(new[] { reference }).First();

        internal static List<string> UnresolvedReferences(IEnumerable<string> tokens) =>
            tokens.Where(t => !t.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase) &&
                              string.IsNullOrEmpty(CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(t))).ToList();

        private static string UnresolvedNote(List<string> unresolved) =>
            unresolved.Count == 0
                ? string.Empty
                : $" No assembly definition named {string.Join(", ", unresolved)}: written as given, and Unity reports a missing reference until one exists.";

        internal static List<string> NewReferences(JArray current, IEnumerable<string> tokens, Func<string, string> canonical)
        {
            var seen = new HashSet<string>(current.Select(t => canonical((string)t)), StringComparer.OrdinalIgnoreCase);
            return tokens.Select(canonical).Where(seen.Add).ToList();
        }

        internal static JArray WithoutReferences(JArray current, IEnumerable<string> tokens, Func<string, string> canonical, out List<string> notFound)
        {
            var asked = tokens.ToList();
            var drop = new HashSet<string>(asked.Select(canonical), StringComparer.OrdinalIgnoreCase);
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kept = new JArray();
            foreach (var entry in current)
            {
                var key = canonical((string)entry);
                if (key != null && drop.Contains(key)) matched.Add(key);
                else kept.Add(entry);
            }

            notFound = asked.Where(t => !matched.Contains(canonical(t))).ToList();
            return kept;
        }

        private static object BothPlatformListsRefusal(string includePlatforms, string excludePlatforms)
        {
            if (SplitCsv(includePlatforms).Length == 0 || SplitCsv(excludePlatforms).Length == 0) return null;
            return Response.Error("INVALID_PARAM", new { include_platforms = includePlatforms, exclude_platforms = excludePlatforms },
                "An assembly definition lists the platforms it includes or the ones it excludes, never both: Unity refuses the file. Pass one of them.");
        }

        // ownFullPath is the file being edited: it already carries the name, so it is not a second definition of it.
        internal static object NameTakenRefusal(string asmName, string ownFullPath)
        {
            var existing = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(asmName);
            if (string.IsNullOrEmpty(existing)) return null;
            if (ownFullPath != null &&
                string.Equals(Path.GetFullPath(Path.Combine(ApplicationPaths.ProjectRoot, existing)), ownFullPath, StringComparison.OrdinalIgnoreCase))
                return null;
            return Response.Error("ASMDEF_NAME_EXISTS", new { name = asmName, existingPath = existing },
                "Assembly names are unique in a project, and Unity reports an error for a second definition with the same name. Pass another name.");
        }

        internal static string[] SplitCsv(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) return Array.Empty<string>();
            return csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        }

        private static JObject LoadAsmdef(string path, out object error)
        {
            error = null;
            string full = null;
            // The project root rather than Assets/: the asmdef of an embedded package under Packages/ lives inside
            // the project, so it can be edited. A file: or registry package lives outside it and PathSafety refuses it.
            if (!string.IsNullOrEmpty(path))
            {
                try { full = PathSafety.ResolveProjectPath(path); }
                catch (PathOutsideProjectException ex)
                {
                    error = Response.Error("INVALID_PATH", new { path, message = ex.Message }, NotEmbeddedHint(path, PackageSourceOf(path)));
                    return null;
                }
            }
            if (full == null || !path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            {
                error = Response.Error("ASMDEF_NOT_FOUND", new { path }, NotEmbeddedHint(path, PackageSourceOf(path)));
                return null;
            }
            try { return JsonParse.Object(File.ReadAllText(full)); }
            catch (Exception e) { error = Response.Error("ASMDEF_PARSE_FAILED", new { path, message = e.Message }); return null; }
        }

        private static bool IsPackagePath(string path) =>
            path != null && path.Replace('\\', '/').StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);

        private static UnityEditor.PackageManager.PackageSource? PackageSourceOf(string path) =>
            IsPackagePath(path) ? UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path.Replace('\\', '/'))?.source : null;

        // Only an embedded package's files sit inside the project; a file: (Local) or registry package resolves outside it.
        internal static bool IsEditableLocation(string path, UnityEditor.PackageManager.PackageSource? source) =>
            !IsPackagePath(path) || source == UnityEditor.PackageManager.PackageSource.Embedded;

        internal static string NotEmbeddedHint(string path, UnityEditor.PackageManager.PackageSource? source) =>
            source == null || IsEditableLocation(path, source)
                ? null
                : $"'{path}' is in a package that is not embedded in the project (a file: or registry package lives outside it), " +
                  "so this tool can neither read nor edit its asmdef. Embed the package under Packages/, or work on the files at the package's own location.";

        private static JObject TryParse(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path)); }
            catch { return null; }
        }

        private static void Save(string path, JObject obj)
        {
            var full = PathSafety.ResolveProjectPath(path);
            AtomicFile.WriteAllText(full, Serialize(obj, File.Exists(full) ? File.ReadAllText(full) : null));
            AssetDatabase.ImportAsset(full.Substring(Path.GetFullPath(ApplicationPaths.ProjectRoot).Length).TrimStart('\\', '/').Replace('\\', '/'));
        }

        // Unity writes asmdef files with 4-space indents; Json.NET's own default is 2 spaces and the platform's
        // newline, which rewrote every line of a file whose content had barely changed.
        internal static string Serialize(JObject obj, string existing)
        {
            var newline = existing == null || !existing.Contains("\n") ? Environment.NewLine : existing.Contains("\r\n") ? "\r\n" : "\n";
            var indent = DetectIndent(existing);
            var text = new StringBuilder();
            using (var writer = new StringWriter(text) { NewLine = newline })
            using (var json = new JsonTextWriter(writer) { Formatting = Formatting.Indented, IndentChar = indent[0], Indentation = indent.Length })
                obj.WriteTo(json);

            if (existing != null && existing.EndsWith("\n", StringComparison.Ordinal))
                text.Append(newline);
            return text.ToString();
        }

        internal static string DetectIndent(string existing)
        {
            var match = existing == null ? null : Regex.Match(existing, @"^([ \t]+)\S", RegexOptions.Multiline);
            return match != null && match.Success ? match.Groups[1].Value : "    ";
        }

        private static bool IsUnderAssets(string path)
        {
            return !string.IsNullOrEmpty(path) && path.Replace('\\', '/').StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
        }
    }
}
