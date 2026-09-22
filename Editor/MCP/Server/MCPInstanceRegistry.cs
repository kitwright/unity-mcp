// Copyright (C) KitWright. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using KitWright.Editor.Settings;
using Newtonsoft.Json;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Per-user record of which editor is serving which port. The server picks its port from
    /// settings and shifts on a collision, so with several projects open the mapping is only
    /// known inside each editor — this writes it somewhere a second project (or the user
    /// wiring up an MCP client config) can read it.
    ///
    /// Written when the transport starts, removed when it stops. An editor that crashes leaves
    /// its file behind, so entries whose process is gone are pruned on the next write.
    /// </summary>
    internal static class MCPInstanceRegistry
    {
        // Test seam: point the registry at a temp directory instead of the user profile.
        internal static string RootOverride;

        internal sealed class InstanceEntry
        {
            public int port;
            public string projectPath;
            public string projectName;
            public string projectIdentity;
            public int pid;
        }

        public static void Publish(int port, string projectPath, string projectName, string projectIdentity)
        {
            try
            {
                var path = EntryPath(projectPath);
                if (path == null)
                    return;

                Directory.CreateDirectory(RootDirectory);
                PruneDeadEntries();

                var entry = new InstanceEntry
                {
                    port = port,
                    projectPath = projectPath,
                    projectName = projectName,
                    projectIdentity = projectIdentity,
                    pid = Process.GetCurrentProcess().Id
                };

                File.WriteAllText(path, JsonConvert.SerializeObject(entry, Formatting.Indented));
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log($"[KitWright] Could not publish instance registry entry: {ex.Message}");
            }
        }

        public static void Remove(string projectPath)
        {
            try
            {
                var path = EntryPath(projectPath);
                if (path != null && File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log($"[KitWright] Could not remove instance registry entry: {ex.Message}");
            }
        }

        internal static string RootDirectory =>
            RootOverride ?? Path.Combine(UserRoot(), "KitWright", "instances");

        // LocalApplicationData is %LOCALAPPDATA% on Windows and the XDG data dir under Mono,
        // so one branch covers every editor platform. Only the fallback needs a home path.
        private static string UserRoot()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                return local;

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        private static string EntryPath(string projectPath)
        {
            var pin = ProjectIdentity.PinFromProjectPath(projectPath);
            return string.IsNullOrEmpty(pin) ? null : Path.Combine(RootDirectory, pin + ".json");
        }

        // Only files this registry wrote and can still parse are removed, so an unrelated file
        // dropped into the directory is left alone.
        private static void PruneDeadEntries()
        {
            foreach (var file in Directory.GetFiles(RootDirectory, "*.json"))
            {
                var entry = TryRead(file);
                if (entry == null || IsProcessAlive(entry.pid))
                    continue;

                try { File.Delete(file); }
                catch (Exception ex) { PluginDebugLogger.Log($"[KitWright] Could not prune {file}: {ex.Message}"); }
            }
        }

        private static InstanceEntry TryRead(string file)
        {
            try
            {
                var entry = JsonConvert.DeserializeObject<InstanceEntry>(File.ReadAllText(file));
                return entry != null && entry.pid > 0 && entry.port > 0 ? entry : null;
            }
            catch
            {
                return null;
            }
        }

        // A recycled pid can keep a stale entry alive; the endpoint check the reader has to do
        // anyway settles that, and guessing harder here would only delete live entries.
        private static bool IsProcessAlive(int pid)
        {
            try
            {
                using (Process.GetProcessById(pid))
                    return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }
}
