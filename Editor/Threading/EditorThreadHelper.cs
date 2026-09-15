// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace KitWright.Editor.Threading
{
    internal class EditorThreadHelper : IDisposable
    {
        private readonly ConcurrentQueue<(Func<object> func, TaskCompletionSource<object> tcs, CancellationToken ct)> _funcQueue
            = new ConcurrentQueue<(Func<object>, TaskCompletionSource<object>, CancellationToken)>();

        private readonly int _mainThreadId;
        private readonly SynchronizationContext _syncContext;
        private bool _disposed;

        private static long s_lastPumpTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        // Under the 30s most MCP clients allow, so our explanation beats their bare timeout.
        private const int StallProbeMs = 20_000;

        // A slow tool keeps the pump ticking while it awaits; only a stalled pump means blocked.
        private const int PumpStaleMs = 5_000;

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        internal static TimeSpan SinceLastPump =>
            TimeSpan.FromSeconds((double)(System.Diagnostics.Stopwatch.GetTimestamp() - Interlocked.Read(ref s_lastPumpTicks)) / System.Diagnostics.Stopwatch.Frequency);

        private static int s_workItemDepth;

        internal static bool WorkItemRunning => Volatile.Read(ref s_workItemDepth) > 0;

        // A tool blocking the main thread synchronously (BuildPlayer, SwitchActiveBuildTarget)
        // stalls the pump exactly like a modal does. Failing it here would defeat its
        // [LongRunningTool] budget and make the client retry into a second build (CoplayDev #1130).
        internal static bool LooksBlocked(
            bool alreadyCompleted, TimeSpan sinceLastPump, bool workItemRunning, bool dialogOpen)
        {
            if (alreadyCompleted || sinceLastPump.TotalMilliseconds < PumpStaleMs)
                return false;

            return dialogOpen || !workItemRunning;
        }

        internal static string BlockedMessage(TimeSpan sinceLastPump)
        {
            return BlockedMessage(sinceLastPump, Win32Dialogs.BlockingDialog());
        }

        internal static string BlockedMessage(TimeSpan sinceLastPump, string dialog)
        {
            var cause = string.IsNullOrEmpty(dialog)
                ? "The usual cause is a modal dialog waiting for a click in the Unity window - most often " +
                  "'Scene(s) Have Been Modified' after something tried to replace a scene with unsaved changes."
                : $"A modal dialog is open and owns the editor's message loop: {dialog}.";

            return $"EDITOR_NOT_PUMPING: the Unity editor loop has not ticked for {sinceLastPump.TotalSeconds:F0}s, " +
                   $"so this call is queued and cannot run. {cause} " +
                   "Bring Unity to the front and dismiss it, then retry. " +
                   "The queued call is dropped rather than left to run once the editor resumes, so a " +
                   "retry applies it once - unless it had already started, which no cancellation stops.";
        }

        public EditorThreadHelper()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            _syncContext = SynchronizationContext.Current;
            EditorApplication.update += ProcessQueues;
        }

        // QueuePlayerLoopUpdate is main-thread-only, so off-thread callers go through the
        // captured sync context. Unfocused post-to-exec: ~154ms with this wake vs ~238ms without
        // (Unity's MCP bridge probe).
        private void WakeEditorLoop()
        {
            if (IsMainThread)
            {
                EditorApplication.QueuePlayerLoopUpdate();
                return;
            }

            _syncContext?.Post(static _ => EditorApplication.QueuePlayerLoopUpdate(), null);
        }

        private static void FailIfEditorIsBlocked<T>(TaskCompletionSource<T> tcs, CancellationTokenSource queuedItem)
        {
            Task.Delay(StallProbeMs).ContinueWith(_ =>
            {
                var idle = SinceLastPump;

                // Reading window titles is the expensive half and it talks to the editor thread's
                // message loop, so ask the free questions first: a finished request or a pump that
                // ticked recently rules a block out on its own, whatever any dialog says.
                if (!LooksBlocked(tcs.Task.IsCompleted, idle, WorkItemRunning, true))
                    return;

                var dialog = Win32Dialogs.BlockingDialog();
                if (!LooksBlocked(tcs.Task.IsCompleted, idle, WorkItemRunning, dialog != null))
                    return;

                FailBlockedCall(tcs, idle, dialog, queuedItem);
            }, TaskScheduler.Default);
        }

        // Split out of the timer so a test can drive it without waiting StallProbeMs for the probe.
        internal static bool FailBlockedCall<T>(
            TaskCompletionSource<T> tcs, TimeSpan idle, string dialog, CancellationTokenSource queuedItem)
        {
            if (!tcs.TrySetException(new TimeoutException(BlockedMessage(idle, dialog))))
                return false;

            // The caller has its answer, so the item must not still be waiting to mutate the
            // project once the editor resumes - ProcessQueues drops a cancelled item, and the
            // client's retry is then the only thing that runs.
            queuedItem.Cancel();
            return true;
        }

        public Task<T> ExecuteOnEditorThreadAsync<T>(Func<T> func)
        {
            if (_disposed)
                return CreateCanceledTask<T>();

            if (IsMainThread)
            {
                try
                {
                    return Task.FromResult(func());
                }
                catch (Exception ex)
                {
                    return Task.FromException<T>(ex);
                }
            }

            var outerTcs = new TaskCompletionSource<T>();
            var tcs = new TaskCompletionSource<object>();
            tcs.Task.ContinueWith(
                task =>
                {
                    if (task.IsCanceled)
                        outerTcs.TrySetCanceled();
                    else if (task.IsFaulted)
                        outerTcs.TrySetException(task.Exception?.InnerException ?? task.Exception ?? new Exception("Unknown error"));
                    else
                        outerTcs.TrySetResult((T)task.Result);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var itemCts = new CancellationTokenSource();
            _funcQueue.Enqueue((() => func(), tcs, itemCts.Token));
            WakeEditorLoop();
            FailIfEditorIsBlocked(outerTcs, itemCts);
            return outerTcs.Task;
        }

        public Task<T> ExecuteAsyncOnEditorThreadAsync<T>(Func<Task<T>> asyncFunc, CancellationToken ct = default)
        {
            if (_disposed || ct.IsCancellationRequested)
                return CreateCanceledTask<T>();

            if (IsMainThread)
            {
                return asyncFunc();
            }

            var outerTcs = new TaskCompletionSource<T>();
            var ctRegistration = ct.CanBeCanceled
                ? ct.Register(() => outerTcs.TrySetCanceled(ct))
                : default(CancellationTokenRegistration?);

            // Linked, so the caller's own cancellation still drops the queued item, and the stall
            // watchdog gets a handle to drop it too.
            var itemCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var tcs = new TaskCompletionSource<object>();
            tcs.Task.ContinueWith(
                task =>
                {
                    if (task.IsCanceled)
                        outerTcs.TrySetCanceled();
                    else if (task.IsFaulted)
                        outerTcs.TrySetException(task.Exception?.InnerException ?? task.Exception ?? new Exception("Unknown error"));
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _funcQueue.Enqueue((() =>
            {
                var running = asyncFunc();

                // ProcessQueues' own count drops the moment this returns, which for a tool that
                // awaits is its first real await rather than its end. The stall watchdog reads that
                // count to tell a blocked editor from a tool still working, so carry it until this
                // one finishes. A tool that already finished inside asyncFunc needs none of this,
                // and must not be left counted after ProcessQueues returns.
                if (!running.IsCompleted)
                {
                    Interlocked.Increment(ref s_workItemDepth);
                    running.ContinueWith(
                        _ => Interlocked.Decrement(ref s_workItemDepth),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }

                running.ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        outerTcs.TrySetException(task.Exception?.InnerException ?? task.Exception ?? new Exception("Unknown error"));
                    else if (task.IsCanceled)
                        outerTcs.TrySetCanceled();
                    else
                        outerTcs.TrySetResult(task.Result);
                });
                return (object)null;
            }, tcs, itemCts.Token));
            WakeEditorLoop();

            // Dispose the linked CTS when the operation completes to avoid leaking the callback
            // registration on the parent token for the lifetime of the server.
            outerTcs.Task.ContinueWith(_ =>
            {
                try { itemCts.Dispose(); } catch { /* best effort */ }
            }, TaskContinuationOptions.ExecuteSynchronously);

            if (ctRegistration.HasValue)
                outerTcs.Task.ContinueWith(_ => ctRegistration.Value.Dispose(), TaskContinuationOptions.ExecuteSynchronously);

            FailIfEditorIsBlocked(outerTcs, itemCts);
            return outerTcs.Task;
        }

        internal void ProcessQueues()
        {
            Interlocked.Exchange(ref s_lastPumpTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            if (_disposed) return;

            int processedCount = 0;
            const int maxPerFrame = 10;

            while (processedCount < maxPerFrame && _funcQueue.TryDequeue(out var item))
            {
                // The caller's deadline passed while this sat in the queue, so running it now would
                // apply a mutation the client already gave up on - and double-apply on its retry.
                if (item.ct.IsCancellationRequested)
                {
                    item.tcs.TrySetCanceled();
                    continue;
                }

                Interlocked.Increment(ref s_workItemDepth);
                try
                {
                    var result = item.func();
                    item.tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.tcs.TrySetException(ex);
                }
                finally
                {
                    Interlocked.Decrement(ref s_workItemDepth);
                }
                processedCount++;
            }

            // Items past the per-frame cap get the next tick now, not after the throttle interval.
            if (!_funcQueue.IsEmpty)
                WakeEditorLoop();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            EditorApplication.update -= ProcessQueues;

            while (_funcQueue.TryDequeue(out var item))
                item.tcs.TrySetCanceled();
        }

        private static Task<T> CreateCanceledTask<T>()
        {
            var tcs = new TaskCompletionSource<T>();
            tcs.SetCanceled();
            return tcs.Task;
        }
    }
}
