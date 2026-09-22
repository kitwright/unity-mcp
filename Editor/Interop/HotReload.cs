// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KitWright.Editor.Interop
{
    /// <summary>
    /// Reflection-only integration with SingularityGroup Hot Reload.
    /// It detours AssetDatabase.Refresh / RequestScriptCompilation into no-ops and
    /// patches method bodies in-memory, so while loaded: sources always look newer
    /// than assemblies and MCP must never force a script reload.
    /// </summary>
    internal static class HotReload
    {
        public const string DisplayName = "Hot Reload (SingularityGroup)";

        private const string AssemblyPrefix = "SingularityGroup.HotReload";
        internal const string EditorAssembly = "SingularityGroup.HotReload.Editor";
        internal const string DetourerType = "SingularityGroup.HotReload.Editor.CompileMethodDetourer";
        internal const string DetourField = "detouredMethod";
        private const string TimelineRelativePath = "Library/com.singularitygroup.hotreload/eventEntries.json";
        private const int MaxTimelineEntries = 20;

        // Values of SingularityGroup.HotReload.Editor.AlertEntryType.
        private static readonly string[] AlertEntryTypeNames =
        {
            "Error", "Failure", "InlinedMethod", "PatchApplied", "PartiallySupportedChange", "UndetectedChange"
        };

        /// <summary>Plugin assemblies are present, so it detours the refresh/compile pipeline.</summary>
        public static bool IsLoaded
        {
            get
            {
                try
                {
                    return AppDomain.CurrentDomain.GetAssemblies().Any(a =>
                    {
                        var name = a.GetName().Name;
                        return name != null && name.StartsWith(AssemblyPrefix, StringComparison.OrdinalIgnoreCase);
                    });
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// True only while the detour is actually installed. The plugin applies it from
        /// <c>EditorCodePatcher.CheckAssetDatabaseRefresh</c> when its own
        /// <c>disableCompilingFromEditorScripts</c> setting is on AND the server is healthy, so a
        /// healthy server on its own proves nothing — with that setting off Unity still compiles
        /// normally. Falls back to the old guess only when the field cannot be read.
        /// </summary>
        public static bool IsSuppressingCompilation =>
            ResolveSuppression(IsLoaded, TryGetDetourInstalled(), TryGetServerHealthy());

        internal static bool ResolveSuppression(bool loaded, bool? detourInstalled, bool? serverHealthy) =>
            loaded && (detourInstalled ?? (serverHealthy ?? true));

        public static object GetStatus()
        {
            var loaded = IsLoaded;
            return new
            {
                id = "hot-reload",
                display_name = DisplayName,
                loaded,
                server_healthy = TryGetServerHealthy(),
                compile_detour_installed = TryGetDetourInstalled(),
                suppresses_compilation = IsSuppressingCompilation,
                timeline = loaded ? ReadTimeline() : null
            };
        }

        /// <summary>CompileMethodDetourer.detouredMethod via reflection; null when unavailable.</summary>
        private static bool? TryGetDetourInstalled()
        {
            try
            {
                var field = FindEditorType(DetourerType)
                    ?.GetField(DetourField, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

                return field?.GetValue(null) as bool?;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ServerHealthCheck.I.IsServerHealthy via reflection; null when unavailable.</summary>
        private static bool? TryGetServerHealthy()
        {
            try
            {
                var type = FindEditorType("SingularityGroup.HotReload.Editor.ServerHealthCheck");
                var instance = type?.GetProperty("I", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var healthy = instance?.GetType().GetProperty("IsServerHealthy")?.GetValue(instance);
                return healthy as bool?;
            }
            catch
            {
                return null;
            }
        }

        private static Type FindEditorType(string fullName)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, EditorAssembly, StringComparison.OrdinalIgnoreCase));
            return assembly?.GetType(fullName, false);
        }

        /// <summary>
        /// Patch history: live in-memory timeline via reflection first
        /// (HotReloadTimelineHelper.EventsTimeline), falling back to the persisted JSON
        /// (only written before assembly reloads, and cleared when the plugin restarts).
        /// </summary>
        private static object ReadTimeline()
        {
            return ReadLiveTimeline() ?? ReadPersistedTimeline();
        }

        private static object ReadLiveTimeline()
        {
            try
            {
                var helper = FindEditorType("SingularityGroup.HotReload.Editor.HotReloadTimelineHelper");
                var timeline = helper?.GetProperty("EventsTimeline", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as System.Collections.IEnumerable;
                if (timeline == null)
                    return null;

                var entries = new List<object>();
                foreach (var entry in timeline)
                {
                    if (entries.Count >= MaxTimelineEntries)
                        break;

                    var type = entry.GetType();
                    object Field(string name) =>
                        type.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)?.GetValue(entry);

                    var alertData = Field("alertData");
                    var patchedMembers = alertData?.GetType()
                        .GetField("patchedMembersDisplayNames", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(alertData) as string[];

                    entries.Add(new
                    {
                        created_at = Field("timestamp") is DateTime dt ? dt.ToString("O") : null,
                        entry_type = Field("alertType")?.ToString(),
                        title = Field("title") as string,
                        description = Field("shortDescription") as string ?? Field("description") as string,
                        patched_members = patchedMembers
                    });
                }

                return new { available = true, source = "live", entries };
            }
            catch
            {
                return null;
            }
        }

        private static object ReadPersistedTimeline()
        {
            try
            {
                var path = Path.Combine(GetProjectRoot(), TimelineRelativePath);
                if (!File.Exists(path))
                    return new { available = false, reason = "timeline file not found", path };

                var entries = new List<object>();
                var alertDatas = JObject.Parse(File.ReadAllText(path))["alertDatas"] as JArray;
                foreach (var entry in (alertDatas ?? new JArray()).Take(MaxTimelineEntries))
                {
                    var typeIndex = entry.Value<int?>("alertEntryType") ?? -1;
                    entries.Add(new
                    {
                        created_at = entry.Value<string>("createdAt"),
                        entry_type = typeIndex >= 0 && typeIndex < AlertEntryTypeNames.Length
                            ? AlertEntryTypeNames[typeIndex]
                            : typeIndex.ToString(),
                        error = entry.Value<string>("errorString"),
                        method = entry.Value<string>("methodSimpleName") ?? entry.Value<string>("methodName"),
                        patched_members = entry["patchedMembersDisplayNames"]?.ToObject<string[]>()
                    });
                }

                return new { available = true, source = "persisted", entries };
            }
            catch (Exception ex)
            {
                return new { available = false, reason = ex.Message };
            }
        }

        private static string GetProjectRoot()
        {
            var dataPath = Application.dataPath;
            if (string.IsNullOrEmpty(dataPath))
                return Directory.GetCurrentDirectory();

            return Directory.GetParent(dataPath)?.FullName ?? Directory.GetCurrentDirectory();
        }
    }
}
