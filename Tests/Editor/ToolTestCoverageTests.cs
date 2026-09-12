// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using KitWright.Editor.Tools;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// A ratchet, not a metric. It reads the test sources next to it and asks, for every registered
    /// tool, whether any test names it at all - by calling the method or by naming the tool in a
    /// string. Tools nothing names are listed in <see cref="KnownUntestedTools"/>; the test fails when
    /// a NEW tool joins them, so the surface can only get better covered from here. The baseline is
    /// also checked for staleness, so it shrinks as tests arrive instead of rotting.
    /// </summary>
    public sealed class ToolTestCoverageTests
    {
        // Two kinds of entry. The first block is deliberate: these cannot be run unattended, and
        // the reason is next to each. The second is work not done yet - delete a line when you
        // write the test, because TheUntestedBaselineHasNoStaleEntries fails if you do not.
        private static readonly string[] KnownUntestedTools =
        {
            // a recursive test run
            "cancel_test_run",
            "run_tests",
            // a package resolve, which reloads the domain the run is living in
            "install_package",
            "remove_package",
            // an assembly definition change, which recompiles and reloads it too
            "add_assembly_references",
            "create_assembly_def",
            "remove_assembly_references",
            "set_assembly_platforms",
            "update_assembly_def_settings",
            // arbitrary code or a script edit, same reason
            // batch_execute left this list when CoreProfileCoversWhatTheShippedSkillsCall started
            // naming it; running it in a test is still off the table for the reason above.
            "clear_execute_code_history",
            "edit_script_members",
            "replay_execute_code",
            // Play Mode, driven from inside a test that is already driving it
            "enter_play_mode",
            "exit_play_mode",
            // minutes of work, or a file the size of the heap
            "bake_lightmaps",
            "memory_take_full_snapshot",
            // pins rendering, or drives the real editor GUI
            "frame_debugger_enable",
            "simulate_editor_window_click",
            "simulate_editor_window_key",
            // replaces the scene the run is in
            "close_scene",
            "load_scene_additive",
            "open_scene",
            "set_active_scene",
            // wipes state that belongs to whoever owns the project
            // clear_console is named by CoreProfileCoversWhatTheShippedSkillsCall now, so it is off
            // this list; nothing calls it in a test, and nothing should.
            "delete_all_player_prefs",
            "set_tool_profile"

            // Nothing is pending here: every other registered tool is named by a test.
        };

        private static string ThisFile([CallerFilePath] string path = null) => path;

        private static string TestsRoot() => Path.GetDirectoryName(ThisFile());

        private static string TestSources()
        {
            var root = TestsRoot();
            if (root == null || !Directory.Exists(root))
                Assert.Ignore($"The test sources are not on disk at '{root}', so coverage cannot be read.");

            var self = ThisFile();
            var text = new StringBuilder();

            foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Reading this file would make every baselined name count as covered by its own baseline.
                if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
                    continue;

                text.Append(File.ReadAllText(file)).Append('\n');
            }

            return text.ToString();
        }

        // A tool counts as named when a test calls it (Provider.Method) or asks for it by tool name
        // ("get_hierarchy"). Both are deliberate mentions; prose in a comment is not.
        private static List<string> UncoveredTools(string sources)
        {
            var uncovered = new List<string>();

            foreach (var entry in ToolRegistry.MethodCache.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (ToolRegistry.IsCustomTool(entry.Key))
                    continue;

                var qualified = $"{entry.Value.DeclaringType?.Name}.{entry.Value.Name}";
                if (sources.Contains(qualified) || sources.Contains($"\"{entry.Key}\""))
                    continue;

                uncovered.Add(entry.Key);
            }

            return uncovered;
        }

        [Test]
        public void NoToolArrivesWithoutATestNamingIt()
        {
            var uncovered = UncoveredTools(TestSources());
            var baseline = new HashSet<string>(KnownUntestedTools, StringComparer.Ordinal);
            var unlisted = uncovered.Where(name => !baseline.Contains(name)).ToList();

            if (unlisted.Count == 0)
                return;

            Assert.Fail($"{unlisted.Count} tool(s) that no test names, and that the baseline does not " +
                        $"account for. Write a test, or - if it genuinely cannot be tested here - paste " +
                        $"these into KnownUntestedTools:{Environment.NewLine}" +
                        string.Join(Environment.NewLine, unlisted.Select(name => $"            \"{name}\",")));
        }

        [Test]
        public void TheUntestedBaselineHasNoStaleEntries()
        {
            var sources = TestSources();
            var uncovered = new HashSet<string>(UncoveredTools(sources), StringComparer.Ordinal);
            var stale = new List<string>();

            foreach (var name in KnownUntestedTools)
            {
                if (ToolRegistry.GetMethod(name) == null)
                    stale.Add($"'{name}' is not a registered tool any more");
                else if (!uncovered.Contains(name))
                    stale.Add($"'{name}' is covered now - delete the line");
            }

            if (stale.Count == 0)
                return;

            stale.Sort(StringComparer.Ordinal);
            Assert.Fail($"{stale.Count} stale baseline entry/entries:{Environment.NewLine}  " +
                        string.Join(Environment.NewLine + "  ", stale));
        }

        [Test]
        public void TheCoverageReaderFindsTheTestSources()
        {
            // Guards the reader itself: a path that resolved to an empty folder would report every
            // tool as untested and, worse, would let the baseline swallow the whole surface.
            var sources = TestSources();

            Assert.Greater(sources.Length, 10000, "Read almost no test source, so coverage below is meaningless.");
            StringAssert.Contains("HierarchyFunctions.GetHierarchy", sources,
                "A known call is missing from the sources read, so the coverage signal is broken.");
        }
    }
}
