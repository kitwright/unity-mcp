// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using KitWright.Editor.Tools;

namespace KitWright.Editor.MCP.Server
{
    internal enum MCPToolExportProfile
    {
        Minimal,
        Core,
        Extended,
        Full
    }

    internal static class MCPToolExportPolicy
    {
        private static readonly HashSet<string> MinimalTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "execute_code",
            "execute_menu_item",
            "get_editor_state",
            "get_scene_info",
            "get_hierarchy",
            "get_console_logs",
            "get_compilation_errors",
            "find_game_objects",
            "set_component_property",
            "capture_game_view",
            "set_tool_profile"
        };

        private static readonly HashSet<string> CoreTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "execute_code",
            "simulate_key_press",
            "simulate_key_combo",
            "simulate_mouse_click",
            "simulate_mouse_drag",
            "get_scene_info",
            "get_hierarchy",
            "get_console_logs",
            "get_performance_snapshot",
            "analyze_scene_complexity",
            "capture_game_view",
            "capture_scene_view",
            "capture_editor_window",
            "raycast_at_point",
            "wait_for_compilation",
            "request_recompile",
            "get_compilation_errors",
            "get_reload_recovery_status",
            "enter_play_mode",
            "exit_play_mode",
            "save_scene",
            "get_editor_state",
            "get_selection",
            "set_selection",
            "get_prefab_stage",
            "find_game_objects",
            "list_components",
            "get_game_object_info",
            "create_game_object",
            "create_primitive",
            "delete_game_object",
            "add_component",
            "set_transform",
            "set_parent",
            "get_component_properties",
            "set_component_property",
            "set_component_properties",
            "reflect_api",
            "fetch_docs",
            "set_tool_profile",
            "execute_menu_item"
        };

        // Extended = every registered tool EXCEPT these niche families. Substring match on tool name
        // (tool names are verb-first: create_terrain, get_addressable_info, memory_take_snapshot).
        private static readonly string[] ExtendedExcludedSubstrings =
        {
            "terrain",
            "addressable",
            "assembly",
            "memory_"
        };

        private static readonly string[] ProfileOrder = { "minimal", "core", "extended", "full" };

        public static IReadOnlyList<string> AllProfiles => ProfileOrder;

        public static IReadOnlyCollection<string> DefaultCoreTools => CoreTools;

        public static IReadOnlyCollection<string> DefaultMinimalTools => MinimalTools;

        public static MCPToolExportProfile Parse(string value)
        {
            switch (value?.Trim().ToLowerInvariant())
            {
                case "minimal": return MCPToolExportProfile.Minimal;
                case "extended": return MCPToolExportProfile.Extended;
                case "full": return MCPToolExportProfile.Full;
                default: return MCPToolExportProfile.Core;
            }
        }

        public static string ToSettingValue(MCPToolExportProfile profile)
        {
            switch (profile)
            {
                case MCPToolExportProfile.Minimal: return "minimal";
                case MCPToolExportProfile.Extended: return "extended";
                case MCPToolExportProfile.Full: return "full";
                default: return "core";
            }
        }

        public static bool IsToolAllowed(
            string toolName,
            MCPToolExportProfile profile,
            bool profileConfigured,
            IEnumerable<string> profileTools)
        {
            if (string.IsNullOrWhiteSpace(toolName))
                return false;

            if (profileConfigured)
                return ContainsTool(profileTools, toolName);

            return IsInDefaultSet(toolName, profile) || ToolRegistry.IsCustomTool(toolName);
        }

        private static bool IsInDefaultSet(string toolName, MCPToolExportProfile profile)
        {
            switch (profile)
            {
                case MCPToolExportProfile.Minimal:
                    return MinimalTools.Contains(toolName);
                case MCPToolExportProfile.Core:
                    return CoreTools.Contains(toolName);
                case MCPToolExportProfile.Extended:
                    return !IsExtendedExcluded(toolName);
                default:
                    return true;
            }
        }

        private static bool IsExtendedExcluded(string toolName)
        {
            foreach (var sub in ExtendedExcludedSubstrings)
            {
                if (toolName.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        public static IEnumerable<string> DefaultToolsFor(
            MCPToolExportProfile profile,
            IEnumerable<string> allToolNames)
        {
            return (allToolNames ?? Enumerable.Empty<string>())
                .Where(name => !string.IsNullOrWhiteSpace(name)
                               && (IsInDefaultSet(name, profile) || ToolRegistry.IsCustomTool(name)));
        }

        private static bool ContainsTool(IEnumerable<string> tools, string toolName)
        {
            if (tools == null)
                return false;

            foreach (var tool in tools)
            {
                if (string.Equals(tool, toolName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public static int GetSortRank(string toolName, MCPToolExportProfile profile)
        {
            if (string.Equals(toolName, "execute_code", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (profile != MCPToolExportProfile.Full && IsInDefaultSet(toolName, profile))
                return 100;

            return 1000;
        }

        public static string BuildDescriptionPrefix(string toolName, MCPToolExportProfile profile)
        {
            var profilePrefix = profile == MCPToolExportProfile.Full
                ? string.Empty
                : $"[{ToSettingValue(profile)}] ";

            if (string.Equals(toolName, "execute_code", StringComparison.OrdinalIgnoreCase))
                return "[primary] " + profilePrefix;

            return profilePrefix;
        }
    }
}
