// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KitWright.Editor.DI;
using KitWright.Editor.MCP.Server;
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
