// Copyright (C) KitWright. Licensed under MIT.

using System.Text.RegularExpressions;

namespace KitWright.Editor.Tools.Builtins
{
    internal static class ExecuteCodeSafetyPolicy
    {
        private const string DeleteAssetAlternative = "The delete_asset tool for a project asset or folder; a path outside the project cannot be deleted from execute_code.";
        private const string RunProcessAlternative = "Run the command in your own shell instead; execute_menu_item for an editor action.";
        private const string StopEditorAlternative = "exit_play_mode to stop Play Mode; closing the editor is the user's call.";
        private const string BoundedLoopAlternative = "Bound the loop: `for (var i = 0; i < max; i++)` or `while (condition)`.";
        private const string LoadedAssemblyAlternative = "Project and package assemblies are already loaded: use typeof(T) or TypeCache.GetTypesDerivedFrom<T>().";
        private const string NetworkAlternative = "fetch_docs for Unity documentation; execute_code has no general network access.";
        private const string DirectApiAlternative = "Call the public API directly; find its signature with reflect_api.";
        private const string WriteFileAlternative = "The write_file tool to write a file; move_asset or AssetDatabase.CopyAsset to move or copy an asset.";
        private const string FolderAlternative = "create_folder or AssetDatabase.CreateFolder for a folder under Assets; move_asset to move one, delete_asset to delete one.";
        private const string ProjectPathAlternative = "Use a path inside the project, such as Assets/... or one built from Application.dataPath.";

        private static readonly SafetyRule[] BaseRules =
        {
            new SafetyRule(@"\bFile\.Delete\b", "File.Delete blocked by safety_checks", DeleteAssetAlternative),
            new SafetyRule(@"\bDirectory\.Delete\b", "Directory.Delete blocked by safety_checks", DeleteAssetAlternative),
            new SafetyRule(@"\bProcess\.Start\b", "Process.Start blocked by safety_checks", RunProcessAlternative),
            new SafetyRule(@"\bSystem\.Diagnostics\.Process\b", "System.Diagnostics.Process blocked by safety_checks", RunProcessAlternative),
            new SafetyRule(@"\bEnvironment\.Exit\b", "Environment.Exit blocked by safety_checks", StopEditorAlternative),
            new SafetyRule(@"\bApplication\.Quit\b", "Application.Quit blocked by safety_checks", StopEditorAlternative),
            new SafetyRule(@"\bAssetDatabase\.DeleteAsset\b", "AssetDatabase.DeleteAsset blocked by safety_checks", DeleteAssetAlternative),
            new SafetyRule(@"\bwhile\s*\(\s*true\s*\)", "Infinite while(true) loop blocked by safety_checks", BoundedLoopAlternative),
            new SafetyRule(@"\bfor\s*\(\s*;\s*;\s*\)", "Infinite for(;;) loop blocked by safety_checks", BoundedLoopAlternative),
            new SafetyRule(@"\bAssembly\.Load\b", "Assembly.Load blocked by safety_checks", LoadedAssemblyAlternative),
            new SafetyRule(@"\bAssembly\.LoadFrom\b", "Assembly.LoadFrom blocked by safety_checks", LoadedAssemblyAlternative),
            new SafetyRule(@"\bAssembly\.LoadFile\b", "Assembly.LoadFile blocked by safety_checks", LoadedAssemblyAlternative),
            new SafetyRule(@"\bnew\s+WebClient\b", "WebClient blocked by safety_checks", NetworkAlternative),
            new SafetyRule(@"\bnew\s+HttpClient\b", "HttpClient blocked by safety_checks", NetworkAlternative),
            new SafetyRule(@"\bMethodInfo\b[\s\S]*?\bInvoke\b", "Reflection method Invoke blocked by safety_checks", DirectApiAlternative),
            new SafetyRule(@"\.GetMethod\s*\(", "Reflection GetMethod blocked by safety_checks", DirectApiAlternative),
            new SafetyRule(@"\bType\.GetType\s*\(", "Indirect Type.GetType resolution blocked by safety_checks", "typeof(T), or TypeCache to look a type up by its base type or attribute."),
            new SafetyRule(@"\bInvokeMember\s*\(", "Reflection InvokeMember blocked by safety_checks", DirectApiAlternative),
            new SafetyRule(@"\bConvert\.FromBase64String\b", "Runtime Base64 decoding blocked by safety_checks", "Put the plain code or data in the snippet instead of decoding it at runtime."),
        };

        private static readonly SafetyRule[] StrictRules =
        {
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?File\.(?:WriteAllText|WriteAllBytes|WriteAllLines|AppendAllText|AppendAllLines|Copy|Create|CreateText|OpenWrite|Move|Replace|SetAttributes|SetCreationTime|SetLastAccessTime|SetLastWriteTime)\b", "File write/move operation blocked by strict filesystem safety", WriteFileAlternative),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?Directory\.(?:CreateDirectory|Delete|Move)\b", "Directory write/destructive operation blocked by strict filesystem safety", FolderAlternative),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileInfo\.(?:CopyTo|Create|CreateText|Delete|MoveTo|Replace)\b", "FileInfo write/destructive operation blocked by strict filesystem safety", WriteFileAlternative),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?DirectoryInfo\.(?:Create|CreateSubdirectory|Delete|MoveTo)\b", "DirectoryInfo write/destructive operation blocked by strict filesystem safety", FolderAlternative),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileStream\s*\(", "Raw FileStream construction blocked by strict filesystem safety", "The write_file tool to write; File.ReadAllBytes (not blocked) or read_file to read."),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamWriter\s*\(", "Raw StreamWriter construction blocked by strict filesystem safety", "The write_file tool."),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamReader\s*\(", "Raw StreamReader construction blocked by strict filesystem safety", "File.ReadAllText (not blocked) or the read_file tool."),
            new SafetyRule("\"(?:~|%USERPROFILE%|%APPDATA%|%LOCALAPPDATA%|\\$HOME)(?:/|\\\\|\\\\\\\\|\"|$)", "User home/config path blocked by strict filesystem safety", ProjectPathAlternative),
            new SafetyRule("\"(?:[A-Za-z]:\\\\|\\\\\\\\|/Users/|/home/|/root/|/System/|/Library/|/Applications/|/bin/|/sbin/|/usr/|/etc/|/var/|/private/|/tmp/)", "Absolute or system path blocked by strict filesystem safety", ProjectPathAlternative),
            new SafetyRule("\"[^\"]*(?:\\.\\./|\\.\\.\\\\)[^\"]*\"", "Path traversal blocked by strict filesystem safety", "Use a path inside the project, built from Application.dataPath, instead of `..`."),
        };

        // Concatenated adjacent string literals ("Fi" + "le.Delete") match the same rules as the
        // joined literal would, since a rule may span what the author split across "+".
        private static readonly Regex AdjacentStringLiterals =
            new Regex("\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\+\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        private static string CollapseAdjacentStringLiterals(string code)
        {
            string previous;
            do
            {
                previous = code;
                code = AdjacentStringLiterals.Replace(code, "\"$1$2\"");
            } while (code != previous);

            return code;
        }

        public static bool TryFindViolation(string code, bool strictFilesystemChecks, out string pattern, out string reason)
        {
            return TryFindViolation(code, strictFilesystemChecks, out pattern, out reason, out _);
        }

        public static bool TryFindViolation(string code, bool strictFilesystemChecks, out string pattern, out string reason, out string alternative)
        {
            code = code ?? string.Empty;
            var normalized = CollapseAdjacentStringLiterals(code);

            if (TryFindViolation(normalized, BaseRules, out pattern, out reason, out alternative))
                return true;

            if (strictFilesystemChecks &&
                TryFindViolation(normalized, StrictRules, out pattern, out reason, out alternative))
            {
                return true;
            }

            pattern = null;
            reason = null;
            alternative = null;
            return false;
        }

        private static bool TryFindViolation(string code, SafetyRule[] rules, out string pattern, out string reason, out string alternative)
        {
            foreach (var rule in rules)
            {
                if (!Regex.IsMatch(code, rule.Pattern))
                    continue;

                pattern = rule.Pattern;
                reason = rule.Reason;
                alternative = rule.Alternative;
                return true;
            }

            pattern = null;
            reason = null;
            alternative = null;
            return false;
        }

        private sealed class SafetyRule
        {
            public SafetyRule(string pattern, string reason, string alternative)
            {
                Pattern = pattern;
                Reason = reason;
                Alternative = alternative;
            }

            public string Pattern { get; }
            public string Reason { get; }
            public string Alternative { get; }
        }
    }
}
