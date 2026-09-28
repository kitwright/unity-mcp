// Copyright (C) KitWright. All rights reserved.

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
        public void NewSettings_LeaveExecuteCodeSafetyChecksUnlockedByDefault()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);

                Assert.IsFalse(controller.ExecuteCodeSafetyChecksLocked);
                Assert.IsFalse(controller.ExecuteCodeProjectNamespaceInjectionEnabled);
                Assert.IsFalse(controller.PluginDebugLoggingEnabled);
                Assert.IsTrue(controller.MCPBrokerModeEnabled);
                Assert.AreEqual(string.Empty, controller.MCPBrokerMonoPath);
                StringAssert.Contains("\"executeCodeSafetyChecksLocked\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksLockedConfigured\": true", ReadSettingsJson(projectPath));
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
        public void ExistingSettingsWithoutSafetyField_MigrateToUnlockedDefault()
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

                Assert.IsFalse(controller.ExecuteCodeSafetyChecksLocked);
                Assert.IsFalse(controller.ExecuteCodeProjectNamespaceInjectionEnabled);
                Assert.IsFalse(controller.PluginDebugLoggingEnabled);
                Assert.IsTrue(controller.MCPBrokerModeEnabled);
                Assert.AreEqual(string.Empty, controller.MCPBrokerMonoPath);
                StringAssert.Contains("\"executeCodeSafetyChecksLocked\": false", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksLockedConfigured\": true", ReadSettingsJson(projectPath));
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
        public void ExecuteCodeSafetyChecksLockedSetting_PersistsTrueValue()
        {
            var projectPath = CreateTempProjectPath();

            try
            {
                var controller = new SettingsController(projectPath);
                controller.ExecuteCodeSafetyChecksLocked = true;

                var reloaded = new SettingsController(projectPath);

                Assert.IsTrue(reloaded.ExecuteCodeSafetyChecksLocked);
                StringAssert.Contains("\"executeCodeSafetyChecksLocked\": true", ReadSettingsJson(projectPath));
                StringAssert.Contains("\"executeCodeSafetyChecksLockedConfigured\": true", ReadSettingsJson(projectPath));
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
