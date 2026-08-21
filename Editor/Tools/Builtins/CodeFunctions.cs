// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KitWright.Editor.Tools.Helpers;
using KitWright.Editor.Tools.Scripting;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Code")]
    internal static class CodeFunctions
    {
        [Description("Create a new C# script with the specified content")]
        public static string CreateScript(
            [ToolParam("Script file name (without .cs)")]
            string name,
            [ToolParam("C# source code content")] string content,
            [ToolParam("Path to save (e.g. 'Assets/Scripts/')", Required = false)]
            string save_path = "Assets/Scripts/")
        {
            var assetPath = Path.Combine(save_path, $"{name}.cs");
            var fullPath = PathSafety.ResolveProjectPath(assetPath);

            if (File.Exists(fullPath))
                return ToolResultFormatter.Error("SCRIPT_EXISTS", new { path = assetPath },
                    "create_script never overwrites: a file is already there and you may not have read it. " +
                    "Use edit_script (with expected_sha256) to replace it, or patch_script for a smaller change.");

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            AtomicFile.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return $"Created script '{name}.cs' at {assetPath}";
        }

        [Description("Get the SHA256 hash of a script file's current contents. " +
                     "Pass the returned sha256 as expected_sha256 to edit_script/patch_script so the edit is rejected " +
                     "if the file changed since you read it (prevents overwriting concurrent edits).")]
        [ReadOnlyTool]
        public static object GetScriptSha(
            [ToolParam("Path to the script file")] string path)
        {
            var fullPath = PathSafety.ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return Response.Error("SCRIPT_NOT_FOUND", new { path });

            var content = File.ReadAllText(fullPath);
            return Response.Success($"SHA256 for {path}.", new
            {
                path,
                sha256 = ComputeSha256(content),
                length = content.Length
            });
        }

        [Description("Replace the entire contents of an existing script. " +
                     "expected_sha256 is required: this is the one edit that overwrites everything, so it must prove " +
                     "the file has not changed since you read it. read_file returns the sha to pass here — and returns " +
                     "none when it had to truncate, which is exactly when a whole-file rewrite would destroy the tail. " +
                     "For a smaller change prefer patch_script or edit_script_members, which need no sha because they " +
                     "match on what they replace.")]
        public static string EditScript(
            [ToolParam("Path to the script file")] string path,
            [ToolParam("New full content for the script")]
            string content,
            [ToolParam("SHA256 of the content you read, from read_file or get_script_sha. Required; the write is rejected with STALE_FILE if the file changed since.")]
            string expected_sha256)
        {
            if (string.IsNullOrEmpty(expected_sha256))
                return ToolResultFormatter.Error("SHA_REQUIRED", new { path },
                    "edit_script overwrites the whole file, so it needs the sha256 of the content you based the " +
                    "rewrite on. Call read_file (or get_script_sha) and pass its sha256. If read_file reported the " +
                    "file as truncated it returns no sha - use patch_script or edit_script_members instead.");

            var fullPath = PathSafety.ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return ToolResultFormatter.Error("SCRIPT_NOT_FOUND", new { path });

            var original = File.ReadAllText(fullPath);
            var staleError = CheckPrecondition(path, original, expected_sha256);
            if (staleError != null) return staleError;

            var syntaxError = CheckSyntaxRegression(path, original, content);
            if (syntaxError != null) return syntaxError;

            AtomicFile.WriteAllText(fullPath, content);
            AssetDatabase.Refresh();

            return $"Updated script at {path} (sha256: {ComputeSha256(content)})";
        }

        [Description("Patch a script by finding and replacing specific text. " +
                     "Safer than edit_script for small changes since it doesn't require sending the entire file content. " +
                     "The old_text must match exactly (including whitespace and indentation). " +
                     "Optionally pass expected_sha256 (from get_script_sha) to reject the patch if the file changed since you read it.")]
        public static string PatchScript(
            [ToolParam("Path to the script file")] string path,
            [ToolParam("Exact text to find in the file")] string old_text,
            [ToolParam("Replacement text")] string new_text,
            [ToolParam("Replace all occurrences (default: false, only first)", Required = false)]
            bool replace_all = false,
            [ToolParam("SHA256 from get_script_sha; patch is rejected with STALE_FILE if the file changed", Required = false)]
            string expected_sha256 = null)
        {
            var fullPath = PathSafety.ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return ToolResultFormatter.Error("SCRIPT_NOT_FOUND", new { path });

            var content = File.ReadAllText(fullPath);

            var staleError = CheckPrecondition(path, content, expected_sha256);
            if (staleError != null) return staleError;

            if (!content.Contains(old_text))
                return ToolResultFormatter.Error("PATCH_TEXT_NOT_FOUND",
                    new { path, hint = "Make sure old_text matches exactly, including whitespace and indentation." });

            int occurrences = 0;
            int index = 0;
            while ((index = content.IndexOf(old_text, index, StringComparison.Ordinal)) >= 0)
            {
                occurrences++;
                index += old_text.Length;
            }

            string newContent;
            if (replace_all)
            {
                newContent = content.Replace(old_text, new_text);
            }
            else
            {
                int firstIndex = content.IndexOf(old_text, StringComparison.Ordinal);
                newContent = content.Substring(0, firstIndex) +
                             new_text +
                             content.Substring(firstIndex + old_text.Length);
            }

            var syntaxError = CheckSyntaxRegression(path, content, newContent);
            if (syntaxError != null) return syntaxError;

            AtomicFile.WriteAllText(fullPath, newContent);
            AssetDatabase.Refresh();

            string replacedInfo = replace_all
                ? $"Replaced all {occurrences} occurrence(s)"
                : $"Replaced first occurrence (of {occurrences} total)";

            return $"Patched script at {path}. {replacedInfo}. (sha256: {ComputeSha256(newContent)})";
        }

        [Description("Edit a C# script one member at a time, addressing methods by name instead of by text. " +
                     "Prefer this over patch_script for method-sized changes: patch_script needs old_text to match the file byte for byte, " +
                     "so it breaks on indentation, a moved attribute or a comment you did not remember exactly, and a near-miss can overwrite " +
                     "part of the next member. Here every span is found by matching braces, so an edit stops at the member's own closing brace.\n" +
                     "Pass a JSON array of edits, applied in order and written only if all of them succeed:\n" +
                     "  {\"op\":\"replace_method\",\"class_name\":\"Player\",\"method_name\":\"Jump\",\"replacement\":\"public void Jump() { ... }\"}\n" +
                     "  {\"op\":\"insert_method\",\"class_name\":\"Player\",\"replacement\":\"void Land() { }\",\"position\":\"end\"}  // start | end | after | before\n" +
                     "  {\"op\":\"insert_method\",\"class_name\":\"Player\",\"replacement\":\"void Land() { }\",\"position\":\"after\",\"anchor_method\":\"Jump\"}\n" +
                     "  {\"op\":\"delete_method\",\"class_name\":\"Player\",\"method_name\":\"Land\"}\n" +
                     "The replacement is re-indented to the target's nesting, so write it flat. Attributes and the comment directly above a " +
                     "method count as part of it and are replaced with it. An overloaded name returns AMBIGUOUS_METHOD with the signatures, " +
                     "since a name alone cannot pick an overload — use patch_script for that. The result is structurally validated before it " +
                     "is written, and expected_sha256 rejects the write if the file changed since you read it.")]
        public static string EditScriptMembers(
            [ToolParam("Path to the script file")] string path,
            [ToolParam("JSON array of edits; see the description for the shape of each op")] string edits,
            [ToolParam("SHA256 from get_script_sha; the write is rejected with STALE_FILE if the file changed", Required = false)]
            string expected_sha256 = null)
        {
            var fullPath = PathSafety.ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return ToolResultFormatter.Error("SCRIPT_NOT_FOUND", new { path });

            List<CSharpMemberEditor.MemberEdit> parsed;
            try
            {
                parsed = ParseEdits(edits);
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Error("INVALID_EDITS", new { path, message = ex.Message });
            }

            if (parsed.Count == 0)
                return ToolResultFormatter.Error("INVALID_EDITS", new { path, message = "The edits array is empty." });

            var original = File.ReadAllText(fullPath);
            var staleError = CheckPrecondition(path, original, expected_sha256);
            if (staleError != null) return staleError;

            var outcome = CSharpMemberEditor.Apply(original, parsed);
            if (!outcome.Success)
                return ToolResultFormatter.Error(outcome.ErrorCode,
                    new { path, message = outcome.Message, candidates = outcome.Candidates });

            var syntaxError = CheckSyntaxRegression(path, original, outcome.Source);
            if (syntaxError != null) return syntaxError;

            AtomicFile.WriteAllText(fullPath, outcome.Source);
            AssetDatabase.Refresh();

            return $"Applied {parsed.Count} member edit(s) to {path} (sha256: {ComputeSha256(outcome.Source)})";
        }

        [Description("Check that a C# file is structurally sound — balanced braces, parentheses and brackets, no unterminated string " +
                     "or comment — reporting the line of the first problem. Pass content to check a proposed version before writing it, " +
                     "or omit it to check what is on disk. This is a structural check, not a compile: for real compiler diagnostics call " +
                     "request_recompile then get_compilation_errors, which is authoritative because Unity builds the whole assembly with " +
                     "its own define symbols and sibling files. Validating one file in isolation cannot do that — it misreports every " +
                     "partial class and every #if branch whose symbol it does not know.")]
        [ReadOnlyTool]
        public static string ValidateScript(
            [ToolParam("Path to the script file")] string path,
            [ToolParam("Content to check instead of the file on disk", Required = false)] string content = null)
        {
            string source;
            if (content != null)
            {
                source = content;
            }
            else
            {
                var fullPath = PathSafety.ResolveProjectPath(path);
                if (!File.Exists(fullPath))
                    return ToolResultFormatter.Error("SCRIPT_NOT_FOUND", new { path });

                source = File.ReadAllText(fullPath);
            }

            var problem = CSharpSyntaxCheck.FindProblem(source);
            if (problem != null)
                return ToolResultFormatter.Error("INVALID_SYNTAX", new { path, problem });

            return $"{path} is structurally sound ({source.Length} chars). " +
                   "Call request_recompile for the compiler's own verdict.";
        }

        private static List<CSharpMemberEditor.MemberEdit> ParseEdits(string edits)
        {
            var parsed = new List<CSharpMemberEditor.MemberEdit>();

            foreach (var token in JArray.Parse(edits))
            {
                parsed.Add(new CSharpMemberEditor.MemberEdit
                {
                    Op = (string)token["op"],
                    ClassName = (string)token["class_name"],
                    MethodName = (string)token["method_name"],
                    Replacement = (string)token["replacement"],
                    Position = (string)token["position"],
                    AnchorMethod = (string)token["anchor_method"]
                });
            }

            return parsed;
        }

        // Optimistic-lock check: reject when the caller's snapshot no longer matches the file on disk.
        internal static string CheckPrecondition(string path, string currentContent, string expectedSha256)
        {
            if (string.IsNullOrEmpty(expectedSha256))
                return null;

            var currentSha = ComputeSha256(currentContent);
            if (expectedSha256.Equals(currentSha, StringComparison.OrdinalIgnoreCase))
                return null;

            return ToolResultFormatter.Error("STALE_FILE", new
            {
                path,
                expected_sha256 = expectedSha256,
                current_sha256 = currentSha,
                hint = "File changed since you read it. Re-read the file (or call get_script_sha) and resend the edit."
            });
        }

        // Only reject when the edit introduces a problem the original did not have, so files that
        // were already broken (e.g. mid-refactor) can still be fixed with further edits.
        private static string CheckSyntaxRegression(string path, string originalContent, string newContent)
        {
            var problem = CSharpSyntaxCheck.FindProblem(newContent);
            if (problem == null || CSharpSyntaxCheck.FindProblem(originalContent) != null)
                return null;

            return ToolResultFormatter.Error("SYNTAX_REGRESSION", new
            {
                path,
                problem,
                hint = "This edit leaves the file structurally broken, so nothing was written. " +
                       "Re-read the file around the reported line and resend a corrected edit."
            });
        }

        internal static string ComputeSha256(string contents)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(contents ?? string.Empty));
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

    }
}
