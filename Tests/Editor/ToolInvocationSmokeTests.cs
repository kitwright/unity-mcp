// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// Calls tools for real, through the same FunctionInvoker a client request goes through, and in
    /// bulk rather than one named tool at a time. Three sweeps, split by what is safe to run unattended:
    /// every tool is called with no arguments at all - which for a tool that requires one is answered
    /// before its body runs, so it mutates nothing - every tool whose schema demands an argument has to
    /// refuse that call rather than substitute a default, and every read-only tool that needs no
    /// arguments is actually executed. A tool that throws internally shows up here as FUNCTION_FAILED
    /// with its name, which is the only way an untested tool's first exception gets found by us.
    /// </summary>
    public sealed class ToolInvocationSmokeTests
    {
        // Codes the invoker produces when it caught something it did not expect. Every other failure
        // code - PLAY_MODE_REQUIRED, NOT_FOUND, NOT_INSTALLED - is a tool answering properly.
        private static readonly HashSet<string> CrashCodes = new HashSet<string>(StringComparer.Ordinal)
        {
            "FUNCTION_FAILED",
            "FUNCTION_INVOKE_ERROR",
            "MANUAL_TOOL_FAILED",
            "UNKNOWN_FUNCTION"
        };

        // Read-only and argument-free, but not runnable unattended. Anything not listed here IS run.
        private static readonly Dictionary<string, string> NotRunUnattended = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["show_dialog"] = "opens a modal dialog, which owns the editor loop until someone clicks it",
            ["wait_for_compilation"] = "blocks for its whole budget by design",
            ["fetch_docs"] = "reaches the network",
            ["search_manual"] = "reaches the network",
            ["memory_open_snapshot_in_profiler"] = "opens the Profiler window and loads a snapshot into it",
            ["capture_simulator_view"] = "opens the Device Simulator window and switches the Game View to it",
            ["frame_debugger_get_events"] = "enables the frame debugger, which pins rendering until it is turned off"
        };

        [SetUp]
        public void IgnoreToolLogging()
        {
            // A tool that answers an error also logs one, and the test framework fails a test on any
            // unexpected LogError. The assertions below read the returned envelope instead.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void StopIgnoringToolLogging()
        {
            LogAssert.ignoreFailingMessages = false;
        }

        // True when the invoker refuses the call before the tool body runs: no compile-time default,
        // and not marked optional. That refusal is what makes calling every tool here safe.
        private static bool RefusesAnEmptyCall(MethodInfo method) =>
            method.GetParameters().Any(parameter =>
                !parameter.HasDefaultValue &&
                (parameter.GetCustomAttribute<ToolParamAttribute>()?.Required ?? true));

        private static IEnumerable<KeyValuePair<string, MethodInfo>> Tools() =>
            ToolRegistry.MethodCache.OrderBy(entry => entry.Key, StringComparer.Ordinal);

        [UnityTest]
        public IEnumerator EveryToolMissingAMandatoryArgumentNamesIt()
        {
            var violations = new List<string>();
            var refused = 0;

            foreach (var entry in Tools())
            {
                if (!RefusesAnEmptyCall(entry.Value))
                    continue;

                refused++;
                string answer = null;
                yield return Call(entry.Key, a => answer = a, violations);
                if (answer == null)
                    continue;

                var envelope = Envelope(entry.Key, answer, violations);
                if (envelope == null)
                    continue;

                var code = (string)envelope["code"];
                if ((bool?)envelope["success"] == true)
                    violations.Add($"{entry.Key} reported success on a call with none of its required arguments");
                else if (code != "MISSING_PARAM")
                    violations.Add($"{entry.Key} answered {code} rather than MISSING_PARAM: {Trim(answer)}");
            }

            Assert.Greater(refused, 50, "Almost no tool required an argument, so this swept nothing.");
            AssertNone(violations,
                "tool(s) that did not name their missing argument. An agent gets no way to fix the call, " +
                "and a tool that ran anyway did so on its defaults");
        }

        /// <summary>
        /// The schema is what the client obeys, so an argument listed under `required` there has to be
        /// one the tool genuinely will not run without. Some are enforced by the invoker and answer
        /// MISSING_PARAM; some carry a compile-time default and are guarded in the body instead, which
        /// is equally fine. What is not fine is a schema that demands an argument the tool quietly
        /// substitutes a default for: the client is made to always send it, and the one time it does
        /// not, it gets a plausible answer computed from something it never asked for.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryToolThatDeclaresARequiredArgumentRefusesACallWithoutIt()
        {
            var violations = new List<string>();
            var examined = 0;

            foreach (var entry in Tools())
            {
                var required = ToolSchemaBuilder.BuildFromMethod(entry.Key, entry.Value).parameters.required;
                if (required.Count == 0)
                    continue;

                // The invoker refuses these before the body runs, and the test above already pins the
                // code it answers with.
                if (RefusesAnEmptyCall(entry.Value))
                    continue;
                if (NotRunUnattended.ContainsKey(entry.Key))
                    continue;

                examined++;
                string answer = null;
                yield return Call(entry.Key, a => answer = a, violations);
                if (answer == null)
                    continue;

                var envelope = Envelope(entry.Key, answer, violations);
                if (envelope != null && (bool?)envelope["success"] == true)
                {
                    violations.Add($"{entry.Key} declares [{string.Join(", ", required)}] as required and " +
                                   $"still reported success with none of them: {Trim(answer)}");
                }
            }

            Debug.Log($"[KitWright] Checked {examined} tool(s) whose required argument the invoker does not enforce.");
            AssertNone(violations,
                "tool(s) whose schema demands an argument they do not actually need. Mark the parameter " +
                "Required = false so the schema stops lying, or guard it in the body");
        }

        [UnityTest]
        public IEnumerator EveryReadOnlyToolThatNeedsNoArgumentsRunsWithoutCrashing()
        {
            var violations = new List<string>();
            var invoked = new List<string>();

            foreach (var entry in Tools())
            {
                if (!ToolRegistry.IsReadOnly(entry.Value) || RefusesAnEmptyCall(entry.Value))
                    continue;
                if (NotRunUnattended.ContainsKey(entry.Key))
                    continue;

                string answer = null;
                yield return Call(entry.Key, a => answer = a, violations);
                if (answer == null)
                    continue;

                invoked.Add(entry.Key);

                // A screenshot answers with a data URI, which MCPRequestHandler renders as an image
                // and which is deliberately not an envelope.
                if (answer.StartsWith("data:", StringComparison.Ordinal))
                    continue;

                var envelope = Envelope(entry.Key, answer, violations);
                if (envelope == null)
                    continue;

                var code = (string)envelope["code"];
                if ((bool?)envelope["success"] != true && CrashCodes.Contains(code))
                    violations.Add($"{entry.Key} threw out of its own body ({code}): {Trim(answer)}");
            }

            Debug.Log($"[KitWright] Ran {invoked.Count} read-only tool(s) with no arguments: " +
                      string.Join(", ", invoked));

            Assert.Greater(invoked.Count, 20, "Almost nothing was actually executed, so this proved little.");
            AssertNone(violations, "read-only tool(s) that failed on a plain call");
        }

        // A rename would otherwise turn a skip into a tool nobody runs and nobody notices.
        [Test]
        public void EveryToolSkippedByThisSweepStillExists()
        {
            var missing = NotRunUnattended.Keys
                .Where(name => ToolRegistry.GetMethod(name) == null)
                .ToList();

            AssertNone(missing,
                "name(s) skipped as unrunnable that are not tools any more. Drop the entry so the sweep " +
                "does not read as covering more than it does");
        }

        private static IEnumerator Call(string tool, Action<string> onAnswer, List<string> violations)
        {
            var invoker = new FunctionInvoker();
            var call = new FunctionCall { FunctionName = tool, Parameters = new Dictionary<string, string>() };

            var task = invoker.InvokeAsync(call);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            while (!task.IsCompleted)
            {
                if (clock.Elapsed.TotalSeconds > 60)
                {
                    violations.Add($"{tool} had not answered after 60s");
                    yield break;
                }

                yield return null;
            }

            if (task.Exception != null)
            {
                // Reaching here means the invoker let an exception escape instead of turning it into
                // an error envelope, which is a transport-level failure rather than a tool failure.
                violations.Add($"{tool} threw out of InvokeAsync: {task.Exception.GetBaseException().Message}");
                yield break;
            }

            onAnswer(task.Result);
        }

        private static JObject Envelope(string tool, string answer, List<string> violations)
        {
            try
            {
                var parsed = JObject.Parse(answer);
                if (parsed["success"] == null || parsed["success"].Type != JTokenType.Boolean)
                {
                    violations.Add($"{tool} answered JSON with no boolean `success`: {Trim(answer)}");
                    return null;
                }

                return parsed;
            }
            catch (Exception ex)
            {
                violations.Add($"{tool} answered something that is not a JSON envelope ({ex.Message}): {Trim(answer)}");
                return null;
            }
        }

        private static string Trim(string answer) =>
            answer == null ? "(null)" : answer.Length <= 300 ? answer : answer.Substring(0, 300) + "...";

        private static void AssertNone(List<string> violations, string what)
        {
            if (violations.Count == 0)
                return;

            violations.Sort(StringComparer.Ordinal);
            Assert.Fail($"{violations.Count} {what}:{Environment.NewLine}  " +
                        string.Join(Environment.NewLine + "  ", violations));
        }
    }
}
