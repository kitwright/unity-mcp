// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor.TestTools.TestRunner.Api;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Testing")]
    internal static class TestListFunctions
    {
        private const int MaxLimit = 5000;
        private const int RetrieveTimeoutSeconds = 30;

        [Description("List the available tests (EditMode and/or PlayMode) without running them: full name, assembly and " +
                     "categories. Feed the names to run_tests. Retrieving the list may wait for a compile to finish.")]
        [ReadOnlyTool]
        public static async Task<object> ListTests(
            [ToolParam("'all', 'EditMode' or 'PlayMode'. Default all.", Required = false)] string mode = "all",
            [ToolParam("Keep only tests whose full name contains this text (case-insensitive)", Required = false)] string name_filter = null,
            [ToolParam("Most tests to list, 1 to 5000. Default 500.", Required = false)] int limit = 500)
        {
            var wanted = new List<TestMode>();
            switch ((mode ?? "all").Trim().ToLowerInvariant())
            {
                case "all": wanted.Add(TestMode.EditMode); wanted.Add(TestMode.PlayMode); break;
                case "editmode": wanted.Add(TestMode.EditMode); break;
                case "playmode": wanted.Add(TestMode.PlayMode); break;
                default:
                    return Response.Error("INVALID_MODE", new { mode, accepted = new[] { "all", "EditMode", "PlayMode" } });
            }

            if (limit < 1 || limit > MaxLimit)
                return Response.Error("INVALID_PARAM", new { param = "limit", provided = limit },
                    $"limit must be from 1 to {MaxLimit}.");

            var tests = new List<object>();
            var total = 0;
            foreach (var testMode in wanted)
            {
                var root = await Retrieve(testMode);
                if (root == null)
                    return Response.Error("TEST_LIST_TIMEOUT", new { mode = testMode.ToString() },
                        $"Unity did not return the {testMode} test list within {RetrieveTimeoutSeconds}s; it may still be compiling.");

                foreach (var assembly in root.Children)
                    foreach (var test in Leaves(assembly))
                    {
                        if (!string.IsNullOrEmpty(name_filter) &&
                            test.FullName.IndexOf(name_filter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        total++;
                        if (tests.Count < limit)
                            tests.Add(new
                            {
                                fullName = test.FullName,
                                mode = testMode.ToString(),
                                assembly = assembly.Name,
                                categories = test.Categories
                            });
                    }
            }

            return Response.Success($"{total} test(s) match; {tests.Count} listed.",
                new { total, truncated = total > tests.Count, tests });
        }

        private static async Task<ITestAdaptor> Retrieve(TestMode mode)
        {
            var api = ScriptableObject.CreateInstance<TestRunnerApi>();
            var done = new TaskCompletionSource<ITestAdaptor>();
            try
            {
                api.RetrieveTestList(mode, root => done.TrySetResult(root));
                var first = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(RetrieveTimeoutSeconds)));
                return first == done.Task ? done.Task.Result : null;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(api);
            }
        }

        private static IEnumerable<ITestAdaptor> Leaves(ITestAdaptor node)
        {
            if (!node.IsSuite)
            {
                yield return node;
                yield break;
            }

            foreach (var child in node.Children)
                foreach (var leaf in Leaves(child))
                    yield return leaf;
        }
    }
}
