// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using KitWright.Editor.Services;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// After the MCP server starts, sweeps all known client config files and repairs any
    /// existing entry for this project whose URL no longer matches the live server —
    /// stale ports happen when the server falls forward to a free port (multi-editor)
    /// or the user changes the port setting. Never creates new entries: only files the
    /// user already configured are touched.
    /// </summary>
    internal static class MCPClientConfigAutoRewrite
    {
        /// <summary>Schedule a sweep on the editor thread. Safe to call from any thread.</summary>
        public static void Schedule(int port)
        {
            // The URL is built inside the callback because the pin comes from Application.dataPath,
            // which only the editor thread may read.
            EditorApplication.delayCall += () => Run(ClientConfigPanel.BuildServerUrl(port));
        }

        public static void Run(string serverUrl)
        {
            var rewritten = new List<string>();

            foreach (var target in ClientConfigPanel.GetAllTargets())
            {
                // Sweep both scopes rather than the target's currently selected one: a client
                // configured globally would otherwise keep a stale URL forever, because the
                // selected scope resolves to the project file whenever one is possible.
                // Repairing is safe in both places — an entry is only ever updated, never added.
                foreach (var path in new[] { target.ProjectConfigPath, target.GlobalConfigPath })
                {
                    try
                    {
                        if (string.IsNullOrEmpty(path) || !File.Exists(path))
                            continue;

                        var serverName = ClientConfigPanel.ServerEntryName;

                        // Every project writes the same entry name into the one global file, so an
                        // entry already pinned to a sibling project is that project's — repointing
                        // it would steal its client config. The project-scoped file is this
                        // project's by definition, so a stale pin there is still ours to repair.
                        var global = string.Equals(path, target.GlobalConfigPath, StringComparison.OrdinalIgnoreCase);

                        var changed = target.IsToml
                            ? RewriteToml(path, serverName, serverUrl, global)
                            : RewriteJson(path, target.RootKey, serverName, serverUrl, global);

                        if (changed)
                            rewritten.Add($"{target.Name} ({path})");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[KitWright MCP Server] Config auto-rewrite failed for {target.Name} ({path}): {ex.Message}");
                    }
                }
            }

            if (rewritten.Count > 0)
                Debug.Log($"[KitWright MCP Server] Updated stale MCP config URL to {serverUrl} for:\n{string.Join("\n", rewritten)}\nRestart or reload the client(s) to reconnect.");
        }

        internal static bool RewriteJson(
            string configPath, string targetRootKey, string serverName, string serverUrl,
            bool leaveOtherProjectsAlone = false)
        {
            var json = File.ReadAllText(configPath);
            if (!(JsonCodec.Deserialize(json) is Dictionary<string, object> root))
                return false;

            var rootKey = string.IsNullOrEmpty(targetRootKey) ? "mcpServers" : targetRootKey;
            if (!(root.TryGetValue(rootKey, out var serversObj) && serversObj is Dictionary<string, object> servers))
                return false;
            if (!(servers.TryGetValue(serverName, out var entryObj) && entryObj is Dictionary<string, object> entry))
                return false;

            // Clients diverge on the URL property name ("url" / "serverUrl" / "httpUrl");
            // update whichever the entry actually uses.
            var changed = false;
            foreach (var key in new[] { "url", "serverUrl", "httpUrl" })
            {
                if (entry.TryGetValue(key, out var value) &&
                    value is string existing &&
                    !UrlsEqual(existing, serverUrl))
                {
                    if (leaveOtherProjectsAlone && TargetsAnotherProject(existing))
                    {
                        WarnLeftAlone(configPath, existing);
                        continue;
                    }

                    entry[key] = serverUrl;
                    changed = true;
                }
            }

            if (changed)
                File.WriteAllText(configPath, JsonCodec.Serialize(root));

            return changed;
        }

        private static bool RewriteToml(
            string path, string serverName, string serverUrl, bool leaveOtherProjectsAlone = false)
        {
            var content = File.ReadAllText(path);
            var header = "[mcp_servers." + serverName + "]";
            var start = content.IndexOf(header, StringComparison.Ordinal);
            if (start < 0)
                return false;

            var end = content.IndexOf("\n[", start + header.Length, StringComparison.Ordinal);
            if (end < 0)
                end = content.Length;

            var section = content.Substring(start, end - start);
            if (leaveOtherProjectsAlone)
            {
                var current = Regex.Match(section, "url\\s*=\\s*\"([^\"]*)\"");
                if (current.Success && TargetsAnotherProject(current.Groups[1].Value))
                {
                    WarnLeftAlone(path, current.Groups[1].Value);
                    return false;
                }
            }

            var updated = Regex.Replace(section, "url\\s*=\\s*\"[^\"]*\"", "url = \"" + serverUrl + "\"");
            if (updated == section)
                return false;

            File.WriteAllText(path, content.Substring(0, start) + updated + content.Substring(end));
            return true;
        }

        // A suppressed repair is also what a project that moved to a new folder sees: its own old
        // pin now reads as a sibling's, and the client just stops working. Silence there is
        // indistinguishable from a bug, so name the file and the way out.
        private static void WarnLeftAlone(string configPath, string existingUrl)
        {
            Debug.LogWarning(
                $"[KitWright MCP Server] Left the '{ClientConfigPanel.ServerEntryName}' entry in '{configPath}' alone: " +
                $"'{existingUrl}' is pinned to a different project. If this project moved, press Configure to claim " +
                "the entry for its current path.");
        }

        private static bool TargetsAnotherProject(string url)
        {
            var pin = HttpMCPTransport.ExtractPin(url);
            if (pin.Length == 0)
                return false;

            var ours = ProjectIdentity.PinFromProjectPath(ApplicationPaths.ProjectRoot);
            return !string.Equals(pin, ours, StringComparison.OrdinalIgnoreCase);
        }

        private static bool UrlsEqual(string a, string b)
        {
            return string.Equals(
                a?.Trim().TrimEnd('/'),
                b?.Trim().TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
