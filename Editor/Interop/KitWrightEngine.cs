// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KitWright.Editor.DI;
using KitWright.Editor.MCP.Server;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Interop
{
    /// <summary>
    /// Reflection-only link to the KitWright patch engine of unity-mcp-pro, so the core needs no reference to it.
    /// While the engine is enabled it patches saved edits itself: an asset refresh would only import the
    /// script and compile it over the patch.
    /// </summary>
    [InitializeOnLoad]
    internal static class KitWrightEngine
    {
        internal const string EngineAssembly = "KitWright.HotReload.Editor";
        internal const string EngineType = "KitWright.HotReload.Editor.HotReloadEngine";
        private const string ActivityName = "Hot Reload";
        private const string DisplayName = "KitWright patch engine";

        private static readonly ConcurrentQueue<(DateTime Time, string Verdict, string Text)> Pending =
            new ConcurrentQueue<(DateTime, string, string)>();

        // The loaded assemblies change only with a domain reload, which resets this too.
        private static readonly Lazy<Type> Engine = new Lazy<Type>(() =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == EngineAssembly)?.GetType(EngineType, false));

        // Test seam: the warning is logged once per domain, however often the engine is polled.
        internal static bool FailureLogged;

        static KitWrightEngine()
        {
            var evt = FindEngine()?.GetEvent("OutcomeRecorded", BindingFlags.Public | BindingFlags.Static);
            if (evt == null)
                return;

            try
            {
                evt.AddEventHandler(null, new Action<string, string, string>(OnOutcome));
                EditorApplication.update += Drain;
            }
            catch (Exception ex)
            {
                LogFailureOnce("OutcomeRecorded", ex);
            }
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    var enabled = FindEngine()?.GetProperty("Enabled", BindingFlags.Public | BindingFlags.Static);
                    return enabled?.GetValue(null) is true;
                }
                catch (Exception ex)
                {
                    LogFailureOnce("Enabled", ex);
                    return false;
                }
            }
        }

        /// <summary>The engine's own status (status, why_off, server, last_verdict...); null when the engine is not installed.</summary>
        internal static JObject GetStatus()
        {
            try
            {
                var json = FindEngine()?.GetMethod("Status", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as string;
                return json == null ? null : DescribeStatus(JObject.Parse(json));
            }
            catch (Exception ex)
            {
                LogFailureOnce("Status", ex);
                return null;
            }
        }

        // The same keys as a SingularityGroup entry in get_code_patching_status; the engine is the one that is off or on.
        internal static JObject DescribeStatus(JObject status)
        {
            status["id"] = "kitwright-engine";
            status["display_name"] = DisplayName;
            status["loaded"] = true;
            status["suppresses_compilation"] = status.Value<string>("status") is string state && state != "off";
            status["plugin"] = "KitWright";
            return status;
        }

        internal enum PatchResult
        {
            Patched,
            CompileError,
            NeedsRecompile,
        }

        // Anything the engine could not put into the running code means the code and the file differ until Unity compiles.
        internal static PatchResult Classify(IEnumerable<string> verdicts)
        {
            var all = verdicts.ToList();
            if (all.Contains("compile_error"))
                return PatchResult.CompileError;
            return all.Any(v => v != "applied" && v != "no_change") ? PatchResult.NeedsRecompile : PatchResult.Patched;
        }

        /// <summary>Every verdict the engine recorded at or after <paramref name="sinceUtc"/>, oldest first; null while none.</summary>
        internal static JArray OutcomesSince(DateTime sinceUtc)
        {
            try
            {
                return ParseOutcomes(FindEngine()?.GetMethod("OutcomesSince", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new object[] { sinceUtc.ToString("O") }) as string);
            }
            catch (Exception ex)
            {
                LogFailureOnce("OutcomesSince", ex);
                return null;
            }
        }

        // The engine writes {"items":[{verdict, details[], created_at, ...}]}.
        internal static JArray ParseOutcomes(string json) => json == null ? null : JObject.Parse(json)["items"] as JArray;

        internal static (List<string> Verdicts, List<string> Details) Summarize(JArray outcomes) => (
            outcomes.Select(o => (string)o["verdict"]).ToList(),
            outcomes.SelectMany(o => (o["details"] as JArray ?? new JArray()).Select(d => (string)d)).Take(20).ToList());

        /// <summary>False while the patch server builds baselines or compiles a batch of edits.</summary>
        internal static bool ServerIdle()
        {
            try
            {
                return IsIdle(FindEngine()?.GetMethod("ServerState", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as string);
            }
            catch (Exception ex)
            {
                LogFailureOnce("ServerState", ex);
                return true;
            }
        }

        // The server rewrites its state file in place, so a read that meets the write sees it empty.
        internal static bool IsIdle(string serverJson) =>
            string.IsNullOrEmpty(serverJson) || (string)JObject.Parse(serverJson)["state"] == "ready";

        internal static MCPToolCallStatus StatusOf(string verdict)
        {
            switch (verdict)
            {
                case "applied":
                case "no_change":
                    return MCPToolCallStatus.Success;
                case "partial":
                case "inlined":
                    return MCPToolCallStatus.Interrupted;
                default:
                    return MCPToolCallStatus.Error;
            }
        }

        private static Type FindEngine() => Engine.Value;

        internal static void LogFailureOnce(string member, Exception ex)
        {
            if (FailureLogged)
                return;

            FailureLogged = true;
            var cause = ex.InnerException ?? ex;
            Debug.LogWarning($"[KitWright MCP] Calling the KitWright patch engine's {member} failed ({cause.GetType().Name}: {cause.Message}). " +
                             "The core and the Pro package may be out of step; this is logged once.");
        }

        // The engine may apply a patch from a background thread while the Editor sleeps, and the log is the main thread's.
        private static void OnOutcome(string verdict, string text, string createdAtUtc)
        {
            var time = DateTime.TryParse(createdAtUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
                ? utc.ToLocalTime()
                : DateTime.Now;
            Pending.Enqueue((time, verdict, text));
        }

        private static void Drain()
        {
            if (Pending.IsEmpty)
                return;

            var log = (RootScopeServices.Services?.GetService(typeof(MCPServerService)) as MCPServerService)?.InteractionLog;
            if (log == null)
                return;

            while (Pending.TryDequeue(out var entry))
                log.Add(ActivityName, StatusOf(entry.Verdict), entry.Text, entry.Time);
        }
    }
}
