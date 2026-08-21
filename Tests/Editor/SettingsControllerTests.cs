// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Settings;
using NUnit.Framework;

namespace KitWright.Editor
{
    public sealed class SettingsControllerTests
    {
        [Test]
        public void NewSettings_EnableExecuteCodeSafetyChecksByDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);

                Assert.IsTrue(controller.ExecuteCodeSafetyChecksEnabled);
                Assert.IsTrue(controller.ExecuteCodeStrictFilesystemSafetyEnabled);
                Assert.IsFalse(controller.ExecuteCodeProjectNamespaceInjectionEnabled);
                Assert.IsFalse(controller.PluginDebugLoggingEnabled);
                Assert.IsTrue(controller.MCPBrokerModeEnabled);
                Assert.IsFalse(controller.RequireClientApprovalEnabled);
                Assert.AreEqual(string.Empty, controller.MCPBrokerMonoPath);
                StringAssert.Contains("\"executeCodeSafetyChecksEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerModeEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerMonoPath\": \"\"", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExistingSettingsWithoutSafetyField_MigrateToEnabledDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var settingsDirectory = Path.Combine(projectPath, "UserSettings");
                Directory.CreateDirectory(settingsDirectory);
                File.WriteAllText(
                    Path.Combine(settingsDirectory, "KitWrightMcpSettings.json"),
                    "{\"enabled\":false,\"port\":8765,\"toolExportProfile\":\"core\"}");

                var controller = new SettingsController(projectPath);

                Assert.IsTrue(controller.ExecuteCodeSafetyChecksEnabled);
                Assert.IsTrue(controller.ExecuteCodeStrictFilesystemSafetyEnabled);
                Assert.IsFalse(controller.ExecuteCodeProjectNamespaceInjectionEnabled);
                Assert.IsFalse(controller.PluginDebugLoggingEnabled);
                Assert.IsTrue(controller.MCPBrokerModeEnabled);
                Assert.IsFalse(controller.RequireClientApprovalEnabled);
                Assert.AreEqual(string.Empty, controller.MCPBrokerMonoPath);
                StringAssert.Contains("\"executeCodeSafetyChecksEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingConfigured\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerModeEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerMonoPath\": \"\"", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExecuteCodeStrictFilesystemSafetySetting_PersistsFalseValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);
                controller.ExecuteCodeStrictFilesystemSafetyEnabled = false;

                var reloaded = new SettingsController(projectPath);

                Assert.IsFalse(reloaded.ExecuteCodeStrictFilesystemSafetyEnabled);
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeStrictFilesystemSafetyConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExecuteCodeSafetyChecksSetting_PersistsFalseValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);
                controller.ExecuteCodeSafetyChecksEnabled = false;

                var reloaded = new SettingsController(projectPath);

                Assert.IsFalse(reloaded.ExecuteCodeSafetyChecksEnabled);
                StringAssert.Contains("\"executeCodeSafetyChecksEnabled\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void ExecuteCodeProjectNamespaceInjectionSetting_PersistsTrueValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);
                controller.ExecuteCodeProjectNamespaceInjectionEnabled = true;

                var reloaded = new SettingsController(projectPath);

                Assert.IsTrue(reloaded.ExecuteCodeProjectNamespaceInjectionEnabled);
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeProjectNamespaceInjectionConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void PluginDebugLoggingSetting_PersistsTrueValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);
                controller.PluginDebugLoggingEnabled = true;

                var reloaded = new SettingsController(projectPath);

                Assert.IsTrue(reloaded.PluginDebugLoggingEnabled);
                StringAssert.Contains("\"pluginDebugLoggingEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"pluginDebugLoggingConfigured\": true", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void BrokerSettings_PersistValues()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);
                controller.MCPBrokerModeEnabled = true;
                controller.MCPBrokerMonoPath = "  /tmp/unity-mono  ";

                var reloaded = new SettingsController(projectPath);

                Assert.IsTrue(reloaded.MCPBrokerModeEnabled);
                Assert.AreEqual("/tmp/unity-mono", reloaded.MCPBrokerMonoPath);
                StringAssert.Contains("\"mcpBrokerModeEnabled\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"mcpBrokerMonoPath\": \"/tmp/unity-mono\"", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void Port_IsDerivedFromProjectPath_ForAProjectWithNoSettingsFile()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var port = new SettingsController(projectPath).MCPServerPort;

                Assert.AreEqual(8765 + ProjectIdentity.PortOffsetFromProjectPath(projectPath), port);
                Assert.AreEqual(0, (port - 8765) % 10, "Derived ports sit on the 10-apart slots.");
                StringAssert.Contains($"\"port\": {port}", ReadSettingsJson(projectPath));
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        // An existing file holding 8765 is indistinguishable from a user who typed 8765, so it is
        // left alone. This is the assertion that fails if the derivation is ever moved back onto
        // the load path.
        [Test]
        public void Port_IsNeverDerivedForAProjectThatAlreadyHasSettings()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                Directory.CreateDirectory(Path.Combine(projectPath, "UserSettings"));
                File.WriteAllText(
                    Path.Combine(projectPath, "UserSettings", "KitWrightMcpSettings.json"),
                    "{\"enabled\":false,\"port\":8765}");

                Assert.AreEqual(8765, new SettingsController(projectPath).MCPServerPort);
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        [Test]
        public void Port_OutOfTcpRangeInTheFileFallsBackToTheDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                Directory.CreateDirectory(Path.Combine(projectPath, "UserSettings"));
                File.WriteAllText(
                    Path.Combine(projectPath, "UserSettings", "KitWrightMcpSettings.json"),
                    "{\"enabled\":false,\"port\":70000}");

                Assert.AreEqual(8765, new SettingsController(projectPath).MCPServerPort);
            }
            finally
            {
                DeleteTempProjectPath(projectPath);
            }
        }

        private static string ReadSettingsJson(string projectPath)
        {
            return File.ReadAllText(Path.Combine(projectPath, "UserSettings", "KitWrightMcpSettings.json"));
        }

        private static string CreateTempProjectPath()
        {
            var path = Path.Combine(Path.GetTempPath(), "KitWrightSettingsTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempProjectPath(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
