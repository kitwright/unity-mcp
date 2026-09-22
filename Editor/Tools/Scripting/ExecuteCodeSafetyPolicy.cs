// Copyright (C) KitWright. All rights reserved.

using System.Text.RegularExpressions;

namespace KitWright.Editor.Tools.Builtins
{
    internal static class ExecuteCodeSafetyPolicy
    {
        private static readonly SafetyRule[] BaseRules =
        {
            new SafetyRule(@"\bFile\.Delete\b", "File.Delete blocked by safety_checks"),
            new SafetyRule(@"\bDirectory\.Delete\b", "Directory.Delete blocked by safety_checks"),
            new SafetyRule(@"\bProcess\.Start\b", "Process.Start blocked by safety_checks"),
            new SafetyRule(@"\bSystem\.Diagnostics\.Process\b", "System.Diagnostics.Process blocked by safety_checks"),
            new SafetyRule(@"\bEnvironment\.Exit\b", "Environment.Exit blocked by safety_checks"),
            new SafetyRule(@"\bApplication\.Quit\b", "Application.Quit blocked by safety_checks"),
            new SafetyRule(@"\bAssetDatabase\.DeleteAsset\b", "AssetDatabase.DeleteAsset blocked by safety_checks"),
            new SafetyRule(@"\bwhile\s*\(\s*true\s*\)", "Infinite while(true) loop blocked by safety_checks"),
            new SafetyRule(@"\bfor\s*\(\s*;\s*;\s*\)", "Infinite for(;;) loop blocked by safety_checks"),
            new SafetyRule(@"\bAssembly\.Load\b", "Assembly.Load blocked by safety_checks"),
            new SafetyRule(@"\bAssembly\.LoadFrom\b", "Assembly.LoadFrom blocked by safety_checks"),
            new SafetyRule(@"\bAssembly\.LoadFile\b", "Assembly.LoadFile blocked by safety_checks"),
            new SafetyRule(@"\bnew\s+WebClient\b", "WebClient blocked by safety_checks"),
            new SafetyRule(@"\bnew\s+HttpClient\b", "HttpClient blocked by safety_checks"),
            new SafetyRule(@"\bMethodInfo\b[\s\S]*?\bInvoke\b", "Reflection method Invoke blocked by safety_checks"),
            new SafetyRule(@"\.GetMethod\s*\(", "Reflection GetMethod blocked by safety_checks"),
            new SafetyRule(@"\bType\.GetType\s*\(", "Indirect Type.GetType resolution blocked by safety_checks"),
            new SafetyRule(@"\bInvokeMember\s*\(", "Reflection InvokeMember blocked by safety_checks"),
            new SafetyRule(@"\bConvert\.FromBase64String\b", "Runtime Base64 decoding blocked by safety_checks"),
        };

        private static readonly SafetyRule[] StrictRules =
        {
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?File\.(?:WriteAllText|WriteAllBytes|WriteAllLines|AppendAllText|AppendAllLines|Copy|Create|CreateText|OpenWrite|Move|Replace|SetAttributes|SetCreationTime|SetLastAccessTime|SetLastWriteTime)\b", "File write/move operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?Directory\.(?:CreateDirectory|Delete|Move)\b", "Directory write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileInfo\.(?:CopyTo|Create|CreateText|Delete|MoveTo|Replace)\b", "FileInfo write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?DirectoryInfo\.(?:Create|CreateSubdirectory|Delete|MoveTo)\b", "DirectoryInfo write/destructive operation blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?FileStream\s*\(", "Raw FileStream construction blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamWriter\s*\(", "Raw StreamWriter construction blocked by strict filesystem safety"),
            new SafetyRule(@"(?<![\w.])(?:System\.IO\.)?StreamReader\s*\(", "Raw StreamReader construction blocked by strict filesystem safety"),
            new SafetyRule("\"(?:~|%USERPROFILE%|%APPDATA%|%LOCALAPPDATA%|\\$HOME)(?:/|\\\\|\\\\\\\\|\"|$)", "User home/config path blocked by strict filesystem safety"),
            new SafetyRule("\"(?:[A-Za-z]:\\\\|\\\\\\\\|/Users/|/home/|/root/|/System/|/Library/|/Applications/|/bin/|/sbin/|/usr/|/etc/|/var/|/private/|/tmp/)", "Absolute or system path blocked by strict filesystem safety"),
            new SafetyRule("\"[^\"]*(?:\\.\\./|\\.\\.\\\\)[^\"]*\"", "Path traversal blocked by strict filesystem safety"),
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
            code = code ?? string.Empty;
            var normalized = CollapseAdjacentStringLiterals(code);

            if (TryFindViolation(normalized, BaseRules, out pattern, out reason))
                return true;

            if (strictFilesystemChecks &&
                TryFindViolation(normalized, StrictRules, out pattern, out reason))
            {
                return true;
            }

            pattern = null;
            reason = null;
            return false;
        }

        private static bool TryFindViolation(string code, SafetyRule[] rules, out string pattern, out string reason)
        {
            foreach (var rule in rules)
            {
                if (!Regex.IsMatch(code, rule.Pattern))
                    continue;

                pattern = rule.Pattern;
                reason = rule.Reason;
                return true;
            }

            pattern = null;
            reason = null;
            return false;
        }

        private sealed class SafetyRule
        {
            public SafetyRule(string pattern, string reason)
            {
                Pattern = pattern;
                Reason = reason;
            }

            public string Pattern { get; }
            public string Reason { get; }
        }
    }
}
