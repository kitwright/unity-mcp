// Copyright (C) KitWright. Licensed under MIT.
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// A background Editor throttles its loop, which stalls tool calls, EditorApplication.delayCall and anything waiting on a
    /// frame. Forcing EditorApplication.SignalTick on a timer keeps it ticking; the method is internal, hence the reflection.
    /// </summary>
    [ToolProvider("Editor")]
    [InitializeOnLoad]
    internal static class EditorTickFunctions
    {
        private const string PersistKey = "KitWright.Autotick.IntervalMs";
        private const int MaxIntervalMs = 5000;

        private static readonly MethodInfo SignalTick =
            typeof(EditorApplication).GetMethod("SignalTick", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private static bool enabled;
        private static int intervalMs = 16;
        private static double lastTickSeconds;

        static EditorTickFunctions()
        {
            if (int.TryParse(SessionState.GetString(PersistKey, string.Empty), out var saved))
                Apply(true, saved);
        }

        [Description("Keep the Editor ticking while it is unfocused or in the background, by forcing EditorApplication.SignalTick " +
                     "at a throttled rate. A background Editor otherwise answers tool calls slowly or not at all. interval_ms is the " +
                     "shortest gap between forced ticks (0 = every update, which pegs a CPU core). persist keeps the choice across " +
                     "domain reloads for this editor session; with persist=false it lasts until the next reload.")]
        public static object SetAutotick(
            [ToolParam("Turn auto-tick on (default) or off", Required = false)] bool enable = true,
            [ToolParam("Shortest milliseconds between forced ticks, 0 to 5000. Default 16 (about 60 Hz).", Required = false)] int interval_ms = 16,
            [ToolParam("Keep the choice across domain reloads. Default true.", Required = false)] bool persist = true)
        {
            if (SignalTick == null)
                return Response.Error("FEATURE_UNAVAILABLE", hint: "This Unity version has no EditorApplication.SignalTick to force.");

            if (interval_ms < 0 || interval_ms > MaxIntervalMs)
                return Response.Error("INVALID_PARAM", new { param = "interval_ms", provided = interval_ms }, $"interval_ms must be from 0 to {MaxIntervalMs}.");

            Apply(enable, interval_ms);

            if (persist)
            {
                if (enable) SessionState.SetString(PersistKey, interval_ms.ToString());
                else SessionState.EraseString(PersistKey);
            }

            return Response.Success(enable ? $"Auto-tick is on (every {interval_ms} ms)." : "Auto-tick is off.",
                new { enabled, intervalMs, persisted = persist });
        }

        private static void Apply(bool enable, int interval)
        {
            EditorApplication.update -= Tick;
            enabled = enable && SignalTick != null;
            intervalMs = interval;
            if (enabled)
                EditorApplication.update += Tick;
        }

        private static void Tick()
        {
            var now = EditorApplication.timeSinceStartup;
            if ((now - lastTickSeconds) * 1000.0 < intervalMs)
                return;

            lastTickSeconds = now;
            SignalTick.Invoke(null, null);
        }
    }
}
