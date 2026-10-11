// Copyright (C) KitWright. All rights reserved.

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
            if (!settings.executeCodeSafetyChecksConfigured)
            {
                settings.executeCodeSafetyChecksEnabled = DefaultExecuteCodeSafetyChecksEnabled;
                settings.executeCodeSafetyChecksConfigured = true;
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
