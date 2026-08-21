// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    // Stands in for a tool declared by project code: the attributes are public, and this
    // assembly is not one of the package assemblies, so the registry must treat it as custom.
    [ToolProvider("Test")]
    public static class CustomToolExposureProbeProvider
    {
        [Description("Test-only probe verifying that project-declared tools are discovered and exposed.")]
        [ReadOnlyTool]
        public static object CustomToolExposureProbe(
            [ToolParam("Ignored")] string value = null) => value;
    }

    public sealed class MCPToolExporterTests
    {
        private const string ProbeTool = "custom_tool_exposure_probe";

        // A null settings controller falls back to the default core profile.
        private static List<Dictionary<string, object>> ExportCoreTools() =>
            new MCPToolExporter(null).ExportTools();

        private static Dictionary<string, object> Tool(string name) =>
            ExportCoreTools().FirstOrDefault(t => (string)t["name"] == name);

        private static bool HasReadOnlyHint(Dictionary<string, object> tool)
        {
            if (tool == null || !tool.TryGetValue("annotations", out var raw))
                return false;

            return raw is Dictionary<string, object> annotations &&
                   annotations.TryGetValue("readOnlyHint", out var hint) &&
                   hint is bool value && value;
        }

        [Test]
        public void ReadOnlyToolsCarryReadOnlyHint()
        {
            Assert.IsTrue(HasReadOnlyHint(Tool("get_hierarchy")));
            Assert.IsTrue(HasReadOnlyHint(Tool("get_console_logs")));
            Assert.IsTrue(HasReadOnlyHint(Tool("reflect_api")));
        }

        [Test]
        public void MutatingToolsHaveNoReadOnlyHint()
        {
            Assert.IsFalse(HasReadOnlyHint(Tool("execute_code")));
            Assert.IsFalse(HasReadOnlyHint(Tool("set_component_property")));
        }

        // Conformant clients run readOnlyHint tools unattended, so a wrong [ReadOnlyTool] makes a
        // mutating tool auto-approvable with nothing failing. Pin the built-in set instead of
        // spot-checking it. Idea from CoplayDev/unity-mcp Server/tests/test_tool_annotations.py.
        private static readonly string[] ExpectedReadOnlyTools =
        {
            "analyze_scene_complexity",
            "capture_editor_window",
            "capture_game_view",
            "capture_scene_view",
            "capture_simulator_view",
            "exists",
            "fetch_docs",
            "find_assets",
            "find_broken_references",
            "find_game_objects",
            "find_references",
            "focus_on_object",
            "frame_debugger_get_events",
            "get_active_tool",
            "get_addressable_info",
            "get_animator_state",
            "get_assembly_def_info",
            "get_asset_import_settings",
            "get_audio_source_info",
            "get_build_settings",
            "get_camera_properties",
            "get_code_patching_status",
            "get_compilation_errors",
            "get_component_properties",
            "get_console_logs",
            "get_constraint_info",
            "get_counters",
            "get_editor_dialog",
            "get_editor_pref",
            "get_editor_state",
            "get_execute_code_history",
            "get_frame_timing",
            "get_game_object_info",
            "get_hierarchy",
            "get_input_actions_info",
            "get_layers",
            "get_lighting_settings",
            "get_lod_group_info",
            "get_material_properties",
            "get_mesh_info",
            "get_nav_mesh_info",
            "get_object_memory",
            "get_performance_snapshot",
            "get_player_pref",
            "get_prefab_stage",
            "get_prefab_variant_info",
            "get_project_settings",
            "get_reload_recovery_status",
            "get_scene_info",
            "get_scene_view_camera",
            "get_script_reference_url",
            "get_script_sha",
            "get_scriptable_object",
            "get_selection",
            "get_shader_info",
            "get_sorting_layers",
            "get_sprite_atlas_info",
            "get_sprite_sheet_info",
            "get_tags",
            "get_terrain_info",
            "get_test_job",
            "get_top_memory_objects",
            "get_undo_state",
            "get_volume_info",
            "get_windows",
            "list_addressable_groups",
            "list_assembly_defs",
            "list_components",
            "list_directory",
            "list_packages",
            "list_scenes",
            "list_shaders",
            "list_sprite_atlases",
            "log_message",
            "memory_compare_snapshots",
            "memory_list_full_snapshots",
            "memory_list_snapshots",
            "memory_open_snapshot_in_profiler",
            "memory_query_references",
            "memory_query_top_objects",
            "physics2d_overlap_point",
            "physics_overlap",
            "physics_raycast",
            "ping_asset",
            "profiler_status",
            "raycast_at_point",
            "read_file",
            "read_shader",
            "reflect_api",
            "search_files",
            "search_manual",
            "select_object",
            "show_dialog",
            "simulate_key_combo",
            "simulate_key_press",
            "simulate_mouse_click",
            "simulate_mouse_drag",
            "validate_script",
            "wait_for_compilation"
        };

        [Test]
        public void ReadOnlyToolSetIsExactlyThePinnedSet()
        {
            var actual = ToolRegistry.MethodCache
                .Where(kv => ToolRegistry.IsReadOnly(kv.Value) && !ToolRegistry.IsCustomTool(kv.Key))
                .Select(kv => kv.Key)
                .ToArray();

            // A pinned name that resolves to no method at all is a tool compiled out with its
            // module/package (physics, terrain, Input System), not a dropped annotation.
            var expected = ExpectedReadOnlyTools.Where(n => ToolRegistry.GetMethod(n) != null).ToArray();

            CollectionAssert.AreEquivalent(expected, actual,
                "Read-only set drifted; conformant clients run readOnlyHint tools without asking, " +
                "so update ExpectedReadOnlyTools deliberately.");
        }

        [Test]
        public void CoreProfileExposesReflectApi()
        {
            Assert.IsNotNull(Tool("reflect_api"));
        }

        [Test]
        public void ProjectDeclaredToolIsDiscovered()
        {
            Assert.IsNotNull(ToolRegistry.GetMethod(ProbeTool));
        }

        [Test]
        public void ProjectDeclaredToolIsMarkedCustom()
        {
            Assert.IsTrue(ToolRegistry.IsCustomTool(ProbeTool));
            Assert.IsFalse(ToolRegistry.IsCustomTool("get_hierarchy"), "Built-in tools are not custom.");
        }

        [Test]
        public void PackageAssemblyDetectionSeparatesBuiltInFromProject()
        {
            Assert.IsTrue(ToolRegistry.IsPackageAssembly(typeof(ToolRegistry).Assembly));
            Assert.IsFalse(ToolRegistry.IsPackageAssembly(typeof(MCPToolExporterTests).Assembly));
        }

        // Without this, a project tool would be invisible under the default core profile,
        // which is the profile most clients connect with.
        [Test]
        public void CustomToolIsExposedUnderNonFullProfiles()
        {
            foreach (var profile in new[]
            {
                MCPToolExportProfile.Minimal,
                MCPToolExportProfile.Core,
                MCPToolExportProfile.Extended
            })
            {
                Assert.IsTrue(
                    MCPToolExportPolicy.IsToolAllowed(ProbeTool, profile, profileConfigured: false, profileTools: null),
                    $"Custom tool should be exposed under the {profile} profile.");
            }
        }

        [Test]
        public void ExplicitProfileConfigurationStillWinsOverCustomTool()
        {
            Assert.IsFalse(MCPToolExportPolicy.IsToolAllowed(
                ProbeTool,
                MCPToolExportProfile.Core,
                profileConfigured: true,
                profileTools: new[] { "execute_code" }));
        }

        // The curated sets are hand-written strings; a tool method rename would otherwise
        // silently shrink the default profiles with nothing failing.
        [Test]
        public void CuratedProfileToolsAllResolveInRegistry()
        {
            foreach (var name in MCPToolExportPolicy.DefaultMinimalTools.Concat(MCPToolExportPolicy.DefaultCoreTools))
            {
#if !ENABLE_INPUT_SYSTEM
                // These three live in KitWright.Editor.InputSystem, which is excluded from the
                // build without the Input System package.
                if (name == "simulate_key_press" || name == "simulate_key_combo" || name == "simulate_mouse_drag")
                    continue;
#endif
                Assert.IsNotNull(ToolRegistry.GetMethod(name), $"Curated profile tool '{name}' is not registered.");
            }
        }

        // The default profile has to be able to build and persist, not only inspect: with only
        // find_game_objects out of the fourteen GameObject tools and no save_scene, an agent on the
        // default could read a scene and set properties on what already existed, but had to route
        // every creation and the save itself through execute_code.
        [Test]
        public void CoreProfileCanCreateParentAndPersist()
        {
            foreach (var name in new[]
                     {
                         "create_game_object",
                         "create_primitive",
                         "delete_game_object",
                         "add_component",
                         "set_transform",
                         "set_parent",
                         "get_game_object_info",
                         "save_scene"
                     })
            {
                Assert.IsTrue(MCPToolExportPolicy.IsToolAllowed(
                    name, MCPToolExportProfile.Core, profileConfigured: false, profileTools: null),
                    $"'{name}' left the core profile, so the default can no longer build a scene without execute_code.");
            }
        }

        [Test]
        public void BuiltInToolOutsideCoreStaysHiddenUnderCore()
        {
            Assert.IsFalse(MCPToolExportPolicy.IsToolAllowed(
                "create_terrain",
                MCPToolExportProfile.Core,
                profileConfigured: false,
                profileTools: null));
        }
    }
}
