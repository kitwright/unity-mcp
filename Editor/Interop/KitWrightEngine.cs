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
        private const string EngineAssembly = "KitWright.HotReload.Editor";
        private const string EngineType = "KitWright.HotReload.Editor.HotReloadEngine";
        private const string ActivityName = "Hot Reload";

        private static readonly ConcurrentQueue<(DateTime Time, string Verdict, string Text)> Pending =
            new ConcurrentQueue<(DateTime, string, string)>();

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
            catch (Exception)
            {
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
                catch (Exception)
                {
                    return false;
                }
            }
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
                var json = FindEngine()?.GetMethod("OutcomesSince", BindingFlags.Public | BindingFlags.Static)
                    ?.Invoke(null, new object[] { sinceUtc.ToString("O") }) as string;
                return json == null ? null : JObject.Parse(json)["items"] as JArray;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>False while the patch server builds baselines or compiles a batch of edits.</summary>
        internal static bool ServerIdle()
        {
            try
            {
                var json = FindEngine()?.GetMethod("ServerState", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null) as string;
                return json == null || (string)JObject.Parse(json)["state"] == "ready";
            }
            catch (Exception)
            {
                return true;
            }
        }

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

        private static Type FindEngine() =>
            AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == EngineAssembly)?.GetType(EngineType, false);

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
