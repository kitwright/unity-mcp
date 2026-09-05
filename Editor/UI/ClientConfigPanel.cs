// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KitWright.Editor.Services;
using KitWright.Editor.Settings;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class ClientConfigPanel
    {
        private readonly SettingsController _settings;
        private readonly MCPServerService _server;
        private readonly Action _rebuildWindow;
        private MCPConfigTarget[] _targets;
        private int _selectedTargetIndex;
        private Label _configStatusLabel;
        private Label _configProblemLabel;
        private Label _configPathLabel;
        private const string ScopeGlobalKey = "KitWright.MCP.ConfigScopeGlobal";
        private Label _configResultLabel;

        public ClientConfigPanel(
            SettingsController settings,
            MCPServerService server,
            Action rebuildWindow)
        {
            _settings = settings;
            _server = server;
            _rebuildWindow = rebuildWindow;
        }

        public void AddTo(VisualElement parent)
        {
            var foldout = new Foldout { text = "Client Configuration", value = true }.Persist("ClientConfig");
            foldout.style.minWidth = 0;
            foldout.contentContainer.style.minWidth = 0;

            var toggle = foldout.Q<Toggle>();
            var toggleLabel = toggle?.Q<Label>();
            if (toggleLabel != null)
            {
                toggleLabel.style.fontSize = 12;
                toggleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
                toggleLabel.style.color = MCPPalette.HeadingBlue;
                toggleLabel.style.flexGrow = 1;
            }

            _configProblemLabel = new Label();
            _configProblemLabel.style.fontSize = 12;
            _configProblemLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _configProblemLabel.style.color = new Color(0.95f, 0.75f, 0.3f);
            _configProblemLabel.style.marginRight = 8;
            _configProblemLabel.Ellipsize();

            _configStatusLabel = new Label();
            _configStatusLabel.style.fontSize = 13;
            _configStatusLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _configStatusLabel.style.marginRight = 0;
            _configStatusLabel.style.flexShrink = 0;
            if (toggle != null)
            {
                toggle.style.marginRight = 0;
                toggle.Add(_configProblemLabel);
                toggle.Add(_configStatusLabel);
            }

            parent.Add(foldout);
            var body = foldout;

            var subHeaderRow = new VisualElement();
            subHeaderRow.style.flexDirection = FlexDirection.Row;
            subHeaderRow.style.alignItems = Align.Center;
            subHeaderRow.style.marginBottom = 6;

            var label = new Label("One-Click MCP Configuration");
            label.style.fontSize = 13;
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.color = new Color(0.75f, 0.75f, 0.75f);
            label.style.flexShrink = 0;
            subHeaderRow.Add(label);

            _configPathLabel = new Label();
            _configPathLabel.style.fontSize = 11;
            _configPathLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            _configPathLabel.style.unityTextAlign = TextAnchor.MiddleRight;
            _configPathLabel.style.flexGrow = 1;
            _configPathLabel.Ellipsize();
            _configPathLabel.style.marginLeft = 8;
            subHeaderRow.Add(_configPathLabel);

            body.Add(subHeaderRow);

            var homePath = GetUserHomePath();
            _targets = CreateTargets(homePath, EditorPrefs.GetBool(ScopeGlobalKey, false));
            var names = _targets.Select(target => target.Name).ToList();

            _selectedTargetIndex = Mathf.Clamp(_selectedTargetIndex, 0, _targets.Length - 1);
            var persistedTargetName = _settings.MCPSelectedConfigTarget;
            if (!string.IsNullOrWhiteSpace(persistedTargetName))
            {
                var persistedIndex = names.FindIndex(name =>
                    string.Equals(name, persistedTargetName, StringComparison.OrdinalIgnoreCase));
                if (persistedIndex >= 0)
                    _selectedTargetIndex = persistedIndex;
            }

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 4;

            var dropdown = new PopupField<string>(names, _selectedTargetIndex);
            dropdown.style.flexGrow = 1;
            dropdown.style.height = 26;
            dropdown.RegisterValueChangedCallback(evt =>
            {
                _selectedTargetIndex = names.IndexOf(evt.newValue);
                _settings.MCPSelectedConfigTarget = evt.newValue;
                _rebuildWindow?.Invoke();
            });
            MCPDropdownStyle.Apply(dropdown);
            row.Add(dropdown);

            var configureButton = new Button(() =>
            {
                ConfigureMCPForTarget(_targets[_selectedTargetIndex]);
                RefreshStatus();
            });
            configureButton.text = "Configure";
            configureButton.style.height = 26;
            configureButton.style.width = 80;
            configureButton.style.marginLeft = 4;
            configureButton.style.backgroundColor = new Color(0.2f, 0.5f, 0.3f);
            configureButton.style.color = Color.white;
            row.Add(configureButton);

            var selectedTarget = _targets[_selectedTargetIndex];
            var skillsSupported = !string.IsNullOrEmpty(MapTargetNameToSkillsPlatformId(selectedTarget.Name));
            var configureSkillsButton = new Button(() =>
            {
                ConfigureMCPAndSkillsForTarget(_targets[_selectedTargetIndex]);
                RefreshStatus();
            });
            configureSkillsButton.text = "Configure + Skills";
            configureSkillsButton.style.height = 26;
            configureSkillsButton.style.width = 130;
            configureSkillsButton.style.marginLeft = 4;
            configureSkillsButton.style.marginRight = 0;
            configureSkillsButton.style.backgroundColor = MCPPalette.AccentBlue;
            configureSkillsButton.style.color = Color.white;
            configureSkillsButton.SetEnabled(skillsSupported);
            row.Add(configureSkillsButton);

            body.Add(row);

            _configResultLabel = new Label();
            _configResultLabel.style.fontSize = 11;
            _configResultLabel.style.color = MCPPalette.Ok;
            _configResultLabel.style.whiteSpace = WhiteSpace.Normal;
            _configResultLabel.style.marginBottom = 4;
            _configResultLabel.style.display = DisplayStyle.None;
            body.Add(_configResultLabel);

            // Deferred: the snippet build's first Newtonsoft call after a domain reload pays
            // the JIT cost (~40ms), lengthening the blank-window flash on Play.
            body.schedule.Execute(() => AddManualConfigurationSection(body, selectedTarget));

            RefreshStatus();
        }

        private void AddManualConfigurationSection(VisualElement parent, MCPConfigTarget target)
        {
            var foldout = new Foldout { text = "Manual Configuration", value = false }.Persist("ClientConfigManual");
            foldout.style.marginTop = -4;
            foldout.style.minWidth = 0;
            foldout.contentContainer.style.minWidth = 0;

            var toggleLabel = foldout.Q<Toggle>()?.Q<Label>();
            if (toggleLabel != null)
            {
                toggleLabel.style.fontSize = 12;
                toggleLabel.style.color = MCPPalette.HeadingBlue;
            }

            foldout.Add(MakeSectionLabel("Config Path:"));

            var pathRow = new VisualElement();
            pathRow.style.flexDirection = FlexDirection.Row;
            pathRow.style.alignItems = Align.Center;
            pathRow.style.minWidth = 0;
            pathRow.style.marginBottom = 6;

            var pathField = new TextField { value = target.ConfigPath, isReadOnly = true };
            pathField.style.flexGrow = 1;
            pathField.tooltip = target.ConfigPath;
            MakeShrinkable(pathField);
            pathRow.Add(pathField);

            pathRow.Add(MakeCopyButton(() => target.ConfigPath));

            var openButton = new Button(() =>
            {
                if (File.Exists(target.ConfigPath))
                    EditorUtility.RevealInFinder(target.ConfigPath);
                else
                    EditorUtility.DisplayDialog(
                        "Manual Configuration",
                        $"Config file does not exist yet:\n{target.ConfigPath}",
                        "OK");
            });
            openButton.text = "Open";
            openButton.style.height = 22;
            openButton.style.width = 50;
            openButton.style.flexShrink = 0;
            openButton.style.marginLeft = 4;
            pathRow.Add(openButton);

            foldout.Add(pathRow);

            var configRow = new VisualElement();
            configRow.style.flexDirection = FlexDirection.Row;
            configRow.style.alignItems = Align.Center;
            configRow.Add(MakeSectionLabel("Configuration:"));
            configRow.Add(MakeScopeButton(target, "Project", false));
            configRow.Add(MakeScopeButton(target, "Global", true));
            foldout.Add(configRow);

            var snippet = BuildManualConfigSnippet(target);
            var snippetRow = new VisualElement();
            snippetRow.style.flexDirection = FlexDirection.Row;
            snippetRow.style.alignItems = Align.FlexStart;
            snippetRow.style.minWidth = 0;
            snippetRow.style.marginBottom = 6;

            var snippetField = new TextField { value = snippet, isReadOnly = true, multiline = true };
            snippetField.style.flexGrow = 1;
            MakeShrinkable(snippetField);
#if UNITY_2023_2_OR_NEWER
            snippetField.style.whiteSpace = WhiteSpace.Pre;
#else
            snippetField.style.whiteSpace = WhiteSpace.NoWrap;
#endif
            snippetRow.Add(snippetField);

            snippetRow.Add(MakeCopyButton(() => snippet));
            foldout.Add(snippetRow);

            foldout.Add(MakeSectionLabel("Installation Steps:"));

            var steps = new Label(
                $"1. Open {target.Name} and locate its MCP servers configuration\n" +
                $"2. Open the config file above (or create it if missing)\n" +
                "3. Merge the configuration snippet into the file, or use the Configure button above\n" +
                $"4. Restart {target.Name} if necessary");
            steps.style.fontSize = 11;
            steps.style.color = MCPPalette.TextHint;
            steps.style.whiteSpace = WhiteSpace.Normal;
            steps.style.marginBottom = 4;
            foldout.Add(steps);

            parent.Add(foldout);
        }

        // A client can expose a project config, a global one, or both; the button for a scope the
        // selected client has no config file for stays disabled rather than writing a guessed path.
        private Button MakeScopeButton(MCPConfigTarget target, string text, bool global)
        {
            var isActive = target.ProjectScoped != global;
            var supported = target.Supports(global);

            var button = new Button(() =>
            {
                EditorPrefs.SetBool(ScopeGlobalKey, global);
                _rebuildWindow?.Invoke();
            });
            button.text = text;
            button.style.height = 20;
            button.style.width = 62;
            button.style.flexShrink = 0;
            button.style.fontSize = 11;
            button.Margin(0, 0, 2, global ? 3 : 8);
            button.Rounded(3);
            button.style.backgroundColor = isActive
                ? MCPPalette.AccentBlue
                : MCPPalette.Surface;
            button.style.color = isActive ? Color.white : MCPPalette.TextMuted;
            button.style.unityFontStyleAndWeight = isActive ? FontStyle.Bold : FontStyle.Normal;
            button.SetEnabled(supported && !isActive);
            button.tooltip = supported
                ? (global ? target.GlobalConfigPath : target.ProjectConfigPath)
                : $"{target.Name} has no {text.ToLowerInvariant()} MCP config file.";
            return button;
        }

        // The TextField's inner elements carry the same flex-shrink:0 default, so shrinking
        // only the field itself is not enough.
        private static void MakeShrinkable(TextField field)
        {
            field.Shrinkable();
            foreach (var element in field.Query<VisualElement>().Build())
                element.Shrinkable();
        }

        private static Label MakeSectionLabel(string text)
        {
            var label = new Label(text);
            label.style.fontSize = 11;
            label.style.color = new Color(0.75f, 0.75f, 0.75f);
            label.style.marginBottom = 2;
            return label;
        }

        private static Button MakeCopyButton(Func<string> getText)
        {
            var button = new Button();
            button.text = "Copy";
            button.style.height = 22;
            button.style.width = 60;
            button.style.flexShrink = 0;
            button.style.marginLeft = 4;
            button.clicked += () =>
            {
                EditorGUIUtility.systemCopyBuffer = getText();
                button.text = "Copied ✓";
                button.style.color = MCPPalette.Ok;
                button.schedule.Execute(() =>
                {
                    button.text = "Copy";
                    button.style.color = StyleKeyword.Null;
                }).ExecuteLater(1500);
            };
            return button;
        }

        private string BuildManualConfigSnippet(MCPConfigTarget target)
        {
            if (target.IsToml)
                return CreateTomlSection(target);

            var rootKey = string.IsNullOrEmpty(target.RootKey) ? "mcpServers" : target.RootKey;
            var root = new Dictionary<string, object>
            {
                [rootKey] = new Dictionary<string, object> { [ServerEntryName] = CreateHttpEntry(target) }
            };
            if (!string.IsNullOrEmpty(target.SchemaUrl))
                root["$schema"] = target.SchemaUrl;

            return Newtonsoft.Json.JsonConvert.SerializeObject(root, Newtonsoft.Json.Formatting.Indented);
        }

        public void RefreshStatus()
        {
            if (_configStatusLabel == null || _configPathLabel == null || _targets == null)
                return;

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _targets.Length - 1);
            var target = _targets[idx];

            var configText = ReadConfigText(target.ConfigPath);

            var configured = ConfigHasOurEntry(configText);
            _configStatusLabel.text = configured ? "Configured ✓" : "Not configured ✕";
            _configStatusLabel.style.color = configured
                ? MCPPalette.Ok
                : MCPPalette.Warn;
            _configPathLabel.text = target.ConfigPath;
            _configPathLabel.tooltip = target.ConfigPath + ProjectScopedNote(target);

            var liveUrl = _server != null && _server.Port > 0 ? BuildServerUrl(_server.Port) : null;

            // The client reads the files it reads regardless of the scope this panel happens to be
            // showing, so a broken entry in the other scope still breaks the connection -- and for a
            // client that merges the global file over the project one it is the only entry that
            // counts. The selected scope speaks first because its path is the one on screen; only
            // when it is clean does the other one get to, and then it names the file it means.
            var problem = DescribeConfigProblem(configText, liveUrl);
            var problemPath = problem == null ? null : target.ConfigPath;

            if (problem == null)
            {
                var otherIsGlobal = target.ProjectScoped;
                var otherPath = otherIsGlobal ? target.GlobalConfigPath : target.ProjectConfigPath;
                var otherProblem = DescribeConfigProblem(ReadConfigText(otherPath), liveUrl);

                if (otherProblem != null)
                {
                    problem = $"{(otherIsGlobal ? "Global" : "Project")} config: {otherProblem}";
                    problemPath = otherPath;
                }
            }

            _configProblemLabel.text = problem == null ? string.Empty : "⚠ " + problem;
            _configProblemLabel.tooltip = problem == null
                ? string.Empty
                : $"{problemPath} posts to a URL that is not {liveUrl}. A URL on a stale port, or one " +
                  "written before project pinning, reaches whichever editor now owns that port -- so " +
                  "tool calls can land in a sibling project.";
        }

        private static string ReadConfigText(string path)
        {
            try
            {
                return !string.IsNullOrEmpty(path) && File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                return null;
            }
        }

        // The file existing said nothing: one holding other MCP servers and no entry of ours read as
        // configured. Searching for the quoted key rather than parsing covers the JSON and TOML targets
        // alike, and the quotes keep a mention inside some other value from counting.
        internal static bool ConfigHasOurEntry(string configText)
        {
            return !string.IsNullOrEmpty(configText) &&
                   (configText.Contains($"\"{ServerEntryName}\"") ||
                    configText.Contains($".{ServerEntryName}]"));
        }

        // Returns the problem without a marker; callers prefix "⚠ " and name the file.
        internal static string DescribeConfigProblem(string configText, string liveUrl)
        {
            if (!ConfigHasOurEntry(configText) || string.IsNullOrEmpty(liveUrl))
                return null;

            if (configText.Contains(liveUrl))
                return null;

            // One entry name, one global file, many projects: an entry pinned to a sibling project
            // is that project's, and the background sweep deliberately leaves it alone. Sending the
            // user to Configure would point it at this editor and break the sibling, so name the
            // fix that actually applies.
            return PinnedToAnotherProject(configText, liveUrl)
                ? "Entry belongs to another project - remove it"
                : "Points at another URL - re-run Configure";
        }

        private static bool PinnedToAnotherProject(string configText, string liveUrl)
        {
            var ours = HttpMCPTransport.ExtractPin(liveUrl);
            if (ours.Length == 0)
                return false;

            foreach (Match found in PinPattern.Matches(configText))
            {
                if (!string.Equals(found.Groups[1].Value, ours, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static readonly Regex PinPattern = new Regex("/p/([^/\"\\s]+)", RegexOptions.Compiled);

        public static string[] GetAllTargetNames()
            => GetAllTargets().Select(t => t.Name).ToArray();

        internal static MCPConfigTarget[] GetAllTargets()
            => CreateTargets(GetUserHomePath());

        private static MCPConfigTarget[] CreateTargets(string homePath, bool useGlobal = false)
        {
            var project = GetProjectRootPath();
            var targets = new[]
            {
                // A project-scoped file holds entries for this project alone, so the entry keeps the
                // plain "kitwright" name and no suffix is needed to separate sibling projects.
                new MCPConfigTarget
                {
                    Name = "Claude Code",
                    ProjectConfigPath = Path.Combine(project, ".mcp.json"),
                    GlobalConfigPath = Path.Combine(homePath, ".claude.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    Name = "Cursor",
                    ProjectConfigPath = Path.Combine(project, ".cursor", "mcp.json"),
                    GlobalConfigPath = Path.Combine(homePath, ".cursor", "mcp.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    Name = "VS Code",
                    ProjectConfigPath = Path.Combine(project, ".vscode", "mcp.json"),
                    GlobalConfigPath = Path.Combine(AppConfigRoot(homePath), "Code", "User", "mcp.json"),
                    IncludeTypeField = true,
                    RootKey = "servers"
                },
                new MCPConfigTarget
                {
                    Name = "Trae",
                    ProjectConfigPath = Path.Combine(project, ".trae", "mcp.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    Name = "Kiro",
                    ProjectConfigPath = Path.Combine(project, ".kiro", "settings", "mcp.json"),
                    GlobalConfigPath = Path.Combine(homePath, ".kiro", "settings", "mcp.json"),
                    IncludeTypeField = true,
                    RootKey = "mcpServers"
                },
                new MCPConfigTarget
                {
                    Name = "Codex",
                    ProjectConfigPath = Path.Combine(project, ".codex", "config.toml"),
                    GlobalConfigPath = Path.Combine(homePath, ".codex", "config.toml"),
                    IsToml = true
                },
                new MCPConfigTarget
                {
                    Name = "Windsurf",
                    GlobalConfigPath = Path.Combine(homePath, ".codeium", "windsurf", "mcp_config.json"),
                    IncludeTypeField = true,
                    HttpUrlProperty = "serverUrl"
                },
                new MCPConfigTarget
                {
                    Name = "Cline",
                    GlobalConfigPath = GetClineConfigPath(homePath),
                    IncludeTypeField = true,
                    HttpTypeValue = "streamableHttp"
                },
                new MCPConfigTarget
                {
                    Name = "VS Code Insiders",
                    ProjectConfigPath = Path.Combine(project, ".vscode", "mcp.json"),
                    GlobalConfigPath = Path.Combine(AppConfigRoot(homePath), "Code - Insiders", "User", "mcp.json"),
                    IncludeTypeField = true,
                    RootKey = "servers"
                },
                new MCPConfigTarget
                {
                    Name = "Rider",
                    GlobalConfigPath = GetRiderConfigPath(homePath),
                    IncludeTypeField = true,
                    RootKey = "servers"
                },
                new MCPConfigTarget
                {
                    // Rider hosts two separate assistants: Copilot (above, IDE-level config) and
                    // JetBrains Junie, which reads a workspace file instead.
                    Name = "Rider (Junie)",
                    ProjectConfigPath = Path.Combine(project, ".junie", "mcp", "mcp.json"),
                    IncludeTypeField = true,
                    DefaultFields = new Dictionary<string, object> { ["enabled"] = true }
                },
                new MCPConfigTarget
                {
                    Name = "Kimi Code",
                    ProjectConfigPath = Path.Combine(project, ".kimi-code", "mcp.json"),
                    GlobalConfigPath = Path.Combine(homePath, ".kimi-code", "mcp.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    Name = "Qwen Code",
                    ProjectConfigPath = Path.Combine(project, ".qwen", "settings.json"),
                    GlobalConfigPath = Path.Combine(homePath, ".qwen", "settings.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    // Antigravity 2.0 and Antigravity IDE are separate apps that coexist on one
                    // machine and share these files, so one entry configures either. Verified by
                    // probe: a uniquely named server placed in .agents/ shows up in the IDE, while
                    // the same probe under <project>/.gemini/ never does — that directory only
                    // works as the global location.
                    Name = "Antigravity",
                    ProjectConfigPath = Path.Combine(project, ".agents", "mcp_config.json"),
                    GlobalConfigPath = Path.Combine(homePath, ".gemini", "config", "mcp_config.json"),
                    IncludeTypeField = true,
                    HttpUrlProperty = "serverUrl",
                    DefaultFields = new Dictionary<string, object> { ["disabled"] = false },
                    // Probed: with the project file naming the live port and the global file a dead
                    // one, Antigravity dials the global URL -- the global entry wins the name.
                    GlobalShadowsProject = true
                },
                new MCPConfigTarget
                {
                    Name = "Kilo Code",
                    ProjectConfigPath = Path.Combine(project, ".kilocode", "mcp.json"),
                    IncludeTypeField = true,
                    RootKey = "mcp",
                    HttpTypeValue = "remote",
                    SchemaUrl = "https://app.kilo.ai/config.json",
                    DefaultFields = new Dictionary<string, object> { ["enabled"] = true }
                },
                new MCPConfigTarget
                {
                    Name = "OpenCode",
                    ProjectConfigPath = Path.Combine(project, "opencode.json"),
                    GlobalConfigPath = Path.Combine(homePath, ".config", "opencode", "opencode.json"),
                    IncludeTypeField = true,
                    RootKey = "mcp",
                    HttpTypeValue = "remote",
                    SchemaUrl = "https://opencode.ai/config.json",
                    DefaultFields = new Dictionary<string, object> { ["enabled"] = true }
                },
                new MCPConfigTarget
                {
                    // A workspace config needs trust prompts or an env flag to be picked up, so the
                    // home file is the one that works unattended.
                    Name = "GitHub Copilot CLI",
                    GlobalConfigPath = Path.Combine(homePath, ".copilot", "mcp-config.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    Name = "CodeBuddy CLI",
                    ProjectConfigPath = Path.Combine(project, ".codebuddy", "settings.json"),
                    IncludeTypeField = true
                },
                new MCPConfigTarget
                {
                    Name = "Roo Code",
                    ProjectConfigPath = Path.Combine(project, ".roo", "mcp.json"),
                    IncludeTypeField = true,
                    HttpTypeValue = "streamableHttp"
                },
            };

            for (var i = 0; i < targets.Length; i++)
                targets[i].UseGlobal = useGlobal;

            return targets;
        }

        private void ConfigureMCPForTarget(MCPConfigTarget target)
        {
            try
            {
                var shadowNote = WriteMCPConfigurationForTarget(target);

                ShowConfigResult($"✓ Configured - restart {target.Name} to connect." + shadowNote);
            }
            catch (Exception ex)
            {
                ShowConfigError(ex);
            }
        }

        private void ConfigureMCPAndSkillsForTarget(MCPConfigTarget target)
        {
            try
            {
                var shadowNote = WriteMCPConfigurationForTarget(target);

                var platformId = MapTargetNameToSkillsPlatformId(target.Name);
                if (string.IsNullOrEmpty(platformId))
                {
                    ShowConfigResult($"✓ Configured - restart {target.Name} to connect. " +
                                     "Project skills are not available for this client." + shadowNote);
                    return;
                }

                if (!ConfigureProjectSkillsForPlatform(platformId))
                    return;

                var projectRoot = GetProjectRootPath();
                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var generatedPaths = ProjectSkillsManager.GetGeneratedPathsForPlatform(projectRoot, manifest, platformId);

                ShowConfigResult($"✓ Configured - restart {target.Name} to connect. " +
                                 $"Installed {generatedPaths.Count()} project skill file(s)." + shadowNote);
            }
            catch (Exception ex)
            {
                ShowConfigError(ex);
            }
        }

        // No RefreshStatus here: the button handlers call it on every path, error included.
        private void ShowConfigResult(string message)
        {
            if (_configResultLabel == null)
                return;

            _configResultLabel.text = message;
            _configResultLabel.style.display = DisplayStyle.Flex;

            // ~40ms a character: the variant reporting a removed global entry is 5x the short one.
            _configResultLabel.schedule
                .Execute(() => _configResultLabel.style.display = DisplayStyle.None)
                .ExecuteLater(Mathf.Clamp(message.Length * 40, 3000, 9000));
        }

        /// <summary>Writes the config and returns a note for the result line, or null when there is nothing to add.</summary>
        private string WriteMCPConfigurationForTarget(MCPConfigTarget target)
        {
            var dir = Path.GetDirectoryName(target.ConfigPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (target.IsToml)
                ConfigureTomlTarget(target);
            else
                ConfigureJsonTarget(target);

            // Only after the project file is on disk: if this threw, the user still has whatever
            // the global file gave them.
            return ClearShadowingGlobalEntry(target);
        }

        // Antigravity merges the global file over the project one, so a global entry of ours wins
        // however correct the project file is. One global slot cannot serve every project that
        // writes the same entry name -- repointing it at whichever project clicked Configure last
        // just moves the breakage to a sibling. Removing it lets each project fall back to its own
        // file, so the entry goes. Clients with no project config are left alone: there the global
        // file is the only one they read, and the background sweep keeps its URL fresh.
        private static string ClearShadowingGlobalEntry(MCPConfigTarget target)
        {
            if (!target.GlobalShadowsProject || !target.ProjectScoped)
                return null;

            var globalPath = target.GlobalConfigPath;
            var text = ReadConfigText(globalPath);
            if (!ConfigHasOurEntry(text))
                return null;

            if (!RemoveOurEntries(globalPath, text, target))
                return $" Warning: the global config ({globalPath}) holds a \"{ServerEntryName}\" " +
                       "entry that shadows this one, but it could not be parsed - remove it by hand.";

            return $" Also removed the \"{ServerEntryName}\" entry from the global config, which was " +
                   "shadowing this project; re-run Configure in any other project that relied on it.";
        }

        // JSON only: the sole client that shadows is Antigravity, and Codex is the only TOML target.
        private static bool RemoveOurEntries(string path, string text, MCPConfigTarget target)
        {
            if (!(JsonCodec.Deserialize(text) is Dictionary<string, object> root))
                return false;

            var rootKey = string.IsNullOrEmpty(target.RootKey) ? "mcpServers" : target.RootKey;
            if (!(root.TryGetValue(rootKey, out var serversObj) && serversObj is Dictionary<string, object> servers))
                return true;

            var removed = servers.Remove(ServerEntryName);
            removed |= servers.Remove(PinnedServerEntryName());
            removed |= servers.Remove(ProductServerEntryName());
            foreach (var legacy in LegacyServerEntryNames())
                removed |= servers.Remove(legacy);

            if (removed)
                AtomicFile.WriteAllText(path, JsonCodec.Serialize(root));

            return true;
        }

        private bool ConfigureProjectSkillsForPlatform(string platformId)
        {
            var projectRoot = GetProjectRootPath();
            var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
            var selectedPlatforms = new HashSet<string>(manifest.platforms, StringComparer.OrdinalIgnoreCase)
            {
                platformId
            };

            if (!ProjectSkillsManager.ConfirmOverwriteConflicts(projectRoot, selectedPlatforms))
                return false;

            ProjectSkillsManager.ApplyConfiguration(projectRoot, selectedPlatforms);
            return true;
        }

        private void ConfigureJsonTarget(MCPConfigTarget target)
        {
            var rootKey = string.IsNullOrEmpty(target.RootKey) ? "mcpServers" : target.RootKey;
            var serverName = ServerEntryName;
            var entry = CreateHttpEntry(target);

            var existingJson = ReadConfigText(target.ConfigPath);
            var json = MergeJsonConfig(existingJson, rootKey, serverName, entry, target.SchemaUrl, target.ConfigPath);

            AtomicFile.WriteAllText(target.ConfigPath, json);
        }

        internal static string MergeJsonConfig(
            string existingJson,
            string rootKey,
            string serverName,
            object entry,
            string schemaUrl,
            string configPath)
        {
            Dictionary<string, object> root;
            var parsed = JsonCodec.Deserialize(existingJson) as Dictionary<string, object>;

            // A file we cannot parse that still has content holds servers we cannot see. Rewriting
            // it would drop every one of them, so stop and let the user fix it. A blank file carries
            // nothing, so it falls through and gets rebuilt.
            if (parsed == null && !string.IsNullOrWhiteSpace(existingJson))
                throw new IOException(
                    $"{configPath} is not valid JSON. Refusing to overwrite it so other MCP servers " +
                    "in the file are not lost. Fix or delete the file, then configure again.");

            if (parsed != null && parsed.ContainsKey(rootKey))
            {
                root = parsed;
                var servers = root[rootKey] as Dictionary<string, object>;
                if (servers != null)
                {
                    servers.Remove(PinnedServerEntryName());
                    servers.Remove(ProductServerEntryName());
                    foreach (var legacy in LegacyServerEntryNames())
                        servers.Remove(legacy);
                    servers[serverName] = entry;
                }
                else
                    root[rootKey] = new Dictionary<string, object> { [serverName] = entry };
            }
            else
            {
                root = parsed ?? new Dictionary<string, object>();
                root[rootKey] = new Dictionary<string, object> { [serverName] = entry };
            }

            if (!string.IsNullOrEmpty(schemaUrl) && !root.ContainsKey("$schema"))
                root["$schema"] = schemaUrl;

            var json = JsonCodec.Serialize(root);
            if (string.IsNullOrWhiteSpace(json))
                throw new IOException($"Serializing the MCP configuration produced no content; {configPath} was left untouched.");

            return json;
        }

        private void ConfigureTomlTarget(MCPConfigTarget target)
        {
            var content = File.Exists(target.ConfigPath) ? File.ReadAllText(target.ConfigPath) : string.Empty;
            content = RemoveTomlSection(content, "[mcp_servers." + PinnedServerEntryName() + "]");
            content = RemoveTomlSection(content, "[mcp_servers." + ServerEntryName + "]");
            foreach (var legacy in LegacyServerEntryNames())
                content = RemoveTomlSection(content, "[mcp_servers." + legacy + "]");

            if (content.Length > 0 && !content.EndsWith("\n"))
                content += "\n";

            content = EnsureCodexRmcpFeature(content + "\n" + CreateTomlSection(target));

            AtomicFile.WriteAllText(target.ConfigPath, content);
        }

        private static string RemoveTomlSection(string content, string sectionHeader)
        {
            var startIdx = content.IndexOf(sectionHeader, StringComparison.Ordinal);
            if (startIdx < 0)
                return content;

            var nextSection = content.IndexOf("\n[", startIdx + sectionHeader.Length, StringComparison.Ordinal);
            var endIdx = nextSection >= 0 ? nextSection + 1 : content.Length;
            return content.Substring(0, startIdx) + content.Substring(endIdx);
        }

        private static string EnsureCodexRmcpFeature(string content)
        {
            if (content.Contains("rmcp_client"))
                return content;

            var featuresIdx = content.IndexOf("[features]", StringComparison.Ordinal);
            if (featuresIdx >= 0)
            {
                var afterHeader = featuresIdx + "[features]".Length;
                var insertAt = content.IndexOf('\n', afterHeader);
                insertAt = insertAt >= 0 ? insertAt + 1 : content.Length;
                return content.Substring(0, insertAt) + "rmcp_client = true\n" + content.Substring(insertAt);
            }

            if (content.Length > 0 && !content.EndsWith("\n"))
                content += "\n";
            return content + "\n[features]\nrmcp_client = true\n";
        }


        private Dictionary<string, object> CreateHttpEntry(MCPConfigTarget target)
        {
            var urlProperty = string.IsNullOrEmpty(target.HttpUrlProperty) ? "url" : target.HttpUrlProperty;
            var entry = new Dictionary<string, object>
            {
                [urlProperty] = GetServerUrl()
            };

            if (target.IncludeTypeField)
                entry["type"] = string.IsNullOrEmpty(target.HttpTypeValue) ? "http" : target.HttpTypeValue;

            if (target.DefaultFields != null)
            {
                foreach (var kvp in target.DefaultFields)
                    entry[kvp.Key] = kvp.Value;
            }

            return entry;
        }

        private string CreateTomlSection(MCPConfigTarget target)
        {
            if (!target.IsToml)
                return string.Empty;

            return $"[mcp_servers.{ServerEntryName}]\nurl = \"{GetServerUrl()}\"\n";
        }

        // Per-project entry name so configuring from two Unity editors does not overwrite each
        // other's entry in the client's MCP config. productName alone is not unique — a clone or a
        // second checkout of the same project carries the same one — so the project-path pin is
        // what actually keeps the entries apart.
        // Both halves of the written URL are local to this machine and this folder: the port comes
        // from UserSettings (per-machine) and the pin from the absolute project path. A teammate who
        // commits and checks out this file gets a URL that resolves to nothing they own — the pin
        // guard answers 404 rather than letting it reach a sibling project, but the config is still
        // useless to them. Keep it out of version control.
        private static string ProjectScopedNote(MCPConfigTarget target)
        {
            if (!target.ProjectScoped)
                return string.Empty;

            var note = "\n\nThis file lives in the project and is machine-specific (it holds this " +
                       "machine's port and this folder's project pin). Add it to .gitignore rather " +
                       "than committing it; teammates run Configure to generate their own.";

            // Configure clears the shadowing global entry itself for these clients, so telling the
            // user to go delete it would send them after a file that is already clean.
            return target.GlobalShadowsProject
                ? note
                : note + "\n\nIf you configured this client before, its old entry in your home " +
                         "directory is now unused and can be deleted.";
        }

        // One name for both scopes. A global config is shared by every project, so the entry there
        // is rewritten by whichever project configures last — which is what a dev working on one
        // project at a time wants.
        internal const string ServerEntryName = "kitwright";

        // The pinned name written by 0.6.x, kept so configuring can drop the stale entry left
        // behind instead of leaving it pointed at a port nobody answers on.
        private static string PinnedServerEntryName()
        {
            return ProductServerEntryName() + "-" +
                   ProjectIdentity.PinFromProjectPath(GetProjectRootPath());
        }

        // Entries written under the pre-rename brand. Configuring drops them so an upgraded user
        // is not left with a duplicate server pointing at whatever port answered back then.
        private static IEnumerable<string> LegacyServerEntryNames()
        {
            yield return LegacyBrand;
            yield return ProductServerEntryName(LegacyBrand + "-");
            yield return ProductServerEntryName(LegacyBrand + "-") + "-" +
                         ProjectIdentity.PinFromProjectPath(GetProjectRootPath());
        }

        private const string LegacyBrand = "gamewright";

        private static string ProductServerEntryName() => ProductServerEntryName("kitwright-");

        private static string ProductServerEntryName(string prefix)
        {
            var name = Application.productName ?? string.Empty;
            var sb = new StringBuilder(prefix);
            foreach (var ch in name.ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                    sb.Append(ch);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                    sb.Append('-');
            }
            var result = sb.ToString().TrimEnd('-');
            return result.Length <= prefix.Length ? prefix.TrimEnd('-') : result;
        }

        private string GetServerUrl()
        {
            var port = _server != null && _server.IsRunning
                ? _server.Port
                : _settings.MCPServerPort;
            return BuildServerUrl(port);
        }

        // The pin says which project this URL was written for. Without it a config left on a stale
        // port reaches whichever sibling editor now owns that port, and that editor answers —
        // applying the edits to the wrong project. HttpMCPTransport refuses a mismatched pin with a
        // 404, but only if the client actually sends one, which is what this path segment is for.
        internal static string BuildServerUrl(int port)
        {
            return $"http://127.0.0.1:{port}/p/{ProjectIdentity.PinFromProjectPath(GetProjectRootPath())}/";
        }

        private static string GetProjectRootPath()
        {
            return ApplicationPaths.ProjectRoot;
        }

        private static string MapTargetNameToSkillsPlatformId(string targetName)
        {
            switch (targetName?.Trim())
            {
                case "Codex":
                    return "codex";
                case "Claude Code":
                    return "claude";
                case "Cursor":
                    return "cursor";
                default:
                    // Every other IDE/agent reads the open .agents/skills/ standard (Antigravity, Windsurf, Gemini CLI...)
                    return "agents";
            }
        }

        private static string GetUserHomePath()
        {
            var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(homePath))
                return homePath;

            var homeDrive = Environment.GetEnvironmentVariable("HOMEDRIVE");
            var homeDir = Environment.GetEnvironmentVariable("HOMEPATH");
            if (!string.IsNullOrEmpty(homeDrive) && !string.IsNullOrEmpty(homeDir))
                return homeDrive + homeDir;

            return Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        }

        private static void ShowConfigError(Exception ex)
        {
            EditorUtility.DisplayDialog(
                "MCP Configuration Error",
                $"Configuration failed:\n{ex.Message}",
                "OK");
        }

        // Where a desktop client keeps its per-user config: %APPDATA% (or %LOCALAPPDATA%) on Windows,
        // ~/Library/Application Support on macOS, ~/.config on Linux. An empty Windows folder path
        // also falls through to ~/.config rather than producing a rooted-nowhere path.
        private static string AppConfigRoot(string homePath, bool localAppData = false)
        {
            if (Application.platform == RuntimePlatform.WindowsEditor)
            {
                var root = Environment.GetFolderPath(localAppData
                    ? Environment.SpecialFolder.LocalApplicationData
                    : Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(root))
                    return root;
            }

            if (Application.platform == RuntimePlatform.OSXEditor)
                return Path.Combine(homePath, "Library", "Application Support");

            return Path.Combine(homePath, ".config");
        }

        private static string GetRiderConfigPath(string homePath)
        {
            return Path.Combine(AppConfigRoot(homePath, localAppData: true), "github-copilot", "intellij", "mcp.json");
        }

        // Cline reads only its VS Code extension globalStorage file; it has no workspace config.
        private static string GetClineConfigPath(string homePath)
        {
            return Path.Combine(AppConfigRoot(homePath), "Code", "User", "globalStorage",
                "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json");
        }

        internal struct MCPConfigTarget
        {
            public string Name;
            public string RootKey;
            public bool IsToml;
            public bool IncludeTypeField;
            public string HttpTypeValue;
            public string HttpUrlProperty;
            public string SchemaUrl;
            public Dictionary<string, object> DefaultFields;

            // A client may offer a config inside the workspace, one in the home directory, or both;
            // an empty path means it has no config file for that scope (or none that works
            // unattended), and the selected scope falls back to the one it does have.
            public string ProjectConfigPath;
            public string GlobalConfigPath;
            public bool UseGlobal;

            // Set only for a client verified to merge its global file over the project one, where a
            // global entry of ours wins however correct the project file is. Most clients do the
            // opposite, and there a global entry is the user's deliberate all-projects setup.
            public bool GlobalShadowsProject;

            public bool Supports(bool global)
                => !string.IsNullOrEmpty(global ? GlobalConfigPath : ProjectConfigPath);

            public bool ProjectScoped => Supports(false) && !(UseGlobal && Supports(true));
            public string ConfigPath => ProjectScoped ? ProjectConfigPath : GlobalConfigPath;
        }
    }
}
