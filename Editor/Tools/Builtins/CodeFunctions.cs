// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System.IO;
using System.Linq;
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
        [Description("Get the SHA256 hash of a script file's current contents. " +
                     "Pass the returned sha256 as expected_sha256 to write_file/patch_script so the edit is rejected " +
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

        [Description("Patch a script by finding and replacing specific text. " +
                     "Safer than write_file for small changes since it doesn't require sending the entire file content. " +
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
            // An empty needle matches at every index without advancing, so the count loop below
            // would spin forever on the main thread.
            if (string.IsNullOrEmpty(old_text))
                return ToolResultFormatter.Error("EMPTY_OLD_TEXT", new { path },
                    "old_text must be the exact, non-empty text to replace; use write_file to rewrite the whole file.");

            var fullPath = PathSafety.ResolveProjectPath(path);
            if (!File.Exists(fullPath))
                return ToolResultFormatter.Error("SCRIPT_NOT_FOUND", new { path });

            var content = File.ReadAllText(fullPath);

            var staleError = CheckPrecondition(path, content, expected_sha256);
            if (staleError != null) return staleError;

            old_text = MatchLineEndings(old_text, content);
            new_text = MatchLineEndings(new_text, content);

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

            foreach (var token in JsonParse.Array(edits))
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

        // A client sends "\n"; a CRLF file would then never match a multi-line old_text and an
        // write_file rewrite would leave the file with mixed endings. A file that already mixes them
        // is left alone, since there is no single ending to match.
        internal static string MatchLineEndings(string text, string reference)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(reference))
                return text;

            var hasCrlf = reference.Contains("\r\n");
            var hasLf = reference.Replace("\r\n", string.Empty).Contains('\n');
            if (hasCrlf == hasLf)
                return text;

            var flat = text.Replace("\r\n", "\n");
            return hasCrlf ? flat.Replace("\n", "\r\n") : flat;
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
        internal static string CheckSyntaxRegression(string path, string originalContent, string newContent)
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
