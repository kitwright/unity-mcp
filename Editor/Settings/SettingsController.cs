// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using UnityEngine;

namespace KitWright.Editor.Settings
{
    internal class SettingsController
    {
        private const string SettingsDirectoryName = "UserSettings";
        private const string SettingsFileName = "KitWrightMcpSettings.json";
        private const int DefaultPort = 8765;
        private const int MaxPort = 65535;
        private const string DefaultToolExportProfile = "core";
        private const string DefaultSelectedConfigTarget = "Claude Code";
        private const bool DefaultExecuteCodeSafetyChecksEnabled = true;
        private const bool DefaultExecuteCodeStrictFilesystemSafetyEnabled = true;
        private const bool DefaultExecuteCodeProjectNamespaceInjectionEnabled = false;
        private const bool DefaultPluginDebugLoggingEnabled = false;
        private const bool DefaultMCPBrokerModeEnabled = true;
        private const bool DefaultMCPAutostartEnabled = true;
        private const int DefaultScreenshotSize = 512;
        private const int DefaultEditorWindowScreenshotSize = 512;
        private const bool DefaultMCPCompactSchemaEnabled = false;
        private const int DefaultActivityLogCapacity = 200;
        internal const bool DefaultRequireClientApprovalEnabled = false;

        private readonly string _settingsPath;
        private readonly object _lock = new object();
        private SettingsData _settings;

        // projectPath is only passed by tests pointing at a temp project; production resolves it.
        public SettingsController(string projectPath = null)
        {
            projectPath = string.IsNullOrEmpty(projectPath) ? ApplicationPaths.ProjectRoot : projectPath;

            _settingsPath = Path.Combine(
                projectPath,
                SettingsDirectoryName,
                SettingsFileName);
            // Only a project with no settings file yet gets the derived port. An existing file
            // holding 8765 may hold it because the user typed it — a firewall rule, an SSH tunnel
            // or a hand-written client config can depend on that — and nothing on disk tells the
            // deliberate 8765 apart from the old shared default.
            var firstRun = !File.Exists(_settingsPath);
            _settings = LoadSettings();
            if (firstRun)
                DeriveProjectPort(projectPath);
        }

        // The port a project defaults to depended on which editor started first, because every
        // project asked for the same one and lost the race to the fall-forward scan. Deriving it
        // from the project path keeps this project on the same port across restarts.
        private void DeriveProjectPort(string projectPath)
        {
            lock (_lock)
            {
                _settings.port = DefaultPort + ProjectIdentity.PortOffsetFromProjectPath(projectPath);
                SaveSettings(_settings);
            }
        }

        public event Action OnSettingsChanged;

        public bool MCPServerEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.enabled;
            }
            set
            {
                UpdateSettings(data => data.enabled = value);
            }
        }

        public int MCPServerPort
        {
            get
            {
                lock (_lock)
                    return _settings.port;
            }
            set
            {
                // Out of TCP range is as unusable as <= 0, so it falls back the same way.
                var normalized = value > 0 && value <= MaxPort ? value : DefaultPort;
                UpdateSettings(data => data.port = normalized);
            }
        }

        public bool RequireClientApprovalEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.requireClientApprovalEnabled;
            }
            set
            {
                UpdateSettings(data => data.requireClientApprovalEnabled = value);
            }
        }

        public int ScreenshotDefaultSize
        {
            get
            {
                lock (_lock)
                    return _settings.screenshotDefaultSize > 0 ? _settings.screenshotDefaultSize : DefaultScreenshotSize;
            }
            set
            {
                var normalized = Mathf.Clamp(value > 0 ? value : DefaultScreenshotSize, 64, 4096);
                UpdateSettings(data => data.screenshotDefaultSize = normalized);
            }
        }

        public int EditorWindowScreenshotSize
        {
            get
            {
                lock (_lock)
                    return _settings.editorWindowScreenshotSize > 0 ? _settings.editorWindowScreenshotSize : DefaultEditorWindowScreenshotSize;
            }
            set
            {
                var normalized = Mathf.Clamp(value > 0 ? value : DefaultEditorWindowScreenshotSize, 64, 4096);
                UpdateSettings(data => data.editorWindowScreenshotSize = normalized);
            }
        }

        public bool MCPCompactSchemaEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.mcpCompactSchemaEnabled;
            }
            set
            {
                UpdateSettings(data => data.mcpCompactSchemaEnabled = value);
            }
        }

        public int ActivityLogCapacity
        {
            get
            {
                lock (_lock)
                    return _settings.activityLogCapacity > 0 ? _settings.activityLogCapacity : DefaultActivityLogCapacity;
            }
            set
            {
                var normalized = Mathf.Clamp(value > 0 ? value : DefaultActivityLogCapacity, 50, 1000);
                UpdateSettings(data => data.activityLogCapacity = normalized);
            }
        }

        public string MCPToolExportProfile
        {
            get
            {
                lock (_lock)
                    return _settings.toolExportProfile;
            }
            set
            {
                var normalized = NormalizeToolExportProfile(value);
                UpdateSettings(data => data.toolExportProfile = normalized);
            }
        }

        public bool IsProfileConfigured(string profile)
        {
            var key = NormalizeToolExportProfile(profile);
            lock (_lock)
                return FindProfileList(_settings, key)?.custom ?? false;
        }

        public string[] GetProfileTools(string profile)
        {
            var key = NormalizeToolExportProfile(profile);
            lock (_lock)
                return FindProfileList(_settings, key)?.tools?.ToArray() ?? Array.Empty<string>();
        }

        public void SetProfileTools(string profile, string[] tools)
        {
            var key = NormalizeToolExportProfile(profile);
            UpdateSettings(data =>
            {
                var entry = FindProfileList(data, key);
                if (entry == null)
                {
                    entry = new ProfileToolList { profile = key };
                    data.profileTools.Add(entry);
                }
                entry.custom = tools != null;
                entry.tools = NormalizeToolNames(tools);
            });
        }

        private static ProfileToolList FindProfileList(SettingsData data, string profile)
        {
            if (data.profileTools == null)
                return null;
            return data.profileTools.FirstOrDefault(
                p => string.Equals(p.profile, profile, StringComparison.OrdinalIgnoreCase));
        }

        public string MCPSelectedConfigTarget
        {
            get
            {
                lock (_lock)
                    return _settings.selectedConfigTarget;
            }
            set
            {
                var normalized = NormalizeSelectedConfigTarget(value);
                UpdateSettings(data => data.selectedConfigTarget = normalized);
            }
        }

        public bool ExecuteCodeSafetyChecksEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.executeCodeSafetyChecksEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.executeCodeSafetyChecksEnabled = value;
                    data.executeCodeSafetyChecksConfigured = true;
                });
            }
        }

        public bool ExecuteCodeStrictFilesystemSafetyEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.executeCodeStrictFilesystemSafetyEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.executeCodeStrictFilesystemSafetyEnabled = value;
                    data.executeCodeStrictFilesystemSafetyConfigured = true;
                });
            }
        }

        public bool ExecuteCodeProjectNamespaceInjectionEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.executeCodeProjectNamespaceInjectionEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.executeCodeProjectNamespaceInjectionEnabled = value;
                    data.executeCodeProjectNamespaceInjectionConfigured = true;
                });
            }
        }

        public bool PluginDebugLoggingEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.pluginDebugLoggingEnabled;
            }
            set
            {
                UpdateSettings(data =>
                {
                    data.pluginDebugLoggingEnabled = value;
                    data.pluginDebugLoggingConfigured = true;
                });
            }
        }

        public bool MCPBrokerModeEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.mcpBrokerModeEnabled;
            }
            set
            {
                UpdateSettings(data => data.mcpBrokerModeEnabled = value);
            }
        }

        public bool MCPAutostartEnabled
        {
            get
            {
                lock (_lock)
                    return _settings.mcpAutostartEnabled;
            }
            set
            {
                UpdateSettings(data => data.mcpAutostartEnabled = value);
            }
        }

        public string MCPBrokerMonoPath
        {
            get
            {
                lock (_lock)
                    return _settings.mcpBrokerMonoPath ?? string.Empty;
            }
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
                UpdateSettings(data => data.mcpBrokerMonoPath = normalized);
            }
        }

        private void UpdateSettings(Action<SettingsData> apply)
        {
            if (apply == null) return;

            var changed = false;
            lock (_lock)
            {
                var beforeJson = JsonUtility.ToJson(_settings);
                apply(_settings);
                NormalizeInPlace(_settings);
                var afterJson = JsonUtility.ToJson(_settings);
                if (string.Equals(beforeJson, afterJson, StringComparison.Ordinal))
                    return;

                SaveSettings(_settings);
                changed = true;
            }

            if (changed)
                OnSettingsChanged?.Invoke();
        }

        private SettingsData LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    var json = File.ReadAllText(_settingsPath);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonUtility.FromJson<SettingsData>(json);
                        if (loaded != null)
                        {
                            var beforeNormalizeJson = JsonUtility.ToJson(loaded);
                            NormalizeInPlace(loaded);
                            var afterNormalizeJson = JsonUtility.ToJson(loaded);
                            if (!string.Equals(beforeNormalizeJson, afterNormalizeJson, StringComparison.Ordinal))
                                SaveSettings(loaded);
                            return loaded;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright] Failed to read MCP settings file '{_settingsPath}': {ex.Message}");
            }

            var defaults = CreateDefaultSettings();
            SaveSettings(defaults);
            return defaults;
        }

        private void SaveSettings(SettingsData settings)
        {
            try
            {
                var directory = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var json = JsonUtility.ToJson(settings, true);
                File.WriteAllText(_settingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright] Failed to write MCP settings file '{_settingsPath}': {ex.Message}");
            }
        }

        private static SettingsData CreateDefaultSettings()
        {
            return new SettingsData
            {
                enabled = false,
                port = DefaultPort,
                toolExportProfile = DefaultToolExportProfile,
                selectedConfigTarget = DefaultSelectedConfigTarget,
                executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled,
                executeCodeSafetyChecksConfigured = true,
                executeCodeStrictFilesystemSafetyEnabled = DefaultExecuteCodeStrictFilesystemSafetyEnabled,
                executeCodeStrictFilesystemSafetyConfigured = true,
                executeCodeProjectNamespaceInjectionEnabled = DefaultExecuteCodeProjectNamespaceInjectionEnabled,
                executeCodeProjectNamespaceInjectionConfigured = true,
                pluginDebugLoggingEnabled = DefaultPluginDebugLoggingEnabled,
                pluginDebugLoggingConfigured = true,
                mcpBrokerModeEnabled = DefaultMCPBrokerModeEnabled,
                mcpBrokerMonoPath = string.Empty,
                mcpAutostartEnabled = DefaultMCPAutostartEnabled
            };
        }

        private static void NormalizeInPlace(SettingsData settings)
        {
            if (settings == null)
                return;

            // Out of TCP range is as unusable as <= 0, and this is the path a hand-edited file takes
            // -- the property setter never sees it.
            settings.port = settings.port > 0 && settings.port <= MaxPort ? settings.port : DefaultPort;
            settings.activityLogCapacity = settings.activityLogCapacity > 0 ? settings.activityLogCapacity : DefaultActivityLogCapacity;
            settings.mcpBrokerMonoPath = settings.mcpBrokerMonoPath ?? string.Empty;
            settings.toolExportProfile = NormalizeToolExportProfile(settings.toolExportProfile);
            settings.profileTools = settings.profileTools ?? new List<ProfileToolList>();
            foreach (var entry in settings.profileTools)
            {
                entry.profile = NormalizeToolExportProfile(entry.profile);
                entry.tools = entry.custom ? NormalizeToolNames(entry.tools) : null;
            }
            settings.selectedConfigTarget = NormalizeSelectedConfigTarget(settings.selectedConfigTarget);
            if (!settings.executeCodeSafetyChecksConfigured)
            {
                settings.executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled;
                settings.executeCodeSafetyChecksConfigured = true;
            }
            if (!settings.executeCodeStrictFilesystemSafetyConfigured)
            {
                settings.executeCodeStrictFilesystemSafetyEnabled = DefaultExecuteCodeStrictFilesystemSafetyEnabled;
                settings.executeCodeStrictFilesystemSafetyConfigured = true;
            }
            if (!settings.executeCodeProjectNamespaceInjectionConfigured)
            {
                settings.executeCodeProjectNamespaceInjectionEnabled = DefaultExecuteCodeProjectNamespaceInjectionEnabled;
                settings.executeCodeProjectNamespaceInjectionConfigured = true;
            }
            if (!settings.pluginDebugLoggingConfigured)
            {
                settings.pluginDebugLoggingEnabled = DefaultPluginDebugLoggingEnabled;
                settings.pluginDebugLoggingConfigured = true;
            }
        }

        private static string NormalizeToolExportProfile(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DefaultToolExportProfile : value.Trim().ToLowerInvariant();
        }

        private static string NormalizeSelectedConfigTarget(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? DefaultSelectedConfigTarget : value.Trim();
        }

        private static List<string> NormalizeToolNames(IEnumerable<string> values)
        {
            if (values == null)
                return null;

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        [Serializable]
        private class SettingsData
        {
            public bool enabled = false;
            public int port = DefaultPort;
            public string toolExportProfile = DefaultToolExportProfile;
            public List<ProfileToolList> profileTools = new List<ProfileToolList>();
            public string selectedConfigTarget = DefaultSelectedConfigTarget;
            public bool executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled;
            public bool executeCodeSafetyChecksConfigured = false;
            public bool executeCodeStrictFilesystemSafetyEnabled = DefaultExecuteCodeStrictFilesystemSafetyEnabled;
            public bool executeCodeStrictFilesystemSafetyConfigured = false;
            public bool executeCodeProjectNamespaceInjectionEnabled = DefaultExecuteCodeProjectNamespaceInjectionEnabled;
            public bool executeCodeProjectNamespaceInjectionConfigured = false;
            public bool pluginDebugLoggingEnabled = DefaultPluginDebugLoggingEnabled;
            public bool pluginDebugLoggingConfigured = false;
            public bool mcpBrokerModeEnabled = DefaultMCPBrokerModeEnabled;
            public string mcpBrokerMonoPath = string.Empty;
            public bool mcpAutostartEnabled = DefaultMCPAutostartEnabled;
            public int screenshotDefaultSize = DefaultScreenshotSize;
            public int editorWindowScreenshotSize = DefaultEditorWindowScreenshotSize;
            public bool mcpCompactSchemaEnabled = DefaultMCPCompactSchemaEnabled;
            public int activityLogCapacity = DefaultActivityLogCapacity;
            public bool requireClientApprovalEnabled = DefaultRequireClientApprovalEnabled;
        }

        [Serializable]
        private class ProfileToolList
        {
            public string profile;
            public bool custom = false;
            public List<string> tools;
        }
    }
}
