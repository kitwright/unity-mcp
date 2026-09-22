// Copyright (C) KitWright. All rights reserved.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.Threading;
using NUnit.Framework;
using UnityEditor.PackageManager;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class EditorThreadHelperStallTests
    {
        [Test]
        public void LooksBlocked_SeparatesAStalledEditorFromAMerelySlowTool()
        {
            Assert.IsFalse(Blocked(TimeSpan.FromMilliseconds(200)),
                "A slow but pumping editor must not be reported as blocked.");
            Assert.IsFalse(Blocked(TimeSpan.FromSeconds(4)),
                "Under the staleness threshold is still healthy.");

            Assert.IsTrue(Blocked(TimeSpan.FromSeconds(5)));
            Assert.IsTrue(Blocked(TimeSpan.FromMinutes(2)));

            Assert.IsFalse(
                EditorThreadHelper.LooksBlocked(true, TimeSpan.FromMinutes(2), false, false),
                "A call that already returned must never be failed after the fact.");
        }

        // CoplayDev/unity-mcp #1130, #1341.
        [Test]
        public void LooksBlocked_LeavesOurOwnLongSynchronousToolToItsTimeoutBudget()
        {
            Assert.IsFalse(
                EditorThreadHelper.LooksBlocked(false, TimeSpan.FromMinutes(6), true, false),
                "A six-minute build is not a blocked editor; its [LongRunningTool] ceiling owns it.");

            Assert.IsTrue(
                EditorThreadHelper.LooksBlocked(false, TimeSpan.FromMinutes(6), true, true),
                "A modal our own work item opened is still a block - that is what the probe is for.");

            Assert.IsTrue(
                EditorThreadHelper.LooksBlocked(false, TimeSpan.FromMinutes(6), false, false),
                "Nothing of ours is running, so a stale pump is someone else blocking the editor.");
        }

        // An import, a domain reload or a play-mode transition stops the pump exactly like a modal
        // does, and leaves nothing of ours counted once whatever started it has returned.
        [Test]
        public void LooksBlocked_LeavesTheEditorAloneWhileItIsImportingOrReloading()
        {
            Assert.IsTrue(
                EditorThreadHelper.LooksBlocked(false, TimeSpan.FromSeconds(24), false, false, false),
                "Baseline: a stale pump with nothing of ours running is the case this guards.");

            Assert.IsFalse(
                EditorThreadHelper.LooksBlocked(false, TimeSpan.FromSeconds(24), false, false, true),
                "The editor stopped pumping to do its own work, so nobody is blocking it.");

            Assert.IsTrue(
                EditorThreadHelper.LooksBlocked(false, TimeSpan.FromSeconds(24), false, true, true),
                "A dialog that was actually found owns the loop whatever the editor was doing.");
        }

        [Test]
        public void WorkItemRunning_IsTrueInsideTheWorkItemAndFalseOutsideIt()
        {
            Assert.IsFalse(EditorThreadHelper.WorkItemRunning, "Nothing is mid-flight before the pump.");

            PumpOnce(cancelBeforePump: false, out var sawWorkItemRunning);

            Assert.IsTrue(sawWorkItemRunning,
                "The flag has to be set around the invoke, or the probe cannot see a build.");
            Assert.IsFalse(EditorThreadHelper.WorkItemRunning, "The finally must clear it again.");
        }

        // The pump's count drops when the queued lambda returns, which for an async tool is its first
        // real await, not its end. Read naively the watchdog then sees "nothing of ours is running"
        // over a stale pump and fails a call that was only waiting on a compile.
        [Test]
        public void WorkItemRunning_StaysTrueWhileAnAsyncToolIsStillAwaiting()
        {
            using (var helper = new EditorThreadHelper())
            {
                var gate = new TaskCompletionSource<bool>();

                using (var handoff = new ManualResetEventSlim())
                {
                    Task.Run(() =>
                    {
                        helper.ExecuteAsyncOnEditorThreadAsync(async () => await gate.Task);
                        handoff.Set();
                    });

                    Assert.IsTrue(handoff.Wait(TimeSpan.FromSeconds(5)), "The work item was never queued.");
                }

                helper.ProcessQueues();

                Assert.IsTrue(EditorThreadHelper.WorkItemRunning,
                    "The tool is still awaiting, so the watchdog must not read the editor as idle.");

                gate.SetResult(true);

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (EditorThreadHelper.WorkItemRunning && DateTime.UtcNow < deadline)
                    Thread.Sleep(10);

                Assert.IsFalse(EditorThreadHelper.WorkItemRunning,
                    "The count has to come back down once the tool finishes, or nothing ever looks blocked again.");
            }
        }

        private static bool Blocked(TimeSpan sinceLastPump) =>
            EditorThreadHelper.LooksBlocked(false, sinceLastPump, false, false);

        [Test]
        public void BlockedMessage_NamesTheCauseAndTheWayOut()
        {
            var message = EditorThreadHelper.BlockedMessage(TimeSpan.FromSeconds(21));

            StringAssert.Contains("EDITOR_NOT_PUMPING", message);
            StringAssert.Contains("21s", message);
            StringAssert.Contains("modal dialog", message);
            StringAssert.Contains("Scene(s) Have Been Modified", message);
        }

        [Test]
        public void BlockedMessage_NamesTheDialogWhenTheProbeIdentifiedOne()
        {
            var message = EditorThreadHelper.BlockedMessage(
                TimeSpan.FromSeconds(21), "Scene(s) Have Been Modified [buttons: Save | Don't Save | Cancel]");

            StringAssert.Contains("EDITOR_NOT_PUMPING", message);
            StringAssert.Contains("Scene(s) Have Been Modified [buttons: Save | Don't Save | Cancel]", message);
            Assert.IsFalse(message.Contains("The usual cause"),
                "A named dialog must replace the guess, not sit next to it.");
        }

        [Test]
        public void FailBlockedCall_CancelsTheItemItJustGaveUpOn()
        {
            var tcs = new TaskCompletionSource<string>();
            using (var queuedItem = new CancellationTokenSource())
            {
                Assert.IsTrue(EditorThreadHelper.FailBlockedCall(tcs, TimeSpan.FromSeconds(21), null, queuedItem));

                Assert.IsInstanceOf<TimeoutException>(
                    tcs.Task.Exception?.InnerException, "the caller has to see why it was given up on");
                Assert.IsTrue(queuedItem.IsCancellationRequested,
                    "an item left queued runs once the modal closes - and again on the client's retry");
            }
        }

        [Test]
        public void FailBlockedCall_LeavesAFinishedCallAlone()
        {
            var tcs = new TaskCompletionSource<string>();
            tcs.SetResult("done");

            using (var queuedItem = new CancellationTokenSource())
            {
                Assert.IsFalse(EditorThreadHelper.FailBlockedCall(tcs, TimeSpan.FromSeconds(21), null, queuedItem));
                Assert.IsFalse(queuedItem.IsCancellationRequested,
                    "the work already ran, so cancelling would only report a lie to the caller");
            }
        }

        [Test]
        public void ProcessQueues_DropsAnItemTheCallerAlreadyGaveUpOn()
        {
            using (var helper = new EditorThreadHelper())
            using (var callerCts = new CancellationTokenSource())
            {
                var ran = 0;

                // Off the main thread, or the helper runs the body inline instead of queueing it.
                var queued = Task.Run(() => helper.ExecuteAsyncOnEditorThreadAsync<string>(
                    () =>
                    {
                        Interlocked.Increment(ref ran);
                        return Task.FromResult("ran");
                    },
                    callerCts.Token));
                queued.Wait(TimeSpan.FromSeconds(5));

                callerCts.Cancel();
                helper.ProcessQueues();

                Assert.AreEqual(0, Volatile.Read(ref ran),
                    "the body must not run after the caller was told the call failed");
            }
        }

        [Test]
        public void BlockingDialog_ReportsNothingWhileTheEditorIsUnblocked()
        {
            Assert.IsNull(Win32Dialogs.BlockingDialog(),
                "No modal is open while this test runs, so the probe must not name one.");
        }

        [Test]
        public void DialogProbe_NeverSendsAWindowMessageWithoutATimeout()
        {
            var path = ResolveEditorSourcePath("Threading/Win32Dialogs.cs");
            var source = File.ReadAllText(path);

            // GetWindowText on a window of this process sends WM_GETTEXT and waits forever for the
            // owning thread to pump. The probe runs on a thread pool thread, and during a domain
            // reload the editor thread does not pump -- so the call parks in user32 where Mono
            // cannot abort it, and the domain unload waits on that job for the rest of the session.
            // The trailing "(" matches a declaration or a call but not the prose explaining why
            // these are banned, which has to be free to name them.
            Assert.That(source, Does.Not.Contain("GetWindowTextW("),
                "Read window titles with SendMessageTimeoutW(WM_GETTEXT), not GetWindowText: " + path);
            Assert.That(source, Does.Contain("SendMessageTimeoutW"), path);
            Assert.That(source, Does.Contain("WM_GETTEXT"), path);

            // Both dialog tools are [OffEditorThread], so the click path is on a pool thread too.
            Assert.That(source, Does.Not.Contain("SendMessageW("),
                "Every send from here waits on the editor thread's message loop, so all of them "
                + "need a timeout: " + path);
        }

        [Test]
        public void LooksBlocked_CanBeRuledOutBeforeAnyWindowIsEnumerated()
        {
            // FailIfEditorIsBlocked asks with dialogOpen: true first so it can skip the Win32 walk.
            // That shortcut is only sound if a "no" under the most pessimistic dialog answer is
            // also a "no" under the real one.
            foreach (var completed in new[] { true, false })
            foreach (var running in new[] { true, false })
            foreach (var busy in new[] { true, false })
            foreach (var idle in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(4), TimeSpan.FromMinutes(2) })
            {
                if (EditorThreadHelper.LooksBlocked(completed, idle, running, true, busy))
                    continue;

                Assert.IsFalse(
                    EditorThreadHelper.LooksBlocked(completed, idle, running, false, busy),
                    $"completed={completed} running={running} busy={busy} idle={idle}: ruled out with " +
                    "a dialog assumed open, so it must stay ruled out without one.");
            }
        }

        private static string ResolveEditorSourcePath(string relative)
        {
            var packageInfo = PackageInfo.FindForAssembly(typeof(EditorThreadHelper).Assembly);
            var root = packageInfo != null
                ? Path.Combine(packageInfo.resolvedPath, "Editor")
                : Path.Combine(Application.dataPath, "unity-mcp", "Editor");
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.IsTrue(File.Exists(path), "Source was not found at " + path);
            return path;
        }

        [Test]
        public void SinceLastPump_IsFreshWhileTheEditorIsRunningThisTest()
        {
            // Batchmode drives the loop only between tests, so the live pump clock is meaningless
            // here — CI measured 23s while the run was perfectly healthy.
            if (Application.isBatchMode)
                Assert.Ignore("The editor loop does not tick during a batchmode test body.");

            Assert.Less(EditorThreadHelper.SinceLastPump.TotalSeconds, 5,
                "The editor is pumping while this test runs, so the watchdog must see it as healthy.");
        }

        [Test]
        public void QueuedWork_RunsOnTheNextPump()
        {
            Assert.IsTrue(PumpOnce(cancelBeforePump: false),
                "A live work item must still run, or the abandon check is passing vacuously.");
        }

        [Test]
        public void QueuedWork_IsDroppedWhenItsCallerAlreadyGaveUp()
        {
            Assert.IsFalse(PumpOnce(cancelBeforePump: true),
                "A call whose deadline passed must never mutate the project after the fact.");
        }

        private static bool PumpOnce(bool cancelBeforePump) =>
            PumpOnce(cancelBeforePump, out _);

        private static bool PumpOnce(bool cancelBeforePump, out bool sawWorkItemRunning)
        {
            var ran = false;
            var sawFlag = false;

            using (var helper = new EditorThreadHelper())
            using (var cts = new CancellationTokenSource())
            {
                // Queued from a worker thread so it lands in the queue instead of running inline.
                // Wait for the CALL to return, not the task it returns: that one only completes once
                // ProcessQueues runs the item, and this thread is the one that has to pump it.
                using (var handoff = new ManualResetEventSlim())
                {
                    Task.Run(() =>
                    {
                        helper.ExecuteAsyncOnEditorThreadAsync(() =>
                        {
                            ran = true;
                            sawFlag = EditorThreadHelper.WorkItemRunning;
                            return Task.FromResult(true);
                        }, cts.Token);
                        handoff.Set();
                    });

                    Assert.IsTrue(handoff.Wait(TimeSpan.FromSeconds(5)), "The work item was never queued.");
                }

                if (cancelBeforePump)
                    cts.Cancel();

                helper.ProcessQueues();
            }

            sawWorkItemRunning = sawFlag;
            return ran;
        }
    }
}
