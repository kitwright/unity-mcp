// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.State;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor.Compilation;

namespace KitWright.Editor.Tests
{
    public sealed class CompilationServiceTests
    {
        [TearDown]
        public void ClearCompilingOverride()
        {
            CompilationService.IsCompilingOverride = null;
        }

        [Test]
        public void ResolveIsCompiling_IgnoresRawFlagWhileNoPipelineCompileIsRunning()
        {
            Assert.IsFalse(CompilationService.ResolveIsCompiling(true, false),
                "A deferred reload (LockReloadAssemblies) leaves the raw flag true with nothing compiling.");
            Assert.IsTrue(CompilationService.ResolveIsCompiling(true, true));
            Assert.IsFalse(CompilationService.ResolveIsCompiling(false, true));
        }

        // get_editor_state is what agents poll to decide whether to wait, so it has to report the
        // resolved flag rather than EditorApplication.isCompiling.
        [Test]
        public void GetEditorState_ReportsTheResolvedCompilingFlag()
        {
            var expected = CompilationService.IsActuallyCompiling;
            var response = JObject.FromObject(EditorStateFunctions.GetEditorState());

            Assert.IsTrue(response.Value<bool>("success"), response.ToString());
            Assert.AreEqual(expected, response["data"].Value<bool>("isCompiling"));
            Assert.IsFalse(expected, "Tests only run once compilation finished, so nothing should be compiling.");
        }

        [Test]
        public void GetCompilationErrors_ReportsTotalAndShownCountsWhenTruncated()
        {
            var field = typeof(CompilationService).GetField("LatestMessages",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "LatestMessages was renamed; update this test.");

            var messages = (List<CompilerMessage>)field.GetValue(null);
            var backup = messages.ToList();

            try
            {
                messages.Clear();
                for (var i = 0; i < 3; i++)
                {
                    messages.Add(new CompilerMessage
                    {
                        message = "error " + i,
                        file = "Assets/Fake.cs",
                        line = i + 1,
                        type = CompilerMessageType.Error
                    });
                }

                var truncated = CompilationService.Instance.GetCompilationErrors(maxEntries: 1);
                StringAssert.Contains("3 total", truncated);
                StringAssert.Contains("Showing 1-1 of 3; pass cursor=1", truncated);
                Assert.That(truncated.Split('\n').Count(line => line.StartsWith("- [")), Is.EqualTo(1));

                var second = CompilationService.Instance.GetCompilationErrors(maxEntries: 1, cursor: 1);
                StringAssert.Contains("error 1", second);
                Assert.That(second, Does.Not.Contain("error 0"));
                StringAssert.Contains("pass cursor=2", second);

                var past = CompilationService.Instance.GetCompilationErrors(maxEntries: 1, cursor: 3);
                StringAssert.Contains("cursor=3 is past the end", past);

                var complete = CompilationService.Instance.GetCompilationErrors(maxEntries: 10);
                StringAssert.Contains("3 total", complete);
                Assert.That(complete, Does.Not.Contain("cursor"));
                Assert.That(complete.Split('\n').Count(line => line.StartsWith("- [")), Is.EqualTo(3));

                // A separate hop from the service tested above.
                var viaTool = CompilationFunctions.GetCompilationErrors(max_entries: 1, cursor: 1);
                StringAssert.Contains("error 1", viaTool);
                Assert.That(viaTool, Does.Not.Contain("error 0"));
            }
            finally
            {
                messages.Clear();
                messages.AddRange(backup);
            }
        }

        // The four gates below only change behaviour while compiling, which no test can reach for
        // real, so each one is driven through CompilationService.IsCompilingOverride.
        [Test]
        public void PostReloadRestart_WaitsWhileCompiling()
        {
            CompilationService.IsCompilingOverride = true;
            Assert.IsTrue(MCPServerDomainReloadHandler.ShouldWaitForCompilationBeforeRestart(),
                "Restarting mid-compile binds the port right before the next domain reload orphans it.");

            CompilationService.IsCompilingOverride = false;
            Assert.IsFalse(MCPServerDomainReloadHandler.ShouldWaitForCompilationBeforeRestart());
        }

        [Test]
        public void PendingCompletion_IsDeferredWhileCompiling()
        {
            CompilationService.IsCompilingOverride = true;
            Assert.IsTrue(DomainReloadHandler.ShouldDeferPendingCompletion(),
                "Returning to the previous state mid-compile loses the pending function on the reload.");

            CompilationService.IsCompilingOverride = false;
            Assert.IsFalse(DomainReloadHandler.ShouldDeferPendingCompletion());
        }

        [Test]
        public void NoThrottleLease_IsHeldWhileCompiling()
        {
            CompilationService.IsCompilingOverride = true;
            Assert.IsTrue(NoThrottleLease.ShouldHoldLease(),
                "Expiring mid-compile hands throttling back while the compile is still running.");

            CompilationService.IsCompilingOverride = false;
            Assert.IsFalse(NoThrottleLease.ShouldHoldLease());
        }

        [Test]
        public void ExternalSyncRecovery_WaitsWhileCompiling()
        {
            CompilationService.IsCompilingOverride = true;
            Assert.IsTrue(ExternalSyncRecoveryTracker.ShouldWaitForCompilation(),
                "The compile outcome is not known yet, so recovery info must not be written early.");

            CompilationService.IsCompilingOverride = false;
            Assert.IsFalse(ExternalSyncRecoveryTracker.ShouldWaitForCompilation());
        }

        // A waiter enters while the raw flag is already true and holds whatever source is current,
        // which can be a tick or two before compilationStarted fires. Only the source live when
        // compilationFinished fires is completed, so replacing it there strands that waiter on a
        // compile that in fact finished.
        [Test]
        public void CompilationStarted_KeepsTheSourceAWaiterIsAlreadyHolding()
        {
            var field = typeof(CompilationService).GetField("_compilationFinishedTcs",
                BindingFlags.NonPublic | BindingFlags.Static);
            var started = typeof(CompilationService).GetMethod("HandleCompilationStarted",
                BindingFlags.NonPublic | BindingFlags.Static);
            var finished = typeof(CompilationService).GetMethod("HandleCompilationFinished",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "_compilationFinishedTcs was renamed; update this test.");
            Assert.IsNotNull(started, "HandleCompilationStarted was renamed; update this test.");
            Assert.IsNotNull(finished, "HandleCompilationFinished was renamed; update this test.");

            var backup = field.GetValue(null);
            try
            {
                var held = new TaskCompletionSource<bool>();
                field.SetValue(null, held);

                started.Invoke(null, new object[] { null });
                Assert.AreSame(held, field.GetValue(null));

                finished.Invoke(null, new object[] { null });
                Assert.IsTrue(held.Task.IsCompletedSuccessfully, "The waiter would have timed out instead.");

                // A source nobody is waiting on any more is stale, so the next compile does get a fresh one.
                started.Invoke(null, new object[] { null });
                Assert.AreNotSame(held, field.GetValue(null));
                finished.Invoke(null, new object[] { null });
            }
            finally
            {
                field.SetValue(null, backup);
            }
        }
    }
}
