// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
                        found.Add(Parse(id, File.ReadAllText(file)));
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

        internal static ProjectSkillsManager.SkillDefinition Parse(string id, string markdown)
        {
            var body = StripFrontMatter(markdown).Trim();
            if (body.Length == 0)
                throw new InvalidOperationException("SKILL.md has no content below its front matter.");

            var description = FrontMatterValue(markdown, "description");
            return new ProjectSkillsManager.SkillDefinition(
                id,
                ContentVersion(markdown),
                Title(id),
                string.IsNullOrEmpty(description) ? id : description,
                false,
                null,
                null,
                body);
        }

        // The skill file is the source of truth, so its content hash is the version: an edited
        // SKILL.md reports an available update with no version field to remember to bump.
        private static string ContentVersion(string markdown)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(markdown ?? string.Empty));
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
                if (separator <= 0)
                    continue;
                if (!string.Equals(lines[i].Substring(0, separator).Trim(), key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return lines[i].Substring(separator + 1).Trim();
            }

            return null;
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
