// Copyright (C) KitWright. All rights reserved.
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("File")]
    internal static class FileFunctions
    {
        private const int MaxReadChars = 10000;

        [Description("Read the contents of a file. Returns the content plus the sha256 of the file as it was read; " +
                     "pass that sha256 back to write_file or edit_script so a rewrite is rejected if someone changed " +
                     "the file in between. A file longer than the read cap comes back truncated AND WITHOUT a sha256, " +
                     "because rewriting a whole file from a truncated copy would delete the tail — patch it with " +
                     "patch_script or edit_script_members instead.")]
        [ReadOnlyTool]
        public static object ReadFile(
            [ToolParam("Path to the file, inside the project (relative to project root, or absolute); a path outside it is refused")] string path)
        {
            var fullPath = PathSafety.ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return Response.Error("FILE_NOT_FOUND", new { path });

            var content = File.ReadAllText(fullPath);
            var truncated = content.Length > MaxReadChars;

            if (!truncated)
            {
                return Response.Success($"Read {path}.", new
                {
                    path,
                    length = content.Length,
                    sha256 = CodeFunctions.ComputeSha256(content),
                    content
                });
            }

            return Response.Success($"Read {path} (truncated).", new
            {
                path,
                length = content.Length,
                truncated = true,
                content = content.Substring(0, MaxReadChars)
            },
            $"Only the first {MaxReadChars} of {content.Length} chars are shown, so no sha256 is issued and " +
            "write_file/edit_script will refuse a whole-file rewrite. Use patch_script or edit_script_members " +
            "to change part of it.");
        }

        [Description("Write content to a file, creating it or overwriting it whole. " +
                     "Overwriting an existing file requires expected_sha256 (from read_file) so a concurrent edit " +
                     "is not silently discarded; creating a new file does not, since there is nothing to lose.")]
        public static string WriteFile(
            [ToolParam("Path to the file, inside the project; a path outside it is refused")] string path,
            [ToolParam("Content to write")] string content,
            [ToolParam("SHA256 from read_file. Required when the file already exists; the write is rejected with STALE_FILE if it changed since.", Required = false)]
            string expected_sha256 = null)
        {
            var fullPath = PathSafety.ResolveProjectPath(path);
            var exists = File.Exists(fullPath);

            if (exists)
            {
                if (string.IsNullOrEmpty(expected_sha256))
                    return ToolResultFormatter.Error("SHA_REQUIRED", new { path },
                        "This file already exists and write_file replaces it whole. Call read_file and pass its " +
                        "sha256 as expected_sha256, so an edit made since you read it is not discarded.");

                var staleError = CodeFunctions.CheckPrecondition(path, File.ReadAllText(fullPath), expected_sha256);
                if (staleError != null) return staleError;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            AtomicFile.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();
            return $"Written {content.Length} chars to {path} (sha256: {CodeFunctions.ComputeSha256(content)})";
        }

        [Description("Search for files by name pattern in the project. A page cut short by max reports a " +
                     "next_cursor to pass back as cursor for the rest.")]
        [ReadOnlyTool]
        public static string SearchFiles(
            [ToolParam("Search pattern (e.g. '*.cs', 'Player*', '*.prefab')")] string pattern,
            [ToolParam("Directory to search in, inside the project; a directory outside it is refused", Required = false)] string directory = "Assets",
            [ToolParam("Maximum paths to return (1-1000). Default 100.", Required = false)] int max = 100,
            [ToolParam(Paging.CursorParam, Required = false)] int cursor = 0)
        {
            var fullPath = PathSafety.ResolveProjectPath(directory);
            if (!Directory.Exists(fullPath))
                return ToolResultFormatter.Error("DIRECTORY_NOT_FOUND", new { directory });

            var files = Directory.GetFiles(fullPath, pattern, SearchOption.AllDirectories);
            if (files.Length == 0)
                return $"No files matching '{pattern}' in {directory}";

            System.Array.Sort(files, System.StringComparer.Ordinal);
            var page = Paging.Page(files, cursor, UnityEngine.Mathf.Clamp(max, 1, 1000));

            var results = new List<string>();
            foreach (var file in page)
            {
                var relative = file.Replace(Path.GetDirectoryName(UnityEngine.Application.dataPath) + "/", "")
                    .Replace('\\', '/');
                results.Add($"  - {relative}");
            }

            return $"Found {files.Length} files.{Paging.Suffix(cursor, page.Count, files.Length)}\n" +
                   string.Join("\n", results);
        }

        [Description("List files and directories directly inside a directory (top level only; use search_files to recurse into subdirectories). " +
                     "The file list pages: a page cut short by max reports a next_cursor to pass back as cursor.")]
        [ReadOnlyTool]
        public static string ListDirectory(
            [ToolParam("Path to directory, inside the project; a path outside it is refused")] string path,
            [ToolParam("Maximum files to return (1-1000). Default 200. Subdirectories are always listed in full.", Required = false)] int max = 200,
            [ToolParam(Paging.CursorParam, Required = false)] int cursor = 0)
        {
            var fullPath = PathSafety.ResolveProjectPath(path);
            if (!Directory.Exists(fullPath))
                return ToolResultFormatter.Error("DIRECTORY_NOT_FOUND", new { path });

            var dirs = Directory.GetDirectories(fullPath);
            var names = new List<string>();
            foreach (var file in Directory.GetFiles(fullPath))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith(".")) continue;
                if (name.EndsWith(".meta")) continue;
                names.Add(name);
            }
            names.Sort(System.StringComparer.Ordinal);
            var page = Paging.Page(names, cursor, UnityEngine.Mathf.Clamp(max, 1, 1000));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Contents of {path}:{Paging.Suffix(cursor, page.Count, names.Count)}");
            foreach (var dir in dirs)
                sb.AppendLine($"  [DIR] {Path.GetFileName(dir)}/");
            foreach (var name in page)
                sb.AppendLine($"  {name}");

            return sb.ToString();
        }

        [Description("Check if a file or directory exists")]
        [ReadOnlyTool]
        public static string Exists(
            [ToolParam("Path to check, inside the project; a path outside it is reported as not existing")] string path)
        {
            string fullPath;
            try { fullPath = PathSafety.ResolveProjectPath(path); }
            catch (System.ArgumentException) { return "Does not exist (outside the project)"; }

            bool fileExists = File.Exists(fullPath);
            bool dirExists = Directory.Exists(fullPath);
            return fileExists ? "File exists" :
                   dirExists ? "Directory exists" :
                   "Does not exist";
        }

    }
}
