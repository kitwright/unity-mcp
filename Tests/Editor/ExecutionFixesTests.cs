// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Settings;
using KitWright.Editor.State;
using KitWright.Editor.Threading;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Scripting;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class ExecutionFixesTests
    {
        private static string ErrorCode(object error) =>
            (string)error.GetType().GetProperty("code").GetValue(error);

        private static ScriptExecutionFunctions.HistoryBox FilledHistory(int count)
        {
            var box = new ScriptExecutionFunctions.HistoryBox();
            for (var i = 1; i <= count; i++)
                ScriptExecutionFunctions.AddHistoryEntry(box, "snippet-" + i, true, "ok");
            return box;
        }

        // The id used to be the list position, which slid down by one for every call made once the
        // history was full, so a replay of "49" ran whatever had moved into slot 49 since.
        [Test]
        public void HistoryIdsStayPutWhileTheOldestEntriesAreEvicted()
        {
            var box = FilledHistory(60);
            Assert.Greater(box.entries[0].id, 1, "the cap should have evicted the first entries");

            Assert.IsNull(ScriptExecutionFunctions.ResolveReplayEntry(box, 40, out var before));
            Assert.AreEqual("snippet-40", before.code);

            ScriptExecutionFunctions.AddHistoryEntry(box, "snippet-61", true, "ok");

            Assert.IsNull(ScriptExecutionFunctions.ResolveReplayEntry(box, 40, out var after));
            Assert.AreEqual("snippet-40", after.code, "a newer entry changed what an existing id points at");
        }

        [Test]
        public void ReplayOfAnEvictedIdIsRefusedInsteadOfRunningAnotherEntry()
        {
            var box = FilledHistory(60);

            var refusal = ScriptExecutionFunctions.ResolveReplayEntry(box, 3, out var entry);

            Assert.IsNull(entry);
            Assert.AreEqual("HISTORY_ENTRY_NOT_FOUND", ErrorCode(refusal));
        }

        [Test]
        public void HistoryIdsAreNotReusedAfterTheHistoryIsEmptied()
        {
            var box = FilledHistory(3);
            var last = box.lastId;
            box.entries.Clear();

            var next = ScriptExecutionFunctions.AddHistoryEntry(box, "after", true, "ok");

            Assert.Greater(next.id, last);
        }

        [Test]
        public void AnIdFromBeforeTheHistoryWasClearedIsNotFoundNotEmpty()
        {
            var box = FilledHistory(3);
            var stale = box.entries[1].id;
            box.entries.Clear();

            var refusal = ScriptExecutionFunctions.ResolveReplayEntry(box, stale, out var entry);

            Assert.IsNull(entry);
            Assert.AreEqual("HISTORY_ENTRY_NOT_FOUND", ErrorCode(refusal));
        }

        private static string Describe(ScriptCompilationResult result) =>
            result.Message + " " + string.Join("; ",
                (result.Errors ?? new List<ScriptCompilationError>()).Select(error => error.code + " " + error.text));

        // Only the type is named and nothing runs, so nothing is written to disk.
        [Test]
        public void ASnippetNamingZipFileCompiles()
        {
            const string snippet =
                "public static class ZipNameProbe { public static string Run() { return typeof(System.IO.Compression.ZipFile).Name; } }";

            var result = ScriptCompilerPipeline.Compile(snippet, new IScriptCompiler[] { new RoslynCscScriptCompiler() });

            Assert.AreEqual(ScriptCompilationStatus.Success, result.Status, Describe(result));
        }

        [Test]
        public void LoadedPathsIncludeAnAssemblyLoadedAfterTheFirstCall()
        {
            ScriptCompilerReferences.GetLoadedPaths();

            Assembly late = null;
            foreach (var name in new[]
                     {
                         "System.Transactions", "System.Data.DataSetExtensions", "System.Configuration.Install",
                         "System.ServiceProcess", "System.Messaging"
                     })
            {
                if (AppDomain.CurrentDomain.GetAssemblies().Any(loaded => loaded.GetName().Name == name))
                    continue;

                try
                {
                    late = Assembly.Load(name);
                    break;
                }
                catch (Exception)
                {
                }
            }

            if (late == null || string.IsNullOrEmpty(late.Location))
                Assert.Ignore("No BCL assembly was left unloaded to load now.");

            var names = ScriptCompilerReferences.GetLoadedPaths()
                .Select(path => Path.GetFileNameWithoutExtension(path))
                .ToArray();
            CollectionAssert.Contains(names, late.GetName().Name);
        }

        [Test]
        public void AMissingPackageAnswersPackageNotInstalledWithAnInstallHint()
        {
            var missing = ToolPackageGate.Compute(new[] { "memory_query_top_objects" }, new string[0]);
            Assert.IsTrue(missing.TryGetValue("memory_query_top_objects", out var package));

            var error = JObject.Parse(
                MCPExecutionBridge.WithheldToolError("memory_query_top_objects", "full", package, "profile hint"));

            Assert.AreEqual("PACKAGE_NOT_INSTALLED", (string)error["code"]);
            Assert.AreEqual("com.unity.memoryprofiler", (string)error["data"]["package"]);
            StringAssert.Contains("install_package", (string)error["hint"]);
            StringAssert.Contains("core", (string)error["hint"], "install_package is hidden under core: the hint has to say so");
            StringAssert.DoesNotContain("does not help", (string)error["hint"]);
        }

        [Test]
        public void AProfileRefusalStillAnswersToolNotExposedWithItsHint()
        {
            var error = JObject.Parse(MCPExecutionBridge.WithheldToolError("create_primitive", "minimal", null, "profile hint"));

            Assert.AreEqual("TOOL_NOT_EXPOSED", (string)error["code"]);
            Assert.AreEqual("minimal", (string)error["data"]["profile"]);
            Assert.AreEqual("profile hint", (string)error["hint"]);
        }

        [UnityTest]
        public IEnumerator TheBridgeAnswersPackageNotInstalledEvenUnderTheFullProfile()
        {
            var tool = ToolRegistry.MethodCache.Keys.FirstOrDefault(ToolPackageGate.IsUnavailable);
            if (tool == null)
                Assert.Ignore("Every optional package is installed here.");

            var tempRoot = Path.Combine(Path.GetTempPath(), "kitwright-execfix-" + Guid.NewGuid().ToString("N"));
            var threadHelper = new EditorThreadHelper();
            try
            {
                var settings = new SettingsController(tempRoot);
                settings.MCPToolExportProfile = "full";
                var bridge = new MCPExecutionBridge(
                    threadHelper, settings, new StateController(), new FunctionInvoker(), null);

                var call = bridge.ExecuteToolAsync(tool, new Dictionary<string, object>(), CancellationToken.None);
                var startedAt = Time.realtimeSinceStartup;
                while (!call.IsCompleted && Time.realtimeSinceStartup - startedAt < 5f)
                    yield return null;

                Assert.IsTrue(call.IsCompleted, "the call never answered");
                var error = JObject.Parse(call.Result);
                Assert.AreEqual("PACKAGE_NOT_INSTALLED", (string)error["code"]);
                Assert.AreEqual(ToolPackageGate.MissingPackageFor(tool), (string)error["data"]["package"]);
            }
            finally
            {
                threadHelper.Dispose();
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        }

        [UnityTest]
        public IEnumerator ARefusedCallDoesNotQueueBehindARunningMutatingOne()
        {
            var tempRoot = Path.Combine(Path.GetTempPath(), "kitwright-execfix-" + Guid.NewGuid().ToString("N"));
            var threadHelper = new EditorThreadHelper();
            ToolGateProbeProvider.Finish = new TaskCompletionSource<bool>();
            ToolGateProbeProvider.WritesStarted = 0;

            try
            {
                ToolRegistry.ScanAssemblies(new[] { typeof(ToolRegistry).Assembly, typeof(ToolGateProbeProvider).Assembly });
                var settings = new SettingsController(tempRoot);
                var bridge = new MCPExecutionBridge(
                    threadHelper, settings, new StateController(), new FunctionInvoker(), null);
                var noArgs = new Dictionary<string, object>();

                bridge.ExecuteToolAsync("tool_gate_write_probe", noArgs, CancellationToken.None);
                Assert.AreEqual(1, ToolGateProbeProvider.WritesStarted, "the probe should be holding the gate");

                settings.MCPToolExportProfile = "minimal";
                var refused = bridge.ExecuteToolAsync("create_primitive", noArgs, CancellationToken.None);
                var startedAt = Time.realtimeSinceStartup;
                while (!refused.IsCompleted && Time.realtimeSinceStartup - startedAt < 5f)
                    yield return null;

                Assert.IsTrue(refused.IsCompleted, "a refused call waited for the running mutating call");
                StringAssert.Contains("TOOL_NOT_EXPOSED", refused.Result);
            }
            finally
            {
                ToolGateProbeProvider.Finish.TrySetResult(true);
                threadHelper.Dispose();
                ToolRegistry.ScanAssemblies();
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
