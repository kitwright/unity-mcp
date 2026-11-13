// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditorInternal;
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

        // Values of SingularityGroup.HotReload.Editor.EntryType.
        private static readonly string[] EntryTypeNames = { "Parent", "Child", "Standalone", "Foldout" };

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

        /// <summary>The plugin is loaded and its server is up, so it patches edited methods, Play Mode included.</summary>
        public static bool IsPatching => IsLoaded && TryGetServerHealthy() == true;

        public const string Applied = "applied";
        public const string NoChange = "no_change";
        public const string CompileError = "compile_error";

        /// <summary>
        /// The verdict on the script edits made since <paramref name="sinceUtc"/>, or null while the plugin has
        /// not answered for them yet (see <see cref="ClassifyOutcome"/>).
        /// </summary>
        public static string OutcomeSince(DateTime sinceUtc) => ClassifyOutcome(ReadEntries(), sinceUtc);

        /// <summary>
        /// The error text of the entries created since <paramref name="sinceUtc"/>: the "error CS" lines only, or
        /// every reason a patch failed (which can name another file changed at the same time).
        /// </summary>
        public static string[] ErrorsSince(DateTime sinceUtc, bool compileErrorsOnly) =>
            (ReadEntries() ?? new List<TimelineEntry>())
                .Where(e => e.CreatedAtUtc >= sinceUtc && !string.IsNullOrEmpty(e.Error) && (!compileErrorsOnly || IsCompileError(e)))
                .Select(e => e.Error)
                .Distinct()
                .ToArray();

        /// <summary>EditorCodePatcher.patchStatus is Compiling or Patching; null when it cannot be read.</summary>
        public static bool? TryGetBusy()
        {
            try
            {
                var status = FindEditorType("SingularityGroup.HotReload.Editor.EditorCodePatcher")
                    ?.GetField("patchStatus", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null)?.ToString();
                return status == null ? (bool?)null : status == "Compiling" || status == "Patching";
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Why the plugin is holding patches back right now (its compile detour stays on, so Unity does not
        /// compile either), or null. Mirrors the early returns of EditorCodePatcher.OnIntervalMainThread.
        /// </summary>
        public static string PausedReason()
        {
            if (System.Diagnostics.Debugger.IsAttached && EditorPrefs.GetBool("HotReloadWindow.DebuggerCompatibilityEnabled", true))
                return "a debugger is attached (its \"auto-disable with debugger\" setting)";
            if (EditorApplication.isPlaying)
                return null;
            if (EditorPrefs.GetBool("HotReloadWindow.PauseHotReloadInEditMode", false))
                return "it is paused in Edit Mode (its \"Pause Hot Reload in Edit Mode\" setting)";
            if (!InternalEditorUtility.isApplicationActive && TryGetPatchOnlyOnFocus() == true)
                return "it patches Edit Mode edits only while the Editor has focus (patchEditModeOnlyOnEditorFocus in hot-reload-config.json)";
            return null;
        }

        private static bool? TryGetPatchOnlyOnFocus()
        {
            try
            {
                var config = FindEditorType("SingularityGroup.HotReload.Editor.EditorCodePatcher")
                    ?.GetField("config", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                return config?.GetType().GetField("patchEditModeOnlyOnEditorFocus")?.GetValue(config) as bool?;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// The plugin's own Recompile button: drops the compile detour and asks Unity to compile. It also
        /// ends Play Mode, so callers use it only outside Play Mode or when asked to leave it.
        /// </summary>
        public static bool TryRequestFullRecompile()
        {
            try
            {
                var recompile = FindEditorType("SingularityGroup.HotReload.Editor.HotReloadRunTab")
                    ?.GetMethod("Recompile", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (recompile == null)
                    return false;

                recompile.Invoke(null, null);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal sealed class TimelineEntry
        {
            public DateTime? CreatedAtUtc;
            public string Type;
            // EntryType: a patch's outcome is a Parent or Standalone entry; its Child entries are the details.
            public string Role;
            public string Title;
            public string Error;
            public string Method;
            public string[] PatchedMembers;
        }

        /// <summary>
        /// The newest outcome among entries created at or after <paramref name="sinceUtc"/>:
        /// <c>compile_error</c> (an Error entry quoting "error CS"; a compile error gets no outcome entry),
        /// <c>applied</c> (PatchApplied), <c>no_change</c> (UndetectedChange: the edit changed no code, such as a
        /// comment, or put back what was already patched), or, where the running code does not match the file,
        /// <c>partial</c> (PartiallySupportedChange), <c>not_applied</c> (Failure: an unsupported change or a
        /// failed patch), <c>inlined</c> (InlinedMethod).
        /// Null while no entry that new decides it.
        /// </summary>
        internal static string ClassifyOutcome(IEnumerable<TimelineEntry> newestFirst, DateTime sinceUtc)
        {
            var recent = (newestFirst ?? Enumerable.Empty<TimelineEntry>())
                .Where(e => e.CreatedAtUtc >= sinceUtc)
                .ToList();

            if (recent.Any(IsCompileError))
                return CompileError;

            foreach (var entry in recent)
            {
                if (entry.Role == "Child")
                    continue;

                switch (entry.Type)
                {
                    case "PatchApplied": return Applied;
                    case "PartiallySupportedChange": return "partial";
                    case "Failure": return "not_applied";
                    case "UndetectedChange": return NoChange;
                    case "InlinedMethod": return "inlined";
                }
            }

            return null;
        }

        private static bool IsCompileError(TimelineEntry entry) =>
            entry.Type == "Error" && entry.Error != null && entry.Error.Contains("error CS");

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
            var source = "live";
            var entries = ReadLiveEntries();
            string reason = null;
            if (entries == null)
            {
                source = "persisted";
                entries = ReadPersistedEntries(out reason);
            }

            if (entries == null)
                return new { available = false, reason };

            return new
            {
                available = true,
                source,
                entries = entries.Take(MaxTimelineEntries).Select(e => new
                {
                    created_at = e.CreatedAtUtc?.ToString("O"),
                    entry_type = e.Type,
                    role = e.Role,
                    title = e.Title,
                    error = e.Error,
                    method = e.Method,
                    patched_members = e.PatchedMembers
                }).ToList()
            };
        }

        private static List<TimelineEntry> ReadEntries() => ReadLiveEntries() ?? ReadPersistedEntries(out _);

        // Newest first, as the plugin keeps them; null when its list cannot be read.
        private static List<TimelineEntry> ReadLiveEntries()
        {
            try
            {
                var helper = FindEditorType("SingularityGroup.HotReload.Editor.HotReloadTimelineHelper");
                var timeline = helper?.GetProperty("EventsTimeline", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) as System.Collections.IEnumerable;
                if (timeline == null)
                    return null;

                const BindingFlags instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                var entries = new List<TimelineEntry>();
                foreach (var entry in timeline)
                {
                    var entryType = entry.GetType();
                    var alertData = entryType.GetField("alertData", instance)?.GetValue(entry);
                    object Data(string name) => alertData?.GetType().GetField(name, instance)?.GetValue(alertData);
                    var createdAt = Data("createdAt") as DateTime? ?? entryType.GetField("timestamp", instance)?.GetValue(entry) as DateTime?;

                    entries.Add(new TimelineEntry
                    {
                        CreatedAtUtc = createdAt?.ToUniversalTime(),
                        // alertData.alertEntryType is the outcome; the entry's own alertType only picks its icon.
                        Type = Data("alertEntryType")?.ToString(),
                        Role = Data("entryType")?.ToString(),
                        Title = entryType.GetField("title", instance)?.GetValue(entry) as string,
                        Error = Data("errorString") as string,
                        Method = Data("methodSimpleName") as string ?? Data("methodName") as string,
                        PatchedMembers = Data("patchedMembersDisplayNames") as string[]
                    });
                }

                return entries;
            }
            catch
            {
                return null;
            }
        }

        private static List<TimelineEntry> ReadPersistedEntries(out string reason)
        {
            reason = null;
            try
            {
                var path = Path.Combine(GetProjectRoot(), TimelineRelativePath);
                if (!File.Exists(path))
                {
                    reason = "timeline file not found: " + path;
                    return null;
                }

                return ParsePersistedEntries(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return null;
            }
        }

        // The plugin writes its enums as numbers.
        internal static List<TimelineEntry> ParsePersistedEntries(string json)
        {
            string Name(string[] names, int? index) =>
                index == null ? null : index >= 0 && index < names.Length ? names[index.Value] : index.ToString();

            return ((JObject.Parse(json)["alertDatas"] as JArray) ?? new JArray())
                .Select(entry => new TimelineEntry
                {
                    CreatedAtUtc = entry["createdAt"]?.ToObject<DateTime?>()?.ToUniversalTime(),
                    Type = Name(AlertEntryTypeNames, entry.Value<int?>("alertEntryType")),
                    Role = Name(EntryTypeNames, entry.Value<int?>("entryType")),
                    Error = entry.Value<string>("errorString"),
                    Method = entry.Value<string>("methodSimpleName") ?? entry.Value<string>("methodName"),
                    PatchedMembers = entry["patchedMembersDisplayNames"]?.ToObject<string[]>()
                })
                .ToList();
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
