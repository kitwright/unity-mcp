// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Skills contributed by whatever is installed: any package - or any folder directly under
    /// Assets/ - holding "Skills~/&lt;id&gt;/SKILL.md" adds that skill to the catalog. An add-on
    /// ships skills by shipping files, so nothing here names a specific package and removing the
    /// package removes its skills.
    /// </summary>
    internal static class PackageSkillCatalog
    {
        // Not cached: Skills~ is invisible to the AssetDatabase, so an edited SKILL.md triggers no
        // reload, and a domain-lifetime cache kept writing the old body on Rewrite.
        internal static IReadOnlyList<ProjectSkillsManager.SkillDefinition> Discover()
        {
            return Scan();
        }

        private static List<ProjectSkillsManager.SkillDefinition> Scan()
        {
            var found = new List<ProjectSkillsManager.SkillDefinition>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in SkillRoots())
            {
                if (!Directory.Exists(root))
                    continue;

                foreach (var directory in Directory.GetDirectories(root))
                {
                    var file = Path.Combine(directory, "SKILL.md");
                    if (!File.Exists(file))
                        continue;

                    var id = Path.GetFileName(directory);
                    if (!seen.Add(id))
                        continue;

                    try
                    {
                        found.Add(Parse(id, File.ReadAllText(file), directory));
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[KitWright] Skipped skill '{id}': {ex.Message}");
                    }
                }
            }

            return found;
        }

        // Skills~ has a trailing tilde so Unity never imports it and AssetDatabase cannot see it;
        // every lookup here is a plain disk read. The Assets/ pass is one level deep on purpose -
        // it covers an Asset Store package unpacked to Assets/<Vendor>/ without walking the tree.
        private static IEnumerable<string> SkillRoots()
        {
            UnityEditor.PackageManager.PackageInfo[] packages;
            try
            {
                packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            }
            catch
            {
                packages = Array.Empty<UnityEditor.PackageManager.PackageInfo>();
            }

            foreach (var package in packages)
            {
                if (!string.IsNullOrEmpty(package?.resolvedPath))
                    yield return Path.Combine(package.resolvedPath, "Skills~");
            }

            if (!Directory.Exists(Application.dataPath))
                yield break;

            foreach (var directory in Directory.GetDirectories(Application.dataPath))
                yield return Path.Combine(directory, "Skills~");
        }

        internal static ProjectSkillsManager.SkillDefinition Parse(string id, string markdown, string sourceDirectory = null)
        {
            var body = StripFrontMatter(markdown).Trim();
            if (body.Length == 0)
                throw new InvalidOperationException("SKILL.md has no content below its front matter.");

            var description = FrontMatterValue(markdown, "description");
            return new ProjectSkillsManager.SkillDefinition(
                id,
                ContentVersion(markdown, sourceDirectory),
                Title(id),
                string.IsNullOrEmpty(description) ? id : description,
                false,
                null,
                null,
                body,
                FrontMatterExtras(markdown),
                sourceDirectory);
        }

        // Every file under a skill's folder, as a path relative to it. A skill file beside SKILL.md (scripts/,
        // references/...) is installed with it.
        internal static List<string> RelativeFiles(string directory)
        {
            var files = new List<string>();
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return files;

            var prefix = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                files.Add(file.Substring(prefix));

            return files;
        }

        // Bump when the rendered skill document or Cursor rule changes (front matter, quoting, body): the version
        // marker of an installed copy comes from this hash, so an old render only reads as outdated if it moves.
        internal const int RendererVersion = 2;

        // The skill file is the source of truth, so its content hash is the version: an edited
        // SKILL.md, or an edited file beside it, reports an available update with no version field to
        // remember to bump. A skill with nothing beside SKILL.md hashes as it always did.
        private static string ContentVersion(string markdown, string sourceDirectory)
        {
            var input = new List<byte>(Encoding.UTF8.GetBytes(RendererVersion + "\n" + (markdown ?? string.Empty)));
            try
            {
                foreach (var relative in ProjectSkillsManager.PlanSiblingFiles(RelativeFiles(sourceDirectory), sourceDirectory, out _))
                {
                    input.AddRange(Encoding.UTF8.GetBytes("\n" + relative + "\n"));
                    input.AddRange(File.ReadAllBytes(Path.Combine(sourceDirectory, relative)));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(input.ToArray());
                return string.Concat(bytes.Take(5).Select(b => b.ToString("x2")));
            }
        }

        private static string Title(string id)
        {
            var words = id.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word.Substring(1));
            return string.Join(" ", words);
        }

        private static string[] SplitLines(string markdown)
        {
            return (markdown ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        }

        private static bool IsFence(string line)
        {
            return line.Trim() == "---";
        }

        internal static string FrontMatterValue(string markdown, string key)
        {
            var lines = SplitLines(markdown);
            if (lines.Length == 0 || !IsFence(lines[0]))
                return null;

            for (int i = 1; i < lines.Length && !IsFence(lines[i]); i++)
            {
                var separator = lines[i].IndexOf(':');
                if (separator <= 0 || IsIndented(lines[i]))
                    continue;
                if (!string.Equals(lines[i].Substring(0, separator).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return ReadScalar(lines[i].Substring(separator + 1).Trim(), ContinuationLines(lines, i + 1));
            }

            return null;
        }

        private static bool IsIndented(string line)
        {
            return line.Length > 0 && char.IsWhiteSpace(line[0]);
        }

        private static List<string> ContinuationLines(string[] lines, int from)
        {
            var found = new List<string>();
            for (int i = from; i < lines.Length && !IsFence(lines[i]) && (lines[i].Length == 0 || IsIndented(lines[i])); i++)
                found.Add(lines[i].Trim());

            return found;
        }

        // `description: >-` and a value wrapped onto indented lines are as common as a one-line value; reading
        // only the first line made the skill's description ">-".
        private static string ReadScalar(string first, List<string> continuation)
        {
            var more = continuation.Where(line => line.Length > 0);

            if (first.StartsWith("|", StringComparison.Ordinal) || first.StartsWith(">", StringComparison.Ordinal))
                return string.Join(first[0] == '|' ? "\n" : " ", more).Trim();

            if (first.Length >= 2 && first[0] == '"' && first[first.Length - 1] == '"')
                return Regex.Replace(first.Substring(1, first.Length - 2), @"\\([\\""])", "$1");

            if (first.Length >= 2 && first[0] == '\'' && first[first.Length - 1] == '\'')
                return first.Substring(1, first.Length - 2).Replace("''", "'");

            return string.Join(" ", new[] { first }.Concat(more)).Trim();
        }

        private static readonly HashSet<string> WrittenFrontMatterKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "description", "version", "platform" };

        // What the author declared beyond the four keys the installed copy writes itself (allowed-tools,
        // disable-model-invocation...) decides how an agent may load the skill, so it travels with it.
        internal static string FrontMatterExtras(string markdown)
        {
            var lines = SplitLines(markdown);
            if (lines.Length == 0 || !IsFence(lines[0]))
                return string.Empty;

            var kept = new List<string>();
            var keep = false;
            for (int i = 1; i < lines.Length && !IsFence(lines[i]); i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    continue;

                if (!IsIndented(line))
                {
                    var separator = line.IndexOf(':');
                    keep = separator > 0 && !WrittenFrontMatterKeys.Contains(line.Substring(0, separator).Trim());
                }

                if (keep)
                    kept.Add(line);
            }

            return string.Join("\n", kept);
        }

        // A description is free text; written bare, a ": " or " #" in it makes the front matter invalid YAML
        // and a strict loader skips the skill.
        internal static string YamlScalar(string value)
        {
            value = value ?? string.Empty;
            var plain = value.Length > 0 &&
                        value.Trim() == value &&
                        value.IndexOfAny(new[] { '\r', '\n' }) < 0 &&
                        !value.Contains(": ") &&
                        !value.Contains(" #") &&
                        !value.EndsWith(":", StringComparison.Ordinal) &&
                        "-?:,[]{}#&*!|>'\"%@`".IndexOf(value[0]) < 0;
            if (plain)
                return value;

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        internal static string StripFrontMatter(string markdown)
        {
            var lines = SplitLines(markdown);
            if (lines.Length == 0 || !IsFence(lines[0]))
                return markdown ?? string.Empty;

            for (int i = 1; i < lines.Length; i++)
            {
                if (IsFence(lines[i]))
                    return string.Join("\n", lines.Skip(i + 1));
            }

            return markdown ?? string.Empty;
        }
    }
}
