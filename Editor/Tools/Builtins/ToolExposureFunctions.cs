// Copyright (C) KitWright. All rights reserved.

using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.DI;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Settings;
using KitWright.Editor.Tools.Helpers;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("ToolExposure")]
    internal static class ToolExposureFunctions
    {
        [Description("Switch the MCP tool profile exposed to clients: 'minimal', 'core', 'extended' or 'full'. " +
                     "Use this to reach a tool the current profile hides (terrain, profiler, addressables, build, …) " +
                     "instead of asking the user to change it in the Unity window. Connected clients are told to refresh " +
                     "through notifications/tools/list_changed, so neither the editor nor the MCP server restarts. " +
                     "The choice persists in UserSettings for later sessions.")]
        public static object SetToolProfile(
            [ToolParam("Profile to expose: minimal, core, extended or full")] string profile)
        {
            if (string.IsNullOrWhiteSpace(profile))
                return Response.Error("EMPTY_PROFILE", new { valid = MCPToolExportPolicy.AllProfiles });

            var requested = profile.Trim().ToLowerInvariant();
            if (!MCPToolExportPolicy.AllProfiles.Contains(requested, StringComparer.OrdinalIgnoreCase))
                return Response.Error("UNKNOWN_PROFILE", new { requested = profile, valid = MCPToolExportPolicy.AllProfiles });

            var settings = RootScopeServices.Services?.GetService(typeof(SettingsController)) as SettingsController;
            if (settings == null)
                return Response.Error("SETTINGS_UNAVAILABLE", new { hint = "The MCP server is not running with a settings scope." });

            var previous = MCPToolExportPolicy.ToSettingValue(MCPToolExportPolicy.Parse(settings.MCPToolExportProfile));
            settings.MCPToolExportProfile = requested;

            var exporter = new MCPToolExporter(settings);
            var toolCount = exporter.ExportTools().Count;
            MCPToolListChangeNotifier.CheckForChanges(exporter);

            return Response.Success(
                $"Tool profile '{requested}' now exposes {toolCount} tools (was '{previous}').",
                new
                {
                    previous,
                    current = requested,
                    tool_count = toolCount,
                    changed = !string.Equals(previous, requested, StringComparison.OrdinalIgnoreCase),
                    hint = "Re-read tools/list to pick up the new set."
                });
        }
    }
}
