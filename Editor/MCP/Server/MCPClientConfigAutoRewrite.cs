// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Helpers;
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
                Debug.Log($"[KitWright MCP Server] Updated stale MCP config URL to {ServerToken.Redact(serverUrl)} for:\n{string.Join("\n", rewritten)}\nRestart or reload the client(s) to reconnect.");

            WarnAboutCommittableTokenCarriers(serverUrl);
        }

        // Every start, not only on a rewrite: a config that already carried the current URL when it
        // was committed is the leak, and it never needs rewriting.
        private static void WarnAboutCommittableTokenCarriers(string serverUrl)
        {
            var token = ServerToken.ExtractToken(serverUrl);
            if (token.Length == 0)
                return;

            // The .bak Configure leaves next to a config holds the URL it replaced, token included.
            var carriers = ClientConfigPanel.GetAllTargets()
                .Select(target => target.ProjectConfigPath)
                .Where(path => !string.IsNullOrEmpty(path))
                .SelectMany(path => new[] { path, path + ".bak" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => Carries(path, token))
                .ToList();

            if (carriers.Count > 0)
                System.Threading.Tasks.Task.Run(() => carriers.ForEach(ServerToken.WarnIfGitWouldCommit));
        }

        internal static bool Carries(string path, string token)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).Contains(token);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // A client holding its config open is not a leak this sweep can see.
                return false;
            }
        }

        internal static bool RewriteJson(
            string configPath, string targetRootKey, string serverName, string serverUrl,
            bool leaveOtherProjectsAlone = false)
        {
            var json = ClientConfigPanel.ReadExistingConfig(configPath);
            if (json == null)
                return false;

            if (!(JsonCodec.Deserialize(JsonCodec.StripComments(json)) is Dictionary<string, object> root))
                return false;

            var rootKey = string.IsNullOrEmpty(targetRootKey) ? "mcpServers" : targetRootKey;
            if (!(root.TryGetValue(rootKey, out var serversObj) && serversObj is Dictionary<string, object> servers))
                return false;
            if (!(servers.TryGetValue(serverName, out var entryObj) && entryObj is Dictionary<string, object> entry))
                return false;

            // Clients diverge on the URL property name ("url" / "serverUrl" / "httpUrl");
            // update whichever the entry actually uses.
            var stale = new List<string>();
            foreach (var key in ClientConfigPanel.UrlProperties)
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

                    stale.Add(existing);
                }
            }

            if (stale.Count == 0)
                return false;

            // The text is edited, not the parsed tree written back: the codec reads no comments or
            // trailing commas, so serializing what it did parse dropped every key after one.
            var updated = json;
            foreach (var url in stale.Distinct())
            {
                var literal = Newtonsoft.Json.JsonConvert.ToString(url);
                if (!updated.Contains(literal))
                {
                    Debug.LogWarning($"[KitWright MCP Server] Config auto-rewrite left {configPath} alone: its URL is written in a form this rewrite cannot replace in place.");
                    return false;
                }

                updated = updated.Replace(literal, Newtonsoft.Json.JsonConvert.ToString(serverUrl));
            }

            AtomicFile.WriteAllText(configPath, updated);
            return true;
        }

        internal static bool RewriteToml(
            string path, string serverName, string serverUrl, bool leaveOtherProjectsAlone = false)
        {
            var content = ClientConfigPanel.ReadExistingConfig(path);
            if (content == null || !ClientConfigPanel.TryFindMcpServerTable(content, serverName, out var start, out var end))
                return false;

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

            var updated = ClientConfigPanel.SetTomlUrl(section, serverUrl);
            if (updated == section)
                return false;

            AtomicFile.WriteAllText(path, content.Substring(0, start) + updated + content.Substring(end));
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
