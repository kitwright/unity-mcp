// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using KitWright.Editor.Services;
using KitWright.Editor.Tools;

namespace KitWright.Editor.MCP.Server
{
    internal static class ProjectSkillsManager
    {
        internal const string ManagedMarker = "<!-- KitWright Unity managed project skills -->";

        // For shared root instructions files (CLAUDE.md / AGENTS.md) KitWright manages ONLY the
        // region between ManagedMarker (begin) and ManagedEndMarker (end); everything outside the
        // block is the user's and is never modified. ManagedMarker doubles as the begin marker so
        // IsManagedFile / version-status detection keep working unchanged.
        internal const string ManagedEndMarker = "<!-- /KitWright Unity managed project skills -->";

        // The block is committed, so it must read the same on every checkout: no absolute path.
        private const string ProjectRootLine = "- Project root: the folder that contains `Assets/` and `ProjectSettings/`.";

        private const string ManifestDirectory = ".kitwright/skills";
        private const string ManifestFileName = "manifest.json";
        private const string ProjectSkillVersionsMarkerPrefix = "<!-- KitWright Unity project skill versions: ";
        private const string SkillVersionMarkerPrefix = "<!-- KitWright Unity skill version: ";
        private const string CodexManagedNotice = "This section is managed by KitWright MCP for Unity. Everything between the begin and end markers is regenerated on each sync; edit outside this block.";
        private const string ClaudeManagedNotice = "This section is managed by KitWright MCP for Unity for Claude Code. Everything between the begin and end markers is regenerated on each sync; edit outside this block.";

        private static readonly string[] SupportedPlatforms = { "codex", "claude", "cursor", "agents" };

        private static readonly List<SkillDefinition> SkillCatalog = new List<SkillDefinition>
        {
            new SkillDefinition(
                "unity-mcp-workflow",
                "1.0.1",
                "Unity MCP Workflow",
                "Efficient workflow for using Unity MCP to edit, import, compile, inspect, and test Unity projects.",
                true,
                "Use this skill when Codex or another AI agent is working in a Unity project and needs to verify code, prefabs, UI, Play Mode behavior, screenshots, scene hierarchy, console logs, domain reloads, or MCP connection issues.",
                new[]
                {
                    "Use Unity MCP as the source of truth for Editor state, scene hierarchy, prefab references, runtime objects, compilation status, and Play Mode behavior.",
                    "Locate the real Unity project root and active scene before editing.",
                    "Inspect hierarchy, prefab paths, selected objects, and relevant component references through MCP before changing user-named objects. Treat user-provided object names as hints, not paths.",
                    "Tool returns are structured JSON: `{success, message, data}` for success and `{success: false, code, error, data}` for errors. Parse `data` and check `code` (UPPERCASE_SNAKE_CASE) for branching — do not pattern-match free-form text.",
                    "Prefer `instanceId` returned by tools for follow-up calls. Pass it back via the `find_method=by_id` (or auto-detect when the value parses as an integer). This is more reliable than re-resolving by `name` when scenes contain duplicates.",
                    "Use the `find_method` parameter on GameObject/Component tools to choose how a target is resolved: `by_id`, `by_name`, `by_path`, `by_tag`, `by_layer`, `by_component`, or `by_id_or_name_or_path`. Default auto-detect picks `by_id` for integers, `by_path` for slashed strings, otherwise `by_name`.",
                    "When a GameObject has multiple components of the same type, target a specific one with `component_instance_id` instead of the type name to avoid hitting the wrong component.",
                    "Set component fields with `set_component_properties`: it now writes through SerializedObject, so `[SerializeField] private` fields are reachable. Pass Object references as JSON `{\"fileID\": <instanceId>}` (preferred) or `{\"assetPath\": \"Assets/...\"}`. The response reports per-field success/failure.",
                    "Inspect editor-level state through dedicated tools: `get_selection`, `set_selection`, `get_prefab_stage`, `get_active_tool`, `get_windows`, `get_tags`, `get_layers`, `get_build_settings`. Do not write `execute_code` snippets just to read this.",
                    "When no specialized MCP tool covers an editor action, try `execute_menu_item` (e.g. 'GameObject/2D Object/Sprite', 'Window/Layouts/Default', 'Edit/Undo') before falling back to `execute_code`. A path ending in '...' is refused because that is the editor's convention for an item that opens a modal dialog and freezes the editor; pass `allow_modal=true` only for a path you know opens an ordinary dockable window.",
                    "When Tool Exposure uses the default `core` profile, rely on the focused workflow tools: `execute_code`, recompilation, Play Mode control, hierarchy, console logs, screenshots, input simulation, and performance inspection.",
                    "When Tool Exposure uses the default `full` profile, all registered MCP tools are available. Prefer specific tools for simple scene, asset, GameObject, component, prefab, camera, UI, package, animation, file, or visual-feedback operations.",
                    "If Tool Exposure has been customized and a named tool is unavailable, adapt to the exposed tool list and report which expected tool is missing.",
                    "Never edit Unity serialized files (`.unity`, `.prefab`, `.asset`) with shell text tools or patches. Use Unity MCP or Editor APIs for scenes, prefabs, and ScriptableObject assets; shell tools may only inspect or locate these files.",
                    "Choose the correct edit surface: source files with normal repo tools, scene objects through Unity APIs and saved scenes, prefab assets through `PrefabUtility.LoadPrefabContents` and `SaveAsPrefabAsset`.",
                    "For `execute_code`, prefer the IKitWrightCommand template over the legacy `static string Run()`: include `using KitWright.Editor.Tools.Scripting;`, implement `IKitWrightCommand`, and use `ctx.RegisterObjectCreation`, `ctx.RegisterObjectModification`, `ctx.DestroyObject` so created/modified objects participate in editor Undo automatically. Use `ctx.Log` / `ctx.LogWarning` / `ctx.LogError` for traceable output that comes back in the response (without polluting the Unity console).",
                    "Batch related Unity-side changes in one guarded `execute_code` snippet. Null-guard every lookup, return explicit missing path/object/component messages, and include concise before/after values.",
                    "`execute_code` now refreshes the asset database and waits for compilation to finish before compiling the snippet, so external file edits are picked up automatically. For other tools that depend on the latest assemblies (e.g. `get_compilation_errors`), still call `request_recompile` after external file edits.",
                    "After code or resource edits, exit Play Mode if needed and call `request_recompile` first: it is the call that imports the edits, and unless it reports that Unity started importing or recompiling, its answer already carries the compile errors. Only when it reports that, call `wait_for_compilation(force_refresh=false)`, whose answer carries them too. Do not follow either call with `get_compilation_errors`. Never call `wait_for_compilation(force_refresh=false)` without a `request_recompile` before it: on its own it reads the state from before the edits.",
                    "When editing several `.cs` files, write them all with your own file tools, then call `request_recompile` once. `create_script`, `edit_script`, `patch_script`, `edit_script_members` and `write_file` each refresh the asset database, so writing N files through them starts up to N imports and compiles.",
                    "Group independent reads into one `batch_execute` call (`commands`: a JSON array of `{\"name\", \"params\"}` objects) instead of one round-trip per read.",
                    "Call `wait_for_compilation` before Play Mode, screenshots, or conclusions when a previous edit has not yet been confirmed.",
                    "`enter_play_mode` and a recompile both reload the domain. In broker mode (the default) the broker holds requests across the reload and delivers them once Unity is back, so make the next call directly — no poll loop; a call answered with \"Unity is recompiling scripts, so this tool did not run\" is simply repeated. If the connection is refused instead (direct mode), poll the JSON-RPC `ping` method over HTTP (with native tools only, `get_reload_recovery_status`) until it answers. Never probe with `tools/list`: it returns the whole tool catalog, tens of KB per probe.",
                    "`request_recompile` is rejected while Unity is in Play Mode — Unity does not process script compilation or domain reloads while playing. Call `exit_play_mode` first, then retry `request_recompile`.",
                    "If a request is interrupted by script recompilation or domain reload, treat the result as unknown until `get_reload_recovery_status`, compilation checks, and MCP readback confirm the final state.",
                    "Read back exact values from Unity after changes, not only success messages.",
                    "Test actual behavior in Unity through hierarchy, console logs, Play Mode, UI interactions, screenshots, or targeted `execute_code` checks.",
                    "When Unity readback and text files disagree for serialized scene or prefab state, trust Unity readback and investigate the asset path.",
                    "Do not run self-healing fallback loops. If a reference, path, tool, or package is missing, report one clear error and stop or skip that item instead of guessing new paths or silently creating replacements.",
                    "For `UnityEngine.Object` references, never use `??=` for lazy rebinding. Use explicit `if (field == null) field = Resolve();` checks so Unity fake-null references are handled correctly.",
                    "If Play Mode is entered, exit Play Mode before finishing unless the user explicitly wants it left running."
                }),
        };

        internal static IReadOnlyList<SkillDefinition> GetBuiltInSkills()
        {
            return SkillCatalog.Where(skill => skill.IsBuiltIn).ToArray();
        }

        internal static IReadOnlyList<SkillDefinition> GetPackageSkills()
        {
            return AllSkills.Where(skill => !skill.IsBuiltIn).ToArray();
        }

        // A skill description is written for the agent router: a full sentence followed by a
        // trigger list. A card shows this lead sentence and keeps the whole text in its tooltip.
        internal static string ShortDescription(string description)
        {
            var text = (description ?? string.Empty).Trim();

            var triggers = text.IndexOf("Triggers", StringComparison.OrdinalIgnoreCase);
            if (triggers > 0)
                text = text.Substring(0, triggers).TrimEnd(' ', '.', ',', '-');

            var sentence = text.IndexOf(". ", StringComparison.Ordinal);
            return sentence > 0 ? text.Substring(0, sentence) : text;
        }

        // Skills shipped as files by an installed package are optional skills like any other.
        // A file skill never shadows a catalog skill: the ids drive file names, and two entries
        // sharing one id would write the same SKILL.md twice with different content.
        private static IEnumerable<SkillDefinition> AllSkills =>
            SkillCatalog.Concat(PackageSkillCatalog.Discover()
                .Where(skill => !SkillCatalog.Any(existing =>
                    string.Equals(existing.Id, skill.Id, StringComparison.OrdinalIgnoreCase))));

        internal static ProjectSkillsManifest LoadManifest(string projectRoot)
        {
            var manifestPath = GetManifestPath(projectRoot);
            try
            {
                if (File.Exists(manifestPath))
                {
                    var json = File.ReadAllText(manifestPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonUtility.FromJson<ProjectSkillsManifest>(json);
                        if (loaded != null)
                            return NormalizeManifest(loaded);
                    }
                }
            }
            catch
            {
            }

            return CreateDefaultManifest();
        }

        internal static void SaveManifest(string projectRoot, ProjectSkillsManifest manifest)
        {
            var normalized = NormalizeManifest(manifest);
            normalized.skillVersions = BuildCurrentSkillVersionEntries(normalized);
            var manifestPath = GetManifestPath(projectRoot);
            var directory = Path.GetDirectoryName(manifestPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(manifestPath, JsonUtility.ToJson(normalized, true));
        }

        internal static string GetManifestPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ManifestDirectory, ManifestFileName);
        }

        internal static string GetCodexAgentsPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "AGENTS.md");
        }

        internal static string GetClaudeInstructionsPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "CLAUDE.md");
        }

        internal static string GetCursorRulesPath(string projectRoot)
        {
            return Path.Combine(projectRoot, ".cursor", "rules");
        }

        internal static string GetCodexSkillsRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".codex", "skills");
        }

        internal static string GetClaudeSkillsRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".claude", "skills");
        }

        internal static string GetAgentsSkillsRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, ".agents", "skills");
        }

        // False only when non-managed instruction files exist and the user declines to overwrite.
        internal static bool ConfirmOverwriteConflicts(string projectRoot, IEnumerable<string> platforms)
        {
            var conflictPaths = GetPlatformConflictPaths(projectRoot, platforms);
            if (conflictPaths.Length == 0)
                return true;

            return EditorUtility.DisplayDialog(
                "Project Skills Configuration",
                "Existing non-managed project instruction files were found:\n\n" +
                string.Join("\n", conflictPaths) +
                "\n\nOverwrite them with KitWright-managed files?",
                "Overwrite",
                "Cancel");
        }

        internal static void ApplyConfiguration(string projectRoot, IEnumerable<string> selectedPlatforms)
        {
            var manifest = new ProjectSkillsManifest
            {
                platforms = selectedPlatforms?.Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>(),
                managedFiles = LoadManifest(projectRoot).managedFiles
            };

            ValidateInstructionFiles(projectRoot, NormalizeManifest(manifest));
            SaveManifest(projectRoot, manifest);

            var normalized = LoadManifest(projectRoot);
            try
            {
                SyncCodex(projectRoot, normalized);
                SyncClaude(projectRoot, normalized);
                SyncCursor(projectRoot, normalized);
                SyncAgents(projectRoot, normalized);
            }
            finally
            {
                SaveManifest(projectRoot, normalized);
            }
        }

        // Every refusal an instructions file can raise happens here, before the manifest or any file is
        // written, so a refused configuration leaves the project as it was.
        private static void ValidateInstructionFiles(string projectRoot, ProjectSkillsManifest manifest)
        {
            var codexPath = GetCodexAgentsPath(projectRoot);
            var claudePath = GetClaudeInstructionsPath(projectRoot);
            var codexLegacy = BuildLegacyCodexAgentsContent(projectRoot, manifest);
            var claudeLegacy = BuildLegacyClaudeInstructionsContent(projectRoot, manifest);

            if (manifest.platforms.Contains("codex", StringComparer.OrdinalIgnoreCase))
                WriteManagedBlock(codexPath, "# AGENTS.md", BuildCodexManagedBlock(projectRoot, manifest), codexLegacy, true);
            else
                RemoveManagedBlock(codexPath, "# AGENTS.md", codexLegacy, true);

            if (manifest.platforms.Contains("claude", StringComparer.OrdinalIgnoreCase))
                WriteManagedBlock(claudePath, "# CLAUDE.md", BuildClaudeManagedBlock(projectRoot, manifest), claudeLegacy, true);
            else
                RemoveManagedBlock(claudePath, "# CLAUDE.md", claudeLegacy, true);
        }

        // Every skill the installed packages ship is installed. A per-skill opt-in existed here and
        // defaulted to none, so a package could ship a skill that no project ever saw until someone
        // found the toggle: "Configure + Skills" wrote the built-in skill alone and reported success.
        // Uninstalling the package is the way to remove its skills, which is where that decision
        // already lives.
        internal static IReadOnlyList<SkillDefinition> GetInstalledSkills()
        {
            return AllSkills.ToArray();
        }

        internal static ProjectSkillsUpgradeStatus GetUpgradeStatus(
            string projectRoot,
            ProjectSkillsManifest manifest,
            string platformId)
        {
            var normalized = NormalizeManifest(manifest);
            if (string.IsNullOrEmpty(projectRoot) ||
                string.IsNullOrEmpty(platformId) ||
                !normalized.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase))
            {
                return new ProjectSkillsUpgradeStatus(Array.Empty<SkillFileVersionStatus>());
            }

            var expectedFiles = GetExpectedVersionedFilesForPlatform(projectRoot, normalized, platformId);
            var entries = new List<SkillFileVersionStatus>(expectedFiles.Count);
            foreach (var expected in expectedFiles)
            {
                entries.Add(InspectVersionedFile(expected));
            }

            return new ProjectSkillsUpgradeStatus(entries);
        }

        internal static bool IsManagedFile(string path)
        {
            try
            {
                return File.Exists(path) && ManagedBlockText.HasManagedHeader(File.ReadAllText(path));
            }
            catch
            {
                return false;
            }
        }

        internal static string[] GetPlatformConflictPaths(string projectRoot, IEnumerable<string> selectedPlatforms)
        {
            var conflicts = new List<string>();
            var platforms = new HashSet<string>(selectedPlatforms ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            // AGENTS.md and CLAUDE.md are shared files. Their KitWright block is appended when no
            // managed marker exists, so user-owned content there is no longer an overwrite conflict.
            // Skill folders are named after the skill id — that name is the slash command in
            // Antigravity and Claude Code — so a hand-written skill of the same name is a conflict.
            foreach (var (platform, skillsRoot) in new[]
            {
                ("codex", GetCodexSkillsRoot(projectRoot)),
                ("claude", GetClaudeSkillsRoot(projectRoot)),
                ("agents", GetAgentsSkillsRoot(projectRoot))
            })
            {
                if (!platforms.Contains(platform))
                    continue;

                foreach (var skill in AllSkills)
                {
                    var path = Path.Combine(skillsRoot, skill.Id, "SKILL.md");
                    if (File.Exists(path) && !IsManagedFile(path))
                        conflicts.Add(path);
                }
            }

            if (platforms.Contains("cursor"))
            {
                var rulesRoot = GetCursorRulesPath(projectRoot);
                foreach (var skill in AllSkills)
                {
                    var path = Path.Combine(rulesRoot, $"kitwright-{skill.Id}.mdc");
                    if (File.Exists(path) && !IsManagedFile(path))
                        conflicts.Add(path);
                }
            }

            return conflicts.ToArray();
        }

        internal static IReadOnlyList<string> GetGeneratedPathsForPlatform(string projectRoot, ProjectSkillsManifest manifest, string platformId)
        {
            var enabled = manifest != null && manifest.platforms.Contains(platformId, StringComparer.OrdinalIgnoreCase);
            if (!enabled)
                return Array.Empty<string>();

            var paths = new List<string> { GetManifestPath(projectRoot) };

            switch (platformId?.Trim().ToLowerInvariant())
            {
                case "codex":
                    paths.Add(GetCodexAgentsPath(projectRoot));
                    paths.Add(GetCodexSkillsRoot(projectRoot));
                    break;
                case "claude":
                    paths.Add(GetClaudeInstructionsPath(projectRoot));
                    paths.Add(GetClaudeSkillsRoot(projectRoot));
                    break;
                case "cursor":
                    paths.Add(GetCursorRulesPath(projectRoot));
                    break;
                case "agents":
                    paths.Add(GetAgentsSkillsRoot(projectRoot));
                    break;
            }

            return paths;
        }

        private static void SyncCodex(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("codex", StringComparer.OrdinalIgnoreCase);
            var agentsPath = GetCodexAgentsPath(projectRoot);
            var skillsRoot = GetCodexSkillsRoot(projectRoot);

            if (!enabled)
            {
                RemoveManagedBlock(agentsPath, "# AGENTS.md", BuildLegacyCodexAgentsContent(projectRoot, manifest));
                DeleteManagedSkillDirectories(projectRoot, skillsRoot, manifest);
                return;
            }

            Directory.CreateDirectory(skillsRoot);

            // Manage only the delimited begin..end block; hand-authored content elsewhere in
            // AGENTS.md is preserved (created/appended if the file has no block yet).
            WriteManagedBlock(
                agentsPath,
                "# AGENTS.md",
                BuildCodexManagedBlock(projectRoot, manifest),
                BuildLegacyCodexAgentsContent(projectRoot, manifest));

            WriteManagedSkillDirectories(projectRoot, skillsRoot, manifest, SkillPlatform.Codex, GetInstalledSkills());
        }

        private static void SyncClaude(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("claude", StringComparer.OrdinalIgnoreCase);
            var claudePath = GetClaudeInstructionsPath(projectRoot);
            var skillsRoot = GetClaudeSkillsRoot(projectRoot);

            if (!enabled)
            {
                RemoveManagedBlock(claudePath, "# CLAUDE.md", BuildLegacyClaudeInstructionsContent(projectRoot, manifest));
                DeleteManagedSkillDirectories(projectRoot, skillsRoot, manifest);
                return;
            }

            Directory.CreateDirectory(skillsRoot);

            // Manage only the delimited begin..end block; hand-authored content elsewhere in
            // CLAUDE.md is preserved (created/appended if the file has no block yet).
            WriteManagedBlock(
                claudePath,
                "# CLAUDE.md",
                BuildClaudeManagedBlock(projectRoot, manifest),
                BuildLegacyClaudeInstructionsContent(projectRoot, manifest));

            WriteManagedSkillDirectories(projectRoot, skillsRoot, manifest, SkillPlatform.Claude, GetInstalledSkills());
        }

        private static void SyncCursor(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("cursor", StringComparer.OrdinalIgnoreCase);
            var rulesRoot = GetCursorRulesPath(projectRoot);

            if (!enabled)
            {
                DeleteManagedCursorRules(rulesRoot);
                return;
            }

            Directory.CreateDirectory(rulesRoot);
            WriteManagedCursorRules(rulesRoot, manifest);
        }

        private static void SyncAgents(string projectRoot, ProjectSkillsManifest manifest)
        {
            var enabled = manifest.platforms.Contains("agents", StringComparer.OrdinalIgnoreCase);
            var skillsRoot = GetAgentsSkillsRoot(projectRoot);

            if (!enabled)
            {
                DeleteManagedSkillDirectories(projectRoot, skillsRoot, manifest);
                return;
            }

            Directory.CreateDirectory(skillsRoot);
            WriteManagedSkillDirectories(projectRoot, skillsRoot, manifest, SkillPlatform.Agents, GetInstalledSkills());
        }

        // Files are written over the old ones and only then are stale ones removed, so a rewrite can keep
        // each file's BOM and line endings. manifest.managedFiles is the record of what this wrote.
        internal static void WriteManagedSkillDirectories(
            string projectRoot,
            string skillsRoot,
            ProjectSkillsManifest manifest,
            SkillPlatform platform,
            IEnumerable<SkillDefinition> skills)
        {
            var rootRelative = RelativeTo(projectRoot, skillsRoot);
            var previous = new HashSet<string>(manifest.managedFiles, StringComparer.Ordinal);
            var current = new HashSet<string>(StringComparer.Ordinal);

            foreach (var skill in skills)
            {
                var directory = Path.Combine(skillsRoot, skill.Id);
                var relativeDirectory = rootRelative + "/" + skill.Id;
                Directory.CreateDirectory(directory);
                ManagedTextFile.WriteGenerated(Path.Combine(directory, "SKILL.md"), BuildSkillDocument(skill, platform));
                current.Add(relativeDirectory + "/SKILL.md");

                foreach (var copied in CopySiblingFiles(skill, directory, relativeDirectory, previous))
                    current.Add(copied);
            }

            DeleteManagedSkillDirectories(projectRoot, skillsRoot, manifest, current);

            foreach (var relative in current)
            {
                if (!manifest.managedFiles.Contains(relative))
                    manifest.managedFiles.Add(relative);
            }

            manifest.managedFiles.Sort(StringComparer.Ordinal);
        }

        private static List<string> CopySiblingFiles(
            SkillDefinition skill,
            string directory,
            string relativeDirectory,
            ISet<string> previous)
        {
            var copied = new List<string>();
            if (string.IsNullOrEmpty(skill.SourceDirectory))
                return copied;

            var plan = PlanSiblingFiles(PackageSkillCatalog.RelativeFiles(skill.SourceDirectory), directory, out var refused);
            foreach (var path in refused)
                Debug.LogWarning($"[KitWright] Skill '{skill.Id}': '{path}' would leave the skill folder, so it was not copied.");

            foreach (var relative in plan)
            {
                var source = ResolveInside(skill.SourceDirectory, relative);
                var target = ResolveInside(directory, relative);
                if (source == null || target == null)
                    continue;

                var recorded = relativeDirectory + "/" + relative;
                var bytes = File.ReadAllBytes(source);
                if (File.Exists(target) && !previous.Contains(recorded) && !bytes.SequenceEqual(File.ReadAllBytes(target)))
                {
                    Debug.LogWarning($"[KitWright] Skill '{skill.Id}': '{target}' already exists and was not written by KitWright, so it was left as it is.");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, bytes);
                copied.Add(recorded);
            }

            return copied;
        }

        // Skill.md and the rest of a package skill's folder, as paths relative to it. A `.meta` file is the
        // AssetDatabase's, not the skill's. A path that would leave the destination is refused, not copied.
        internal static List<string> PlanSiblingFiles(
            IEnumerable<string> sourceRelativePaths,
            string destinationDirectory,
            out List<string> refused)
        {
            var plan = new List<string>();
            refused = new List<string>();

            foreach (var raw in sourceRelativePaths ?? Enumerable.Empty<string>())
            {
                var relative = (raw ?? string.Empty).Replace('\\', '/').Trim();
                if (relative.Length == 0 ||
                    relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(relative, "SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (relative.Split('/').Contains("..") || ResolveInside(destinationDirectory, relative) == null)
                {
                    refused.Add(raw);
                    continue;
                }

                if (!plan.Contains(relative, StringComparer.OrdinalIgnoreCase))
                    plan.Add(relative);
            }

            plan.Sort(StringComparer.Ordinal);
            return plan;
        }

        // Null when `relative` is empty, rooted, or resolves outside `root`.
        internal static string ResolveInside(string root, string relative)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative))
                return null;

            try
            {
                var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                               Path.DirectorySeparatorChar;
                var full = Path.GetFullPath(Path.Combine(root, relative));
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                return full.Length > rootFull.Length && full.StartsWith(rootFull, comparison) ? full : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string RelativeTo(string root, string path)
        {
            var relative = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? path.Substring(root.Length) : path;
            return relative.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
        }

        private static void WriteManagedCursorRules(string rulesRoot, ProjectSkillsManifest manifest)
        {
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var skill in GetInstalledSkills())
            {
                var name = $"kitwright-{skill.Id}.mdc";
                ManagedTextFile.WriteGenerated(Path.Combine(rulesRoot, name), BuildCursorRuleContent(skill));
                written.Add(name);
            }

            DeleteManagedCursorRules(rulesRoot, written);
        }

        // Only what the manifest lists is removed, and only a SKILL.md that still carries the managed marker
        // in its header: a folder is never deleted wholesale, because it may hold the user's own files.
        // `keep` holds the files a sync just wrote.
        private static void DeleteManagedSkillDirectories(
            string projectRoot,
            string skillsRoot,
            ProjectSkillsManifest manifest,
            ISet<string> keep = null)
        {
            var prefix = RelativeTo(projectRoot, skillsRoot) + "/";

            var listedFolders = manifest.managedFiles
                .Where(relative => relative.StartsWith(prefix, StringComparison.Ordinal) && relative.IndexOf('/', prefix.Length) > 0)
                .GroupBy(relative => relative.Substring(prefix.Length, relative.IndexOf('/', prefix.Length) - prefix.Length))
                .ToList();

            foreach (var folder in listedFolders)
            {
                var folderPath = Path.Combine(skillsRoot, folder.Key);
                var skillPath = Path.Combine(folderPath, "SKILL.md");
                var userTookOver = File.Exists(skillPath) && !IsManagedFile(skillPath);

                foreach (var relative in folder.ToList())
                {
                    if (keep != null && keep.Contains(relative))
                        continue;

                    manifest.managedFiles.Remove(relative);
                    var full = userTookOver ? null : ResolveInside(skillsRoot, relative.Substring(prefix.Length));
                    if (full != null && File.Exists(full))
                        File.Delete(full);
                }

                PruneEmptyDirectories(folderPath);
            }

            if (!Directory.Exists(skillsRoot))
                return;

            // Folders an earlier version wrote carry no record: the skill's own name, or the legacy
            // `kitwright-<id>` form of it, stands in for the listing.
            foreach (var directory in Directory.GetDirectories(skillsRoot))
            {
                var name = Path.GetFileName(directory);
                var skillPath = Path.Combine(directory, "SKILL.md");
                if (!IsManagedFile(skillPath) || !IsListedSkillFolder(name, manifest))
                    continue;

                if (keep != null && keep.Contains(prefix + name + "/SKILL.md", StringComparer.OrdinalIgnoreCase))
                    continue;

                File.Delete(skillPath);
                PruneEmptyDirectories(directory);
            }
        }

        private static bool IsListedSkillFolder(string name, ProjectSkillsManifest manifest)
        {
            return manifest.skillVersions.Any(entry =>
                string.Equals(name, entry.id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "kitwright-" + entry.id, StringComparison.OrdinalIgnoreCase));
        }

        private static void PruneEmptyDirectories(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (var child in Directory.GetDirectories(directory))
                PruneEmptyDirectories(child);

            if (Directory.GetFileSystemEntries(directory).Length == 0)
                Directory.Delete(directory);
        }

        private static void DeleteManagedCursorRules(string rulesRoot, ISet<string> keep = null)
        {
            if (!Directory.Exists(rulesRoot))
                return;

            foreach (var file in Directory.GetFiles(rulesRoot, "kitwright-*.mdc", SearchOption.TopDirectoryOnly))
            {
                if (keep != null && keep.Contains(Path.GetFileName(file)))
                    continue;

                if (IsManagedFile(file))
                    File.Delete(file);
            }
        }

        // Write `block` (which begins with ManagedMarker and ends with ManagedEndMarker) into a
        // shared root instructions file, managing ONLY the delimited region and preserving all
        // user content outside it:
        //   - file absent/empty        -> create `defaultTitle` + block
        //   - complete begin..end block -> replace just the block region in place
        //   - complete GameWright block -> same, a block an earlier release wrote
        //   - begin marker, no end      -> migrate an exact legacy generated file; otherwise stop
        //                                  with a manual-migration error rather than clobber content
        //   - no begin marker           -> append the block below the existing user content
        // A marker counts only when it is a line of its own, so prose that mentions one is left alone.
        private static void WriteManagedBlock(string path, string defaultTitle, string block, string legacyContent, bool dryRun = false)
        {
            var content = string.Empty;
            var bom = false;
            var newline = "\n";
            if (File.Exists(path) && !ManagedTextFile.TryRead(path, out content, out bom, out newline))
                throw new InvalidOperationException(ManagedTextFile.NotUtf8(path));

            var updated = ManagedBlockText.Upsert(content, block, defaultTitle, legacyContent, newline, out var refusal);
            if (refusal != null)
                throw new InvalidOperationException($"'{path}' {refusal}");

            if (!dryRun)
                ManagedTextFile.Write(path, updated, bom);
        }

        // Inverse of WriteManagedBlock, used when a platform is disabled: strip ONLY the managed
        // block and keep the user's content. If nothing but a bare title (or whitespace) remains,
        // the file was KitWright-only, so delete it. Exact legacy generated files can also be safely
        // deleted; edited legacy files are left untouched and reported for manual cleanup.
        private static void RemoveManagedBlock(string path, string defaultTitle, string legacyContent, bool dryRun = false)
        {
            if (!File.Exists(path))
                return;

            if (!ManagedTextFile.TryRead(path, out var content, out var bom, out var newline))
            {
                if (ManagedBlockText.MentionsMarkers(File.ReadAllText(path)))
                    throw new InvalidOperationException(ManagedTextFile.NotUtf8(path));

                return;
            }

            var updated = ManagedBlockText.Strip(content, defaultTitle, legacyContent, newline, out var refusal);
            if (refusal != null)
                throw new InvalidOperationException($"'{path}' {refusal}");

            if (dryRun)
                return;

            if (updated == null)
                File.Delete(path);
            else if (!string.Equals(updated, content, StringComparison.Ordinal))
                ManagedTextFile.Write(path, updated, bom);
        }

        private static List<SkillVersionEntry> BuildCurrentSkillVersionEntries(ProjectSkillsManifest manifest)
        {
            return GetInstalledSkills()
                .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                .Select(skill => new SkillVersionEntry { id = skill.Id, version = skill.Version })
                .ToList();
        }

        private static string BuildSkillVersionSignature(SkillDefinition skill)
        {
            return $"{skill.Id}@{skill.Version}";
        }

        private static string BuildSkillVersionMarker(SkillDefinition skill)
        {
            return $"{SkillVersionMarkerPrefix}{BuildSkillVersionSignature(skill)} -->";
        }

        private static string BuildProjectSkillVersionsSummary(IEnumerable<SkillDefinition> skills)
        {
            return string.Join(
                ", ",
                (skills ?? Array.Empty<SkillDefinition>())
                    .OrderBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(BuildSkillVersionSignature));
        }

        private static string BuildProjectSkillVersionsMarker(IEnumerable<SkillDefinition> skills)
        {
            return $"{ProjectSkillVersionsMarkerPrefix}{BuildProjectSkillVersionsSummary(skills)} -->";
        }

        private static List<ExpectedSkillVersionFile> GetExpectedVersionedFilesForPlatform(
            string projectRoot,
            ProjectSkillsManifest manifest,
            string platformId)
        {
            var skills = GetInstalledSkills().ToArray();
            var result = new List<ExpectedSkillVersionFile>();

            switch (platformId?.Trim().ToLowerInvariant())
            {
                case "codex":
                    AddProjectVersionFile(result, GetCodexAgentsPath(projectRoot), skills);
                    foreach (var skill in skills)
                    {
                        result.Add(new ExpectedSkillVersionFile(
                            Path.Combine(GetCodexSkillsRoot(projectRoot), skill.Id, "SKILL.md"),
                            skill.Id,
                            skill.Version,
                            BuildSkillVersionMarker(skill)));
                    }
                    break;
                case "claude":
                    AddProjectVersionFile(result, GetClaudeInstructionsPath(projectRoot), skills);
                    foreach (var skill in skills)
                    {
                        result.Add(new ExpectedSkillVersionFile(
                            Path.Combine(GetClaudeSkillsRoot(projectRoot), skill.Id, "SKILL.md"),
                            skill.Id,
                            skill.Version,
                            BuildSkillVersionMarker(skill)));
                    }
                    break;
                case "cursor":
                    foreach (var skill in skills)
                    {
                        result.Add(new ExpectedSkillVersionFile(
                            Path.Combine(GetCursorRulesPath(projectRoot), $"kitwright-{skill.Id}.mdc"),
                            skill.Id,
                            skill.Version,
                            BuildSkillVersionMarker(skill)));
                    }
                    break;
                case "agents":
                    foreach (var skill in skills)
                    {
                        result.Add(new ExpectedSkillVersionFile(
                            Path.Combine(GetAgentsSkillsRoot(projectRoot), skill.Id, "SKILL.md"),
                            skill.Id,
                            skill.Version,
                            BuildSkillVersionMarker(skill)));
                    }
                    break;
            }

            return result;
        }

        private static void AddProjectVersionFile(
            List<ExpectedSkillVersionFile> result,
            string path,
            IReadOnlyList<SkillDefinition> skills)
        {
            result.Add(new ExpectedSkillVersionFile(
                path,
                "project",
                BuildProjectSkillVersionsSummary(skills),
                BuildProjectSkillVersionsMarker(skills)));
        }

        private static SkillFileVersionStatus InspectVersionedFile(ExpectedSkillVersionFile expected)
        {
            if (!File.Exists(expected.Path))
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    "missing",
                    true,
                    false,
                    true);
            }

            string content;
            try
            {
                content = File.ReadAllText(expected.Path);
            }
            catch
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    "unreadable",
                    false,
                    true,
                    true);
            }

            var managed = expected.SkillId == "project"
                ? ManagedBlockText.HasBlockMarker(content)
                : ManagedBlockText.HasManagedHeader(content);
            if (!managed)
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    "unmanaged",
                    false,
                    true,
                    true);
            }

            if (expected.SkillId == "project")
            {
                var markers = ManagedBlockText.Scan(content, ManagedMarker, ManagedEndMarker);
                if (!markers.IsWellFormed && !markers.IsEmpty)
                {
                    return new SkillFileVersionStatus(
                        expected.Path,
                        expected.SkillId,
                        expected.ExpectedVersion,
                        markers.BeginCount == 1 && markers.EndCount == 0 ? "legacy marker" : "invalid markers",
                        false,
                        false,
                        true);
                }
            }

            if (content.Contains(expected.ExpectedMarker, StringComparison.Ordinal))
            {
                return new SkillFileVersionStatus(
                    expected.Path,
                    expected.SkillId,
                    expected.ExpectedVersion,
                    expected.ExpectedVersion,
                    false,
                    false,
                    false);
            }

            var installedVersion = expected.SkillId == "project"
                ? ExtractMarkerValue(content, ProjectSkillVersionsMarkerPrefix)
                : ExtractSkillVersion(content, expected.SkillId);
            if (string.IsNullOrEmpty(installedVersion))
                installedVersion = "unknown";

            return new SkillFileVersionStatus(
                expected.Path,
                expected.SkillId,
                expected.ExpectedVersion,
                installedVersion,
                false,
                false,
                true);
        }

        private static string ExtractSkillVersion(string content, string skillId)
        {
            var markerValue = ExtractMarkerValue(content, SkillVersionMarkerPrefix);
            if (string.IsNullOrEmpty(markerValue))
                return null;

            var prefix = skillId + "@";
            return markerValue.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? markerValue.Substring(prefix.Length)
                : markerValue;
        }

        private static string ExtractMarkerValue(string content, string markerPrefix)
        {
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(markerPrefix))
                return null;

            var start = content.IndexOf(markerPrefix, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += markerPrefix.Length;
            var end = content.IndexOf(" -->", start, StringComparison.Ordinal);
            if (end < 0)
                return null;

            return content.Substring(start, end - start).Trim();
        }

        private static string BuildCodexManagedBlock(string projectRoot, ProjectSkillsManifest manifest)
        {
            var installed = GetInstalledSkills();
            return
$@"{ManagedMarker}
{BuildProjectSkillVersionsMarker(installed)}

# KitWright MCP for Unity Project Guidance

{CodexManagedNotice}

## Installed project skills

{string.Join("\n", installed.Select(skill => $"- `{skill.Id}` v{skill.Version} - {skill.Description}"))}

## Codex workflow rules

- Prefer project-local KitWright skills under `.codex/skills/`.
- Use `execute_code` as the primary Unity automation tool. For new snippets, include `using KitWright.Editor.Tools.Scripting;`, implement `IKitWrightCommand`, and use `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` so changes participate in Undo automatically.
- Confirm the Unity project root, active scene, and real object/prefab/asset path before edits. Treat user-provided object names as hints, not paths.
- Inspect Unity objects through MCP before changing user-named scene or prefab targets. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Tool returns are structured JSON (`{{success, message, data}}` / `{{success: false, code, error, data}}`). Branch on `code`, not free-form text.
- Set component fields with `set_component_properties` — it picks up `[SerializeField] private` fields and accepts Object references as `{{""fileID"": <instanceId>}}` or `{{""assetPath"": ""Assets/...""}}`.
- Read editor state through dedicated tools (`get_selection`, `get_prefab_stage`, `get_tags`, `get_layers`, `get_build_settings`); use `execute_menu_item` before falling back to ad-hoc `execute_code`.
- Never edit `.unity`, `.prefab`, or `.asset` files with shell text tools or patches; use Unity MCP / Editor APIs for scenes, prefabs, and ScriptableObject assets.
- Save only the scene or prefab assets intentionally modified, then read back exact values.
- With default `core` exposure, use the focused workflow tools. With default `full` exposure, prefer specific MCP tools for simple editor operations.
- `execute_code` refreshes the asset database and waits for compilation before running. For other tools that depend on freshly compiled code, still call `request_recompile` after external script edits.
- In `execute_code`, null-guard every lookup and return explicit missing path/object/component messages; do not run self-healing fallback loops.
- For Unity object references, do not use `??=` for lazy rebinding; use explicit `if (field == null) field = Resolve();`.
- After code or resource edits, exit Play Mode if needed and call `request_recompile` first; it returns the compile errors unless it reports that Unity started importing or recompiling. Only then call `wait_for_compilation(force_refresh=false)`, which returns them too. Do not follow either with `get_compilation_errors`, and never use `force_refresh=false` without a `request_recompile` before it: on its own it reads the state from before the edits.
- Editing several `.cs` files: write them with your own file tools, then call `request_recompile` once. `create_script`, `edit_script`, `patch_script`, `edit_script_members` and `write_file` each refresh the asset database.
- Group independent reads into one `batch_execute` call instead of one round-trip each.
- `request_recompile` is rejected while Unity is in Play Mode. Call `exit_play_mode` first, then retry.
- `enter_play_mode` and a recompile reload the domain. In broker mode (the default) requests are held across the reload, so make the next call directly, with no poll loop. If the connection is refused instead (direct mode), poll the JSON-RPC `ping` method over HTTP (with native tools only, `get_reload_recovery_status`) until it answers. Never probe with `tools/list`: it returns the whole tool catalog.
- If recompilation triggers a domain reload or interrupts a request, treat the result as unknown until `get_reload_recovery_status`, compilation checks, and MCP readback confirm it.
- Avoid changing `Library/`, `Temp/`, `Logs/`, or `obj/`.

## Project

{ProjectRootLine}
- Product name: `{Application.productName}`

## Notes

- Re-run `KitWright > Project Skills` after changing selected skills or platforms.
{ManagedEndMarker}";
        }

        private static string BuildClaudeManagedBlock(string projectRoot, ProjectSkillsManifest manifest)
        {
            var installed = GetInstalledSkills();
            return
$@"{ManagedMarker}
{BuildProjectSkillVersionsMarker(installed)}

# KitWright MCP for Unity Project Guidance

{ClaudeManagedNotice}

## Installed skills

{string.Join("\n", installed.Select(skill => $"- `{skill.Id}` v{skill.Version} - {skill.Description}"))}

## Preferred workflow

- Use KitWright Unity tools for Unity editor state and automation.
- Use `execute_code` for non-trivial Unity orchestration. For new snippets, include `using KitWright.Editor.Tools.Scripting;`, implement `IKitWrightCommand`, and use `ctx.RegisterObjectCreation` / `RegisterObjectModification` / `DestroyObject` so changes participate in Undo and `ctx.Log` for traceable output.
- Confirm the Unity project root, active scene, and real object/prefab/asset path before edits. Treat user-provided object names as hints, not paths.
- Inspect Unity objects through MCP before changing user-named scene or prefab targets. Carry the returned `instanceId` into follow-up calls (`find_method=by_id`) instead of re-resolving by name.
- Tool returns are structured JSON (`{{success, message, data}}` / `{{success: false, code, error, data}}`). Branch on `code`, not free-form text.
- Set component fields with `set_component_properties` — it picks up `[SerializeField] private` fields and accepts Object references as `{{""fileID"": <instanceId>}}` or `{{""assetPath"": ""Assets/...""}}`.
- Read editor state through `get_selection`, `get_prefab_stage`, `get_tags`, `get_layers`, `get_build_settings`; try `execute_menu_item` before writing ad-hoc `execute_code`.
- Never edit `.unity`, `.prefab`, or `.asset` files with shell text tools or patches; use Unity MCP / Editor APIs for scenes, prefabs, and ScriptableObject assets.
- Save only the scene or prefab assets intentionally modified, then read back exact values.
- With default `core` exposure, use the focused workflow tools. With default `full` exposure, prefer specific MCP tools for simple editor operations.
- `execute_code` refreshes assets and waits for compilation before running. For other tools that depend on freshly compiled code, still call `request_recompile` after external script edits.
- In `execute_code`, null-guard every lookup and return explicit missing path/object/component messages; do not run self-healing fallback loops.
- For Unity object references, do not use `??=` for lazy rebinding; use explicit `if (field == null) field = Resolve();`.
- After code or resource edits, exit Play Mode if needed and call `request_recompile` first; it returns the compile errors unless it reports that Unity started importing or recompiling. Only then call `wait_for_compilation(force_refresh=false)`, which returns them too. Do not follow either with `get_compilation_errors`, and never use `force_refresh=false` without a `request_recompile` before it: on its own it reads the state from before the edits.
- Editing several `.cs` files: write them with your own file tools, then call `request_recompile` once. `create_script`, `edit_script`, `patch_script`, `edit_script_members` and `write_file` each refresh the asset database.
- Group independent reads into one `batch_execute` call instead of one round-trip each.
- `request_recompile` is rejected while Unity is in Play Mode. Call `exit_play_mode` first, then retry.
- `enter_play_mode` and a recompile reload the domain. In broker mode (the default) requests are held across the reload, so make the next call directly, with no poll loop. If the connection is refused instead (direct mode), poll the JSON-RPC `ping` method over HTTP (with native tools only, `get_reload_recovery_status`) until it answers. Never probe with `tools/list`: it returns the whole tool catalog.
- If domain reload interrupts a request, treat the result as unknown until `get_reload_recovery_status`, compilation checks, and MCP readback confirm it.
- Additional installed skills are available under `.claude/skills/`.

## Project

{ProjectRootLine}
- Product name: `{Application.productName}`
{ManagedEndMarker}";
        }

        private static string BuildLegacyCodexAgentsContent(string projectRoot, ProjectSkillsManifest manifest)
        {
            var block = RemoveManagedEndMarker(BuildCodexManagedBlock(projectRoot, manifest))
                .Replace(CodexManagedNotice, "This file is managed by KitWright MCP for Unity.")
                .Replace(ProjectRootLine, $"- Project root: `{projectRoot}`");
            return "# AGENTS.md\n" + block + "\n";
        }

        private static string BuildLegacyClaudeInstructionsContent(string projectRoot, ProjectSkillsManifest manifest)
        {
            var block = RemoveManagedEndMarker(BuildClaudeManagedBlock(projectRoot, manifest))
                .Replace(ClaudeManagedNotice, "This file is managed by KitWright MCP for Unity for Claude Code.")
                .Replace(ProjectRootLine, $"- Project root: `{projectRoot}`");
            return "# CLAUDE.md\n" + block + "\n";
        }

        private static string RemoveManagedEndMarker(string block)
        {
            var suffix = "\n" + ManagedEndMarker;
            return block != null && block.EndsWith(suffix, StringComparison.Ordinal)
                ? block.Substring(0, block.Length - suffix.Length)
                : block;
        }

        internal static string BuildCursorRuleContent(SkillDefinition skill)
        {
            var alwaysApply = skill.IsBuiltIn ? "true" : "false";

            if (!string.IsNullOrEmpty(skill.Body))
                return
$@"---
description: {PackageSkillCatalog.YamlScalar(skill.Description)}
alwaysApply: {alwaysApply}
version: {skill.Version}
---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

{skill.Body}
";

            return
$@"---
description: {skill.Description}
alwaysApply: {alwaysApply}
version: {skill.Version}
---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

# {skill.Title}

{skill.WhenToUse}

## Rules

{string.Join("\n", skill.Rules.Select(rule => $"- {rule}"))}

## Metadata

- Skill id: `{skill.Id}`
- Skill version: `{skill.Version}`
- Built-in: `{skill.IsBuiltIn}`
- Source: `https://github.com/kitwright/unity-mcp`
";
        }

        private static string ExtraFrontMatterLines(SkillDefinition skill)
        {
            return string.IsNullOrEmpty(skill.ExtraFrontMatter) ? string.Empty : skill.ExtraFrontMatter + "\n";
        }

        internal static string BuildSkillDocument(SkillDefinition skill, SkillPlatform platform)
        {
            if (string.Equals(skill.Id, "unity-mcp-workflow", StringComparison.OrdinalIgnoreCase))
                return BuildUnityMcpWorkflowSkillDocument(skill, platform);

            if (!string.IsNullOrEmpty(skill.Body))
                return
$@"---
name: {skill.Id}
description: {PackageSkillCatalog.YamlScalar(skill.Description)}
version: {skill.Version}
platform: {platform.ToString().ToLowerInvariant()}
{ExtraFrontMatterLines(skill)}---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

{skill.Body}
";

            return
$@"---
name: {skill.Id}
description: {skill.Description}
version: {skill.Version}
platform: {platform.ToString().ToLowerInvariant()}
---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

# {skill.Title}

{skill.WhenToUse}

## Rules

{string.Join("\n", skill.Rules.Select(rule => $"- {rule}"))}

## Metadata

- Original skill id: `{skill.Id}`
- Skill version: `{skill.Version}`
- Source repository: `https://github.com/kitwright/unity-mcp`
";
        }

        private static string BuildUnityMcpWorkflowSkillDocument(SkillDefinition skill, SkillPlatform platform)
        {
            var header =
$@"---
name: {skill.Id}
description: {skill.Description}
version: {skill.Version}
platform: {platform.ToString().ToLowerInvariant()}
---
{ManagedMarker}
{BuildSkillVersionMarker(skill)}

# {skill.Title}

{skill.WhenToUse}
";

            var body =
@"
## Operating Loop

1. Establish context.
   - Confirm the Unity project root and active scene.
   - Check that Unity MCP is reachable before assuming Editor state.
   - Inspect hierarchy, prefab paths, selected objects, and relevant component references through MCP.
   - If the user names an object, treat the name as a hint and verify the real Unity object path before editing.
   - Group independent reads into one `batch_execute` call instead of one round-trip each.
2. Choose the edit surface.
   - Edit source files with normal repo tools, then trigger Unity recompilation.
   - Edit scene objects through Unity APIs, mark the scene dirty, and save the scene.
   - Edit prefab assets with `PrefabUtility.LoadPrefabContents`, `PrefabUtility.SaveAsPrefabAsset`, and `PrefabUtility.UnloadPrefabContents`.
   - Edit ScriptableObject assets through `SerializedObject`, `EditorUtility.SetDirty`, and `AssetDatabase.SaveAssetIfDirty` / `SaveAssets`.
   - Never patch `.unity`, `.prefab`, or `.asset` YAML with shell text tools.
   - If the user is looking at an open scene instance, update the visible scene instance as well as the prefab asset when appropriate.
3. Execute changes.
   - Prefer one well-guarded `execute_code` batch over many fragile UI clicks.
   - Use null guards for every object, component, asset, and path lookup.
   - Return explicit missing-path/object/component messages that include the expected path and the scene or prefab searched.
   - Return concise before/after values from snippets.
   - Save only the assets or scenes intentionally modified.
   - Do not run self-healing fallback loops; if a reference, path, package, or tool is missing, report it once and stop or skip that item.
4. Validate.
   - Read back the changed objects through MCP.
   - For code or resource edits, exit Play Mode if needed and follow Recompile And Reload below: `request_recompile`, then `wait_for_compilation(force_refresh=false)` only if it reports a started import or compile. Both return the compile errors, so do not follow them with `get_compilation_errors`; inspect console errors.
   - For runtime behavior, enter Play Mode or inspect live objects when needed.
   - If MCP is unreachable, do not claim scene, prefab, asset, or runtime verification.
   - Report exactly what was verified and what still requires device, store, network, or manual validation.

## Unity Serialized Asset Safety

- Do not use shell text tools, scripts, or patches to modify `.unity`, `.prefab`, or `.asset` files. These are Unity-owned serialized assets; changing them outside Unity can corrupt file IDs, prefab overrides, references, import state, or scene dirtiness.
- Shell tools may inspect or locate serialized Unity assets, but scene, prefab, and ScriptableObject modifications must go through Unity MCP tools or Editor APIs.
- For scenes, modify live objects through Unity APIs, mark only the touched scene dirty, and save that scene.
- For prefabs, use Prefab Mode tools or `PrefabUtility.LoadPrefabContents` / `SaveAsPrefabAsset` / `UnloadPrefabContents`.
- For ScriptableObjects or other `.asset` files, load the asset with `AssetDatabase`, modify serialized properties through `SerializedObject` when possible, mark that asset dirty, and save only that asset.
- If Unity readback and raw file text disagree, trust Unity readback and investigate the asset path instead of hand-editing YAML.

## Tool Exposure

- With the default `core` profile, rely on the focused workflow tools: `execute_code`, recompilation, Play Mode control, hierarchy, console logs, screenshots, input simulation, and performance inspection.
- With the default `full` profile, prefer specific MCP tools for simple scene, asset, GameObject, component, prefab, camera, UI, package, animation, file, or visual-feedback operations.
- If Tool Exposure is customized and a named tool is unavailable, adapt to the exposed tool list and report which expected tool is missing.

## MCP Call Pattern

If native MCP tools are not directly available, probe the local HTTP endpoint. Take its URL from the `kitwright` entry in this project's MCP client config (`.mcp.json`, `.codex/config.toml`, `.gemini/settings.json`, ...). It is not written here because it carries this editor's access token: a skill file gets committed, and the token must not be.

```bash
URL='<url of the kitwright entry>'
curl -sS -m 1 -X POST ""${URL}mcp"" \
  -H 'Content-Type: application/json' \
  -d '{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}'
```

Probe with `ping`, never with `tools/list`: that returns the whole tool catalog, tens of KB per probe. Call `tools/list` once, only when you need the tool schemas.

That URL carries this project's pin and access token, and the editor rewrites it to the live
port on every start. A URL without the token is refused with 401. A pinless or stale-port URL
is answered by whichever editor happens to hold the port, which may be a different Unity project.

For multi-line `execute_code` calls over curl, generate JSON with a real encoder instead of hand-escaping C#:

```bash
node - <<'NODE'
const code = String.raw`
using UnityEngine;

public class InspectSomething
{
    public static string Run()
    {
        var obj = GameObject.Find(""PracticeInGameUiRoot"");
        return obj != null ? obj.name : ""not found"";
    }
}
`;
const payload = {
  jsonrpc: ""2.0"",
  id: 1,
  method: ""tools/call"",
  params: { name: ""execute_code"", arguments: { code } }
};
process.stdout.write(JSON.stringify(payload));
NODE
```

## Recommended `execute_code` Template

For non-trivial snippets, prefer `IKitWrightCommand` over the legacy `public static string Run()` template. `execute_code` auto-adds `using KitWright.Editor.Tools.Scripting;` when `IKitWrightCommand` is used, but include it explicitly in generated snippets for readability:

```csharp
using KitWright.Editor.Tools.Scripting;
using UnityEngine;

public class CommandScript : IKitWrightCommand
{
    public void Execute(ExecutionContext ctx)
    {
        var root = GameObject.Find(""PracticeInGameUiRoot"");
        if (root == null)
        {
            ctx.LogWarning(""PracticeInGameUiRoot not found"");
            ctx.ReturnValue = ""missing root"";
            return;
        }

        ctx.RegisterObjectModification(root);
        ctx.Log(""Found {0}, active={1}"", root.name, root.activeInHierarchy);
        ctx.ReturnValue = new
        {
            name = root.name,
            active = root.activeInHierarchy
        };
    }
}
```

Use `ctx.RegisterObjectCreation(obj)`, `ctx.RegisterObjectModification(obj)`, and `ctx.DestroyObject(obj)` instead of direct Undo calls when possible. Use `ctx.Log`, `ctx.LogWarning`, and `ctx.LogError` for output returned in the MCP response without polluting the Unity Console.

## Unity C# Patterns

Add explicit `using` directives or use fully qualified types for project code. `execute_code` does not auto-inject project namespaces by default:

```csharp
var root = UnityEngine.GameObject.Find(""PracticeInGameUiRoot"");
var rect = root.GetComponent<UnityEngine.RectTransform>();
```

Use Unity null semantics for `UnityEngine.Object` references:

```csharp
if (image == null)
{
    return ""Image missing"";
}
```

Do not use `??=` to lazily resolve or rebind `UnityEngine.Object` references. Unity's destroyed or unbound serialized references can be fake-null: `field == null` returns true through Unity's overloaded operator, while C# `??=` can still treat the managed wrapper as non-null and skip the fallback assignment. Use an explicit Unity-null check instead:

```csharp
if (_hud == null)
{
    _hud = GetComponentInChildren<MyHud>(true);
}
```

For prefab edits:

```csharp
var path = ""Assets/MyGame/UI/Prefabs/PF_PracticeInGameUiRoot.prefab"";
var prefab = UnityEditor.PrefabUtility.LoadPrefabContents(path);
try
{
    var target = prefab.transform.Find(""SafeArea/SwingCancelZone"");
    if (target == null)
    {
        return ""SwingCancelZone not found in prefab"";
    }

    var rect = target.GetComponent<UnityEngine.RectTransform>();
    var before = rect.anchoredPosition;
    rect.anchoredPosition = new UnityEngine.Vector2(-76f, 448f);

    UnityEditor.EditorUtility.SetDirty(rect);
    UnityEditor.PrefabUtility.SaveAsPrefabAsset(prefab, path);
    UnityEditor.AssetDatabase.SaveAssets();
    return ""Prefab saved: pos "" + before + "" -> "" + rect.anchoredPosition;
}
finally
{
    UnityEditor.PrefabUtility.UnloadPrefabContents(prefab);
}
```

For scene edits:

```csharp
var obj = UnityEngine.GameObject.Find(""PracticeInGameUiRoot/SafeArea/SwingCancelZone"");
if (obj == null)
{
    return ""Scene object not found"";
}

var rect = obj.GetComponent<UnityEngine.RectTransform>();
var before = rect.sizeDelta;
UnityEditor.Undo.RecordObject(rect, ""Update cancel zone"");
rect.sizeDelta = new UnityEngine.Vector2(220f, 116f);
UnityEditor.EditorUtility.SetDirty(rect);
UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(obj.scene);
return ""Scene dirtied: size "" + before + "" -> "" + rect.sizeDelta + "" (call save_scene to write it)"";
```

## Recompile And Reload

After external C# or asset file edits:

1. If Unity is in Play Mode, call `exit_play_mode` first — `request_recompile` is rejected during play because Unity does not run script compilation or domain reloads while playing.
2. Call `request_recompile`, always first: it is the call that imports the edits. Unless it reports that Unity started importing or recompiling, its answer already carries the compile result (`COMPILATION_FAILED` with the issues, or no errors).
3. Only if it reported that Unity started importing or recompiling, call `wait_for_compilation` with `force_refresh=false`: the refresh already ran, and its answer carries the compile errors too. Never use `force_refresh=false` without step 2 before it — on its own it reads the state from before the edits.
4. Do not follow step 2 or 3 with `get_compilation_errors`. Read `get_console_logs` errors before continuing.
5. If a domain reload drops or interrupts the request, call `get_reload_recovery_status` when available, re-scan the MCP endpoint if needed, then continue from step 3.

Editing several `.cs` files: write all of them with your own file tools, then run this sequence once. `create_script`, `edit_script`, `patch_script`, `edit_script_members` and `write_file` each refresh the asset database, so writing N files through them starts up to N imports and compiles.

Do not treat a disconnected, interrupted, or domain-reload-recovered request as a successful compile or edit. It only means the state is unknown until compilation checks and MCP readback confirm the final values.

`enter_play_mode` and a recompile both reload the domain. In broker mode (the default) the broker holds requests across the reload and delivers them once Unity is back, so make the next call directly — no poll loop is needed (over curl, give that call a timeout of a minute rather than `-m 1`). A call answered with ""Unity is recompiling scripts, so this tool did not run"" is simply repeated. If the connection is refused instead (direct mode), poll `ping` as in MCP Call Pattern (with native tools only, `get_reload_recovery_status`) until it answers.

## Verification Checklist

Use readback snippets that print exact values, not only `success`:

```csharp
var all = UnityEngine.Resources.FindObjectsOfTypeAll<UnityEngine.Transform>();
UnityEngine.Transform target = null;
for (int i = 0; i < all.Length; i++)
{
    if (all[i].name == ""SwingCancelZone"")
    {
        target = all[i];
        break;
    }
}

if (target == null)
{
    return ""SwingCancelZone not found"";
}

var rect = target.GetComponent<UnityEngine.RectTransform>();
return ""path="" + target.name + ""; pos="" + rect.anchoredPosition + ""; size="" + rect.sizeDelta;
```

For UI work, verify prefab or scene hierarchy, sprite references, anchors, sorting order, active state, text fit, and button listeners. A populated `Content` hierarchy does not prove the user can see the UI.

For gameplay or network work, verify object identity, ownership, live instance existence, transform values, animation state, visibility, and whether client-side filters are discarding valid data.

## Failure Handling

- If MCP is unreachable, say so and fall back only to safe filesystem inspection or code edits. Do not claim scene, prefab, or runtime verification without Unity readback.
- If an object lookup fails, inspect hierarchy and prefab contents instead of inventing a path.
- If multiple matching objects exist, print their paths and choose the one matching the user-visible UI or current scene.
- If a reference, package, tool, or path is missing, return one clear error and stop or skip that item. Do not loop through guessed fallback paths, create replacement objects silently, or report success after a best-effort fallback.
- If compile errors appear after a change, fix them before Play Mode validation.
- When Unity and text files disagree for serialized scene or prefab state, trust Unity readback and inspect the asset path.
";

            var footer =
$@"
## Metadata

- Original skill id: `{skill.Id}`
- Skill version: `{skill.Version}`
- Source repository: `https://github.com/kitwright/unity-mcp`
";

            return header + body + footer;
        }

        private static ProjectSkillsManifest CreateDefaultManifest()
        {
            return new ProjectSkillsManifest
            {
                platforms = new List<string>()
            };
        }

        private static ProjectSkillsManifest NormalizeManifest(ProjectSkillsManifest manifest)
        {
            manifest ??= CreateDefaultManifest();
            manifest.platforms ??= new List<string>();
            manifest.skillVersions ??= new List<SkillVersionEntry>();
            manifest.managedFiles ??= new List<string>();

            manifest.managedFiles = manifest.managedFiles
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().Replace('\\', '/'))
                .Where(value => !Path.IsPathRooted(value) && !value.Split('/').Contains(".."))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            manifest.platforms = manifest.platforms
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => SupportedPlatforms.Contains(value, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var installedIds = new HashSet<string>(
                GetInstalledSkills().Select(skill => skill.Id),
                StringComparer.OrdinalIgnoreCase);

            manifest.skillVersions = manifest.skillVersions
                .Where(entry => entry != null &&
                                !string.IsNullOrWhiteSpace(entry.id) &&
                                !string.IsNullOrWhiteSpace(entry.version) &&
                                installedIds.Contains(entry.id))
                .GroupBy(entry => entry.id.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    return new SkillVersionEntry { id = group.Key, version = first.version.Trim() };
                })
                .OrderBy(entry => entry.id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return manifest;
        }

        internal enum SkillPlatform
        {
            Codex,
            Claude,
            Cursor,
            Agents
        }

        [Serializable]
        internal sealed class ProjectSkillsManifest
        {
            public List<string> platforms = new List<string>();
            public List<SkillVersionEntry> skillVersions = new List<SkillVersionEntry>();

            // Every file a sync wrote under a skills folder, relative to the project root.
            public List<string> managedFiles = new List<string>();
        }

        [Serializable]
        internal sealed class SkillVersionEntry
        {
            public string id;
            public string version;
        }

        internal sealed class SkillDefinition
        {
            public SkillDefinition(string id, string version, string title, string description, bool isBuiltIn, string whenToUse, IReadOnlyList<string> rules, string body = null, string extraFrontMatter = null, string sourceDirectory = null)
            {
                Id = id;
                Version = version;
                Title = title;
                Description = description;
                IsBuiltIn = isBuiltIn;
                WhenToUse = whenToUse;
                Rules = rules ?? Array.Empty<string>();
                Body = body;
                ExtraFrontMatter = extraFrontMatter;
                SourceDirectory = sourceDirectory;
            }

            public string Id { get; }
            public string Version { get; }
            public string Title { get; }
            public string Description { get; }
            public bool IsBuiltIn { get; }
            public string WhenToUse { get; }
            public IReadOnlyList<string> Rules { get; }

            /// <summary>Ready-made skill text. When set, it is written verbatim instead of
            /// generating a document from WhenToUse and Rules.</summary>
            public string Body { get; }

            /// <summary>Front matter lines the skill's author wrote beyond name, description, version and
            /// platform, copied into the installed SKILL.md as they were.</summary>
            public string ExtraFrontMatter { get; }

            /// <summary>The folder a package skill was read from; its files beside SKILL.md are installed with it.</summary>
            public string SourceDirectory { get; }
        }

        private sealed class ExpectedSkillVersionFile
        {
            public ExpectedSkillVersionFile(string path, string skillId, string expectedVersion, string expectedMarker)
            {
                Path = path;
                SkillId = skillId;
                ExpectedVersion = expectedVersion;
                ExpectedMarker = expectedMarker;
            }

            public string Path { get; }
            public string SkillId { get; }
            public string ExpectedVersion { get; }
            public string ExpectedMarker { get; }
        }

        internal sealed class ProjectSkillsUpgradeStatus
        {
            public ProjectSkillsUpgradeStatus(IReadOnlyList<SkillFileVersionStatus> files)
            {
                Files = files ?? Array.Empty<SkillFileVersionStatus>();
                HasUpdates = Files.Any(file => file.RequiresUpgrade);
            }

            public IReadOnlyList<SkillFileVersionStatus> Files { get; }
            public bool HasUpdates { get; }
        }

        internal sealed class SkillFileVersionStatus
        {
            public SkillFileVersionStatus(
                string path,
                string skillId,
                string expectedVersion,
                string installedVersion,
                bool missing,
                bool unmanaged,
                bool requiresUpgrade)
            {
                Path = path;
                SkillId = skillId;
                ExpectedVersion = expectedVersion;
                InstalledVersion = installedVersion;
                Missing = missing;
                Unmanaged = unmanaged;
                RequiresUpgrade = requiresUpgrade;
            }

            public string Path { get; }
            public string SkillId { get; }
            public string ExpectedVersion { get; }
            public string InstalledVersion { get; }
            public bool Missing { get; }
            public bool Unmanaged { get; }
            public bool RequiresUpgrade { get; }
        }
    }

    internal static class ManagedBlockText
    {
        internal const string LegacyBeginMarker = "<!-- GameWright Unity managed project skills -->";
        internal const string LegacyEndMarker = "<!-- /GameWright Unity managed project skills -->";

        internal readonly struct Markers
        {
            public Markers(int beginCount, int endCount, int start, int end)
            {
                BeginCount = beginCount;
                EndCount = endCount;
                Start = start;
                End = end;
            }

            public int BeginCount { get; }
            public int EndCount { get; }

            /// Offset of the begin line, and of the end of the end line: the region to replace.
            public int Start { get; }
            public int End { get; }

            /// Exactly one begin line followed by exactly one end line: the only shape safe to rewrite in place.
            public bool IsWellFormed => Start >= 0;
            public bool IsEmpty => BeginCount == 0 && EndCount == 0;
        }

        // A marker is a line of its own (indentation allowed); inside a sentence or a code span it is text.
        internal static Markers Scan(string content, string begin, string end)
        {
            var begins = WholeLines(content, begin);
            var ends = WholeLines(content, end);
            var ordered = begins.Count == 1 && ends.Count == 1 && ends[0].Index > begins[0].Index;
            return new Markers(
                begins.Count,
                ends.Count,
                ordered ? begins[0].Index : -1,
                ordered ? ends[0].Index + ends[0].Length : -1);
        }

        private static MatchCollection WholeLines(string content, string marker)
        {
            return Regex.Matches(
                content ?? string.Empty,
                "^[ \\t]*" + Regex.Escape(marker) + "[ \\t]*(?=\\r?$)",
                RegexOptions.Multiline);
        }

        internal static bool HasBlockMarker(string content)
        {
            return Scan(content, ProjectSkillsManager.ManagedMarker, ProjectSkillsManager.ManagedEndMarker).BeginCount > 0 ||
                   Scan(content, LegacyBeginMarker, LegacyEndMarker).BeginCount > 0;
        }

        internal static bool MentionsMarkers(string content)
        {
            return content != null &&
                   (content.Contains(ProjectSkillsManager.ManagedMarker, StringComparison.Ordinal) ||
                    content.Contains(ProjectSkillsManager.ManagedEndMarker, StringComparison.Ordinal) ||
                    content.Contains(LegacyBeginMarker, StringComparison.Ordinal) ||
                    content.Contains(LegacyEndMarker, StringComparison.Ordinal));
        }

        // A generated skill file or rule opens with the marker, after its front matter if it has any.
        internal static bool HasManagedHeader(string content)
        {
            var lines = (content ?? string.Empty).TrimStart('\uFEFF').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var index = 0;
            if (lines[0].Trim() == "---")
            {
                index = 1;
                while (index < lines.Length && lines[index].Trim() != "---")
                    index++;

                index++;
            }

            for (; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.Length == 0)
                    continue;

                return line == ProjectSkillsManager.ManagedMarker || line == LegacyBeginMarker;
            }

            return false;
        }

        // The file's new text, or null with `refusal` set (written for "'<path>' <refusal>") when its markers
        // are duplicated, unmatched or out of order. `content` is empty for a file that does not exist yet.
        internal static string Upsert(
            string content,
            string block,
            string defaultTitle,
            string legacyContent,
            string newline,
            out string refusal)
        {
            refusal = null;
            content = content ?? string.Empty;
            block = ManagedTextFile.ToNewline(block, newline);

            var kit = Scan(content, ProjectSkillsManager.ManagedMarker, ProjectSkillsManager.ManagedEndMarker);
            var old = Scan(content, LegacyBeginMarker, LegacyEndMarker);

            if (kit.BeginCount == 1 && kit.EndCount == 0 && old.IsEmpty)
            {
                if (ManagedTextEquals(content, legacyContent))
                    return defaultTitle + newline + newline + block + newline;

                refusal =
                    $"contains a legacy KitWright begin marker without an end marker and no longer exactly matches the known generated file. No content was changed. Preserve any hand-authored text, then either remove the legacy begin marker so KitWright can append a new block, or add '{ProjectSkillsManager.ManagedEndMarker}' immediately after the KitWright-managed section.";
                return null;
            }

            if (!IsConsistent(kit) || !IsConsistent(old) || Overlap(kit, old))
            {
                refusal = $"contains duplicate, unmatched, or out-of-order KitWright managed markers. No content was changed. Keep exactly one '{ProjectSkillsManager.ManagedMarker}' line followed by one '{ProjectSkillsManager.ManagedEndMarker}' line.";
                return null;
            }

            if (kit.IsWellFormed && old.IsWellFormed)
            {
                content = RemoveRegion(content, old.Start, old.End);
                kit = Scan(content, ProjectSkillsManager.ManagedMarker, ProjectSkillsManager.ManagedEndMarker);
            }

            var target = kit.IsWellFormed ? kit : old;
            if (target.IsWellFormed)
                return content.Substring(0, target.Start) + block + content.Substring(target.End);

            var trimmed = content.TrimEnd('\n', '\r', ' ', '\t');
            var userContent = trimmed.Length == 0 ? defaultTitle : trimmed;
            return userContent + newline + newline + block + newline;
        }

        // The text without the managed block(s), null when nothing but a bare title would remain (delete the
        // file), or null with `refusal` set. The input itself comes back when there is nothing to remove.
        internal static string Strip(string content, string defaultTitle, string legacyContent, string newline, out string refusal)
        {
            refusal = null;
            content = content ?? string.Empty;

            var kit = Scan(content, ProjectSkillsManager.ManagedMarker, ProjectSkillsManager.ManagedEndMarker);
            var old = Scan(content, LegacyBeginMarker, LegacyEndMarker);

            if (kit.IsEmpty && old.IsEmpty)
                return content;

            if (kit.BeginCount == 1 && kit.EndCount == 0 && old.IsEmpty)
            {
                if (ManagedTextEquals(content, legacyContent))
                    return null;

                refusal = "contains an edited legacy KitWright file with no end marker. It was left untouched while disabling Project Skills. Preserve any hand-authored text and remove the stale KitWright section manually.";
                return null;
            }

            if (!IsConsistent(kit) || !IsConsistent(old) || Overlap(kit, old))
            {
                refusal = "contains duplicate, unmatched, or out-of-order KitWright managed markers. It was left untouched while disabling Project Skills.";
                return null;
            }

            var result = content;
            foreach (var region in new[] { kit, old }.Where(found => found.IsWellFormed).OrderByDescending(found => found.Start))
                result = result.Substring(0, region.Start).TrimEnd() + newline + result.Substring(region.End).TrimStart('\n', '\r');

            var remaining = result.Trim();
            return remaining.Length == 0 || remaining == defaultTitle.Trim() ? null : result;
        }

        private static bool IsConsistent(Markers markers) => markers.IsWellFormed || markers.IsEmpty;

        private static bool Overlap(Markers first, Markers second)
        {
            return first.IsWellFormed && second.IsWellFormed && first.Start < second.End && second.Start < first.End;
        }

        private static string RemoveRegion(string content, int start, int end)
        {
            if (end < content.Length && content[end] == '\r')
                end++;
            if (end < content.Length && content[end] == '\n')
                end++;

            return content.Remove(start, end - start);
        }

        internal static bool ManagedTextEquals(string left, string right)
        {
            return string.Equals(NormalizeManagedText(left), NormalizeManagedText(right), StringComparison.Ordinal);
        }

        private static string NormalizeManagedText(string value)
        {
            return (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .TrimEnd('\n', ' ', '\t');
        }
    }

    // CLAUDE.md, AGENTS.md and skill files belong to the user's repo: a rewrite keeps a file's BOM and its
    // dominant line ending, and a file that is not UTF-8 is refused rather than decoded lossily and corrupted.
    internal static class ManagedTextFile
    {
        private static readonly UTF8Encoding Strict = new UTF8Encoding(false, true);

        internal static string Decode(byte[] bytes, out bool bom, out string newline)
        {
            bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip = bom ? 3 : 0;
            var text = Strict.GetString(bytes, skip, bytes.Length - skip);
            newline = DominantNewline(text);
            return text;
        }

        internal static string DominantNewline(string text)
        {
            var crlf = 0;
            var lf = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n')
                    continue;

                if (i > 0 && text[i - 1] == '\r')
                    crlf++;
                else
                    lf++;
            }

            return crlf > lf ? "\r\n" : "\n";
        }

        internal static bool TryRead(string path, out string text, out bool bom, out string newline)
        {
            try
            {
                text = Decode(File.ReadAllBytes(path), out bom, out newline);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = null;
                bom = false;
                newline = "\n";
                return false;
            }
        }

        internal static string NotUtf8(string path)
        {
            return $"'{path}' is not valid UTF-8, so KitWright did not touch it. Convert the file to UTF-8, then configure again.";
        }

        internal static string ToNewline(string text, string newline)
        {
            var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            return newline == "\n" ? normalized : normalized.Replace("\n", newline);
        }

        internal static byte[] Encode(string text, bool bom)
        {
            var body = new UTF8Encoding(false).GetBytes(text);
            if (!bom)
                return body;

            var bytes = new byte[body.Length + 3];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            Buffer.BlockCopy(body, 0, bytes, 3, body.Length);
            return bytes;
        }

        internal static void Write(string path, string text, bool bom)
        {
            File.WriteAllBytes(path, Encode(text, bom));
        }

        // A generated file takes the BOM and line ending of the file it replaces; a new one is LF without BOM.
        internal static void WriteGenerated(string path, string text)
        {
            var bom = false;
            var newline = "\n";
            if (File.Exists(path))
                TryRead(path, out _, out bom, out newline);

            Write(path, ToNewline(text, newline), bom);
        }
    }
}
