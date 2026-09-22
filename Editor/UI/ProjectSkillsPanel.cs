// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using KitWright.Editor.Services;
using KitWright.Editor.Settings;

namespace KitWright.Editor.MCP.Server
{
    internal sealed class ProjectSkillsPanel : IMCPWindowPanel
    {
        private readonly SettingsController _settingsController;
        private VisualElement _root;
        private VisualElement _mainContainer;
        private Label _statusSummaryText;
        private Label _manifestPathLabel;
        private VisualElement _generatedFilesContainer;
        private Button _rewriteButton;
        private MCPSwitchToggle _enableCurrentPlatformToggle;
        private string[] _platformTargets;
        private int _selectedTargetIndex;

        public ProjectSkillsPanel(SettingsController settingsController)
        {
            _settingsController = settingsController;
        }

        public void Build(VisualElement container)
        {
            _root = container;
            BuildUI();
        }

        public void Dispose()
        {
        }

        private void BuildUI()
        {
            _root.Clear();

            var outerLayout = new VisualElement();
            outerLayout.style.flexGrow = 1;
            outerLayout.style.flexDirection = FlexDirection.Column;
            _root.Add(outerLayout);

            var scrollView = new ScrollView(ScrollViewMode.Vertical);
            scrollView.style.flexGrow = 1;
            scrollView.style.marginBottom = 8;
            outerLayout.Add(scrollView);

            _mainContainer = scrollView.contentContainer;

            _mainContainer.Add(MCPSection.PanelTitle("Project Skills"));
            _mainContainer.Add(MCPSection.PanelHint("Configure project-level skills for supported AI clients. Turning a platform on writes every skill the installed packages ship; turning it off removes them."));

            BuildPlatformSection();
            BuildSkillsSection();
            BuildStatusSection();

            RefreshStatus();
        }

        private void BuildPlatformSection()
        {
            var (section, foldout) = MCPSection.Create("Current Platform", "CurrentPlatform");

            _platformTargets = ClientConfigPanel.GetAllTargetNames();
            _selectedTargetIndex = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            var persistedTargetName = _settingsController.MCPSelectedConfigTarget;
            if (!string.IsNullOrWhiteSpace(persistedTargetName))
            {
                var persistedIndex = Array.FindIndex(_platformTargets, name => string.Equals(name, persistedTargetName, StringComparison.OrdinalIgnoreCase));
                if (persistedIndex >= 0)
                    _selectedTargetIndex = persistedIndex;
            }

            var platformDropdown = new PopupField<string>(new List<string>(_platformTargets), _selectedTargetIndex);
            platformDropdown.style.marginBottom = 6;
            platformDropdown.RegisterValueChangedCallback(evt =>
            {
                _selectedTargetIndex = Array.IndexOf(_platformTargets, evt.newValue);
                _settingsController.MCPSelectedConfigTarget = evt.newValue;
                BuildUI();
            });
            MCPDropdownStyle.Apply(platformDropdown);
            foldout.Add(platformDropdown);

            var currentPlatformId = GetCurrentSkillsPlatformId();
            var currentPlatformSupported = !string.IsNullOrEmpty(currentPlatformId);
            var manifest = ProjectSkillsManager.LoadManifest(GetProjectRootPath());

            _enableCurrentPlatformToggle = new MCPSwitchToggle("Enable skills for current platform");
            _enableCurrentPlatformToggle.SetValueWithoutNotify(
                currentPlatformSupported &&
                manifest.platforms.Contains(currentPlatformId, StringComparer.OrdinalIgnoreCase));
            _enableCurrentPlatformToggle.SetEnabled(currentPlatformSupported);
            _enableCurrentPlatformToggle.style.marginBottom = 4;
            _enableCurrentPlatformToggle.RegisterValueChangedCallback(_ => ApplyProjectSkillsConfiguration());
            foldout.Add(_enableCurrentPlatformToggle);

            if (!currentPlatformSupported)
            {
                foldout.Add(CreateHint("Project skills integration is not available for this platform.", new Color(1f, 0.75f, 0.45f)));
            }

            _mainContainer.Add(section);
        }

        private void BuildSkillsSection()
        {
            var (builtInSection, builtInFoldout) = MCPSection.Create("Built-in Skills", "BuiltInSkills");

            foreach (var skill in ProjectSkillsManager.GetBuiltInSkills())
            {
                builtInFoldout.Add(CreateSkillRow(skill.Title, skill.Description, $"v{skill.Version} Required"));
            }
            _mainContainer.Add(builtInSection);

            var (packageSection, packageFoldout) = MCPSection.Create("Package Skills", "PackageSkills");

            var packageSkills = ProjectSkillsManager.GetPackageSkills();

            if (packageSkills.Count == 0)
            {
                packageFoldout.Add(CreateHint(
                    "No installed package ships a skill. A package adds one by shipping Skills~/<id>/SKILL.md.",
                    MCPPalette.TextHint));
            }
            else
            {
                foreach (var skill in packageSkills)
                    packageFoldout.Add(CreatePackageSkillCard(skill));

                packageFoldout.Add(CreateHint(
                    "Installed with the built-in skills while the platform is on. Remove one by uninstalling the package that ships it.",
                    MCPPalette.TextHint));
            }

            _mainContainer.Add(packageSection);
        }

        private void BuildStatusSection()
        {
            var (section, foldout) = MCPSection.Create("Installed Files", "InstalledFiles");

            // The section header already reserves its trailing edge for a badge, and the foldout
            // title's own label carries flex-grow, so a button dropped there sits at the right of
            // the header without a layout of its own.
            _rewriteButton = CreateRewriteButton();
            _rewriteButton.style.display = DisplayStyle.None;
            foldout.Q<Toggle>()?.Add(_rewriteButton);

            _statusSummaryText = new Label();
            _statusSummaryText.style.fontSize = 13;
            _statusSummaryText.style.marginBottom = 4;
            _statusSummaryText.style.whiteSpace = WhiteSpace.Normal;
            foldout.Add(_statusSummaryText);

            _manifestPathLabel = new Label();
            _manifestPathLabel.style.fontSize = 11;
            _manifestPathLabel.style.color = new Color(0.5f, 0.5f, 0.5f);
            _manifestPathLabel.style.marginBottom = 6;
            _manifestPathLabel.style.whiteSpace = WhiteSpace.Normal;
            OnDoubleClick(_manifestPathLabel, () => Reveal(ProjectSkillsManager.GetManifestPath(GetProjectRootPath())));
            foldout.Add(_manifestPathLabel);

            _generatedFilesContainer = new VisualElement();
            foldout.Add(_generatedFilesContainer);
            _mainContainer.Add(section);
        }

        private void RefreshStatus()
        {
            if (_statusSummaryText == null || _manifestPathLabel == null || _generatedFilesContainer == null)
                return;

            var projectRoot = GetProjectRootPath();
            var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
            var installedSkills = ProjectSkillsManager.GetInstalledSkills();
            var currentPlatformId = GetCurrentSkillsPlatformId();
            var currentPlatformDisplayName = GetCurrentSkillsPlatformDisplayName();
            var currentPlatformSupported = !string.IsNullOrEmpty(currentPlatformId);
            var currentPlatformConfigured = currentPlatformSupported &&
                                            manifest.platforms.Contains(currentPlatformId, StringComparer.OrdinalIgnoreCase);
            var manifestPath = ProjectSkillsManager.GetManifestPath(projectRoot);
            var manifestExists = File.Exists(manifestPath);
            var upgradeStatus = currentPlatformConfigured
                ? ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, currentPlatformId)
                : null;

            if (_enableCurrentPlatformToggle != null)
            {
                _enableCurrentPlatformToggle.SetEnabled(currentPlatformSupported);
                _enableCurrentPlatformToggle.SetValueWithoutNotify(currentPlatformConfigured);
            }

            if (!currentPlatformSupported)
            {
                _statusSummaryText.text = $"Status: Unsupported current platform | Skills: {installedSkills.Count}";
                _statusSummaryText.style.color = MCPPalette.Warn;
            }
            else if (!currentPlatformConfigured)
            {
                _statusSummaryText.text = $"Status: Not configured for {currentPlatformDisplayName} | Skills: {installedSkills.Count}";
                _statusSummaryText.style.color = MCPPalette.Warn;
            }
            else
            {
                if (upgradeStatus != null && upgradeStatus.HasUpdates)
                {
                    _statusSummaryText.text = $"Status: Configured for {currentPlatformDisplayName} | Skills: {installedSkills.Count} | Updates available";
                    _statusSummaryText.style.color = new Color(1f, 0.72f, 0.32f);
                }
                else
                {
                    _statusSummaryText.text = $"Status: Configured for {currentPlatformDisplayName} | Skills: {installedSkills.Count} | Up to date";
                    _statusSummaryText.style.color = MCPPalette.Ok;
                }
            }

            _manifestPathLabel.text = manifestExists
                ? $"Manifest: {manifestPath}"
                : $"Manifest will be created at: {manifestPath}";

            RefreshGeneratedFiles(projectRoot, manifest, currentPlatformId, currentPlatformDisplayName, currentPlatformConfigured);
        }

        private void ApplyProjectSkillsConfiguration()
        {
            var projectRoot = GetProjectRootPath();
            var currentPlatformId = GetCurrentSkillsPlatformId();

            if (string.IsNullOrEmpty(currentPlatformId))
                return;

            // The toggle is the whole control surface here, so it applies as it is flipped. Every
            // exit refreshes: a declined overwrite or a failed write leaves the switch showing what
            // is on disk rather than what was clicked.
            try
            {
                var manifest = ProjectSkillsManager.LoadManifest(projectRoot);
                var selectedPlatforms = new HashSet<string>(manifest.platforms, StringComparer.OrdinalIgnoreCase);
                if (_enableCurrentPlatformToggle != null && _enableCurrentPlatformToggle.value)
                    selectedPlatforms.Add(currentPlatformId);
                else
                    selectedPlatforms.Remove(currentPlatformId);

                if (!ProjectSkillsManager.ConfirmOverwriteConflicts(projectRoot, selectedPlatforms))
                    return;

                ProjectSkillsManager.ApplyConfiguration(projectRoot, selectedPlatforms);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog(
                    "Project Skills Configuration Error",
                    $"Configuration failed:\n{ex.Message}",
                    "OK");
            }
            finally
            {
                RefreshStatus();
            }
        }

        private string GetCurrentSkillsPlatformId()
        {
            if (_platformTargets == null || _platformTargets.Length == 0)
                return null;

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            return MapTargetNameToSkillsPlatformId(_platformTargets[idx]);
        }

        private string GetCurrentSkillsPlatformDisplayName()
        {
            if (_platformTargets == null || _platformTargets.Length == 0)
                return "Unknown";

            var idx = Mathf.Clamp(_selectedTargetIndex, 0, _platformTargets.Length - 1);
            return _platformTargets[idx];
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
                    return "agents";
            }
        }

        private void RefreshGeneratedFiles(
            string projectRoot,
            ProjectSkillsManager.ProjectSkillsManifest manifest,
            string currentPlatformId,
            string currentPlatformDisplayName,
            bool currentPlatformConfigured)
        {
            _generatedFilesContainer.Clear();
            _rewriteButton.style.display = DisplayStyle.None;

            if (string.IsNullOrEmpty(currentPlatformId))
            {
                _generatedFilesContainer.Add(CreateHint($"{currentPlatformDisplayName} is not supported for project skills yet.", MCPPalette.TextDim));
                return;
            }

            if (!currentPlatformConfigured)
            {
                _generatedFilesContainer.Add(CreateHint($"{currentPlatformDisplayName} skills are not configured yet. Turn on \"Enable skills for current platform\" to generate the files.", MCPPalette.TextMuted));
                return;
            }

            var upgradeStatus = ProjectSkillsManager.GetUpgradeStatus(projectRoot, manifest, currentPlatformId);
            if (upgradeStatus.Files.Count > 0)
            {
                _generatedFilesContainer.Add(CreateHint($"Skill files for {currentPlatformDisplayName}:", MCPPalette.TextMuted));
                foreach (var file in upgradeStatus.Files)
                {
                    var upToDate = !file.Missing && !file.Unmanaged && !file.RequiresUpgrade;
                    var row = CreateStatusRow(FormatVersionStatus(file), upToDate, upToDate ? new Color(0.55f, 0.85f, 0.55f) : new Color(1f, 0.72f, 0.32f), file.Path);
                    _generatedFilesContainer.Add(row);
                }

                // The platform switch writes as it is flipped, so an edited, deleted or hand-owned
                // file has no state change to ride along with. This is the one action for that, and
                // it exists only while there is something to repair.
                if (upgradeStatus.Files.Any(file => file.Missing || file.Unmanaged || file.RequiresUpgrade))
                    _rewriteButton.style.display = DisplayStyle.Flex;
            }

            var paths = ProjectSkillsManager.GetGeneratedPathsForPlatform(projectRoot, manifest, currentPlatformId);
            if (paths.Count == 0)
            {
                _generatedFilesContainer.Add(CreateHint($"Generated files for {currentPlatformDisplayName}: none.", MCPPalette.TextDim));
                return;
            }

            _generatedFilesContainer.Add(CreateHint($"Generated paths for {currentPlatformDisplayName}:", MCPPalette.TextMuted));
            foreach (var path in paths)
            {
                var exists = File.Exists(path) || Directory.Exists(path);
                var row = CreateStatusRow(exists ? path : $"Missing  {path}", exists, exists ? new Color(0.55f, 0.85f, 0.55f) : new Color(1f, 0.65f, 0.45f), path);
                _generatedFilesContainer.Add(row);
            }
        }

        private Button CreateRewriteButton()
        {
            var button = new Button(ApplyProjectSkillsConfiguration);
            button.text = "Rewrite";
            button.style.fontSize = 10;
            button.style.unityFontStyleAndWeight = FontStyle.Bold;
            button.style.height = 18;
            button.Padding(0, 7, 0, 7).Margin(0, 0, 0, 8);
            button.style.backgroundColor = MCPPalette.AccentBlue;
            button.style.color = Color.white;
            button.tooltip = "Write the KitWright-managed skill files for this platform again, replacing what is on disk.";

            // The button lives inside the foldout's own toggle, which collapses the section on any
            // click it sees.
            button.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());
            return button;
        }

        private static string GetProjectRootPath()
        {
            return ApplicationPaths.ProjectRoot;
        }

        private static string FormatVersionStatus(ProjectSkillsManager.SkillFileVersionStatus status)
        {
            if (status == null)
                return "Unknown skill file status";

            if (status.Missing)
                return $"Missing  {status.Path}";

            if (status.Unmanaged)
                return $"Conflict  {status.Path}  (not KitWright-managed)";

            // A stale file needs no word of its own: it is the only row that loses its check mark
            // while the header is showing a Rewrite button, and Missing and Conflict are the states
            // that survive a rewrite and so still have to name themselves.
            return status.Path;
        }

        private static VisualElement CreateStatusRow(string text, bool ok, Color color, string revealPath)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.justifyContent = Justify.SpaceBetween;
            row.style.marginLeft = 8;
            row.style.marginBottom = 2;

            var label = new Label(text);
            label.style.fontSize = 11;
            label.style.color = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.flexGrow = 1;
            row.Add(label);

            label.tooltip = $"Double-click to show in the file browser:\n{revealPath}";
            OnDoubleClick(label, () => Reveal(revealPath));

            if (ok)
            {
                var check = new Label("✓");
                check.style.fontSize = 11;
                check.style.color = color;
                check.style.marginLeft = 8;
                row.Add(check);
            }

            return row;
        }

        // A missing file cannot be selected, so the walk up to the nearest folder that exists is
        // what makes the row still worth double-clicking: it opens where the file should have been.
        private static void Reveal(string path)
        {
            var target = path;
            while (!string.IsNullOrEmpty(target) && !File.Exists(target) && !Directory.Exists(target))
                target = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(target))
                EditorUtility.RevealInFinder(target);
        }

        private static void OnDoubleClick(VisualElement element, Action action)
        {
            element.RegisterCallback<MouseDownEvent>(evt =>
            {
                if (evt.clickCount == 2)
                    action();
            });
        }

        private static Label CreateHint(string text, Color color)
        {
            var label = new Label(text);
            label.style.fontSize = 11;
            label.style.color = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            label.style.marginBottom = 4;
            return label;
        }

        private static VisualElement CreateSkillRow(string title, string description, string badgeText)
        {
            var row = new VisualElement();
            row.style.backgroundColor = new Color(0.17f, 0.17f, 0.17f);
            row.Rounded(4);
            row.Border(1, MCPPalette.BorderDark);
            row.Padding(5, 7, 5, 7);
            row.style.marginBottom = 4;

            var titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;

            var titleLabel = new Label(title);
            titleLabel.style.flexGrow = 1;
            titleLabel.style.fontSize = 13;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new Color(0.88f, 0.88f, 0.88f);
            titleRow.Add(titleLabel);

            var badge = new Label(badgeText);
            badge.style.fontSize = 11;
            badge.style.color = Color.white;
            badge.style.backgroundColor = MCPPalette.AccentBlue;
            badge.Rounded(3);
            badge.Padding(1, 5, 1, 5);
            titleRow.Add(badge);

            row.Add(titleRow);

            var descriptionLabel = new Label(description);
            descriptionLabel.style.fontSize = 11;
            descriptionLabel.style.color = MCPPalette.TextDim;
            descriptionLabel.style.marginTop = 3;
            descriptionLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(descriptionLabel);

            return row;
        }

        private VisualElement CreatePackageSkillCard(ProjectSkillsManager.SkillDefinition skill)
        {
            var row = new VisualElement();
            row.style.backgroundColor = new Color(0.17f, 0.17f, 0.17f);
            row.Rounded(4);
            row.Border(1, MCPPalette.BorderDark);
            row.Padding(5, 7, 5, 7);
            row.style.marginBottom = 4;

            var titleLabel = new Label(skill.Title);
            titleLabel.style.fontSize = 13;
            titleLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            titleLabel.style.color = new Color(0.88f, 0.88f, 0.88f);
            row.Add(titleLabel);

            var descriptionLabel = new Label(ProjectSkillsManager.ShortDescription(skill.Description));
            descriptionLabel.tooltip = skill.Description;
            descriptionLabel.style.fontSize = 11;
            descriptionLabel.style.color = new Color(0.58f, 0.58f, 0.58f);
            descriptionLabel.style.marginTop = 2;
            descriptionLabel.style.whiteSpace = WhiteSpace.Normal;
            row.Add(descriptionLabel);

            return row;
        }
    }
}
