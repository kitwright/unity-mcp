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

        private static long s_lastPumpUtcTicks = DateTime.UtcNow.Ticks;

        // Under the 30s most MCP clients allow, so our explanation beats their bare timeout.
        private const int StallProbeMs = 20_000;

        // A slow tool keeps the pump ticking while it awaits; only a stalled pump means blocked.
        private const int PumpStaleMs = 5_000;

        public bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        internal static TimeSpan SinceLastPump =>
            TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Interlocked.Read(ref s_lastPumpUtcTicks));

        internal static bool LooksBlocked(bool alreadyCompleted, TimeSpan sinceLastPump)
        {
            return !alreadyCompleted && sinceLastPump.TotalMilliseconds >= PumpStaleMs;
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
                   "The queued call still runs once the editor resumes.";
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

        private static void FailIfEditorIsBlocked<T>(TaskCompletionSource<T> tcs)
        {
            Task.Delay(StallProbeMs).ContinueWith(_ =>
            {
                var idle = SinceLastPump;
                if (!LooksBlocked(tcs.Task.IsCompleted, idle))
                    return;

                tcs.TrySetException(new TimeoutException(BlockedMessage(idle)));
            }, TaskScheduler.Default);
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

            _funcQueue.Enqueue((() => func(), tcs, CancellationToken.None));
            WakeEditorLoop();
            FailIfEditorIsBlocked(outerTcs);
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
                asyncFunc().ContinueWith(task =>
                {
                    if (task.IsFaulted)
                        outerTcs.TrySetException(task.Exception?.InnerException ?? task.Exception ?? new Exception("Unknown error"));
                    else if (task.IsCanceled)
                        outerTcs.TrySetCanceled();
                    else
                        outerTcs.TrySetResult(task.Result);
                });
                return (object)null;
            }, tcs, ct));
            WakeEditorLoop();

            if (ctRegistration.HasValue)
                outerTcs.Task.ContinueWith(_ => ctRegistration.Value.Dispose(), TaskContinuationOptions.ExecuteSynchronously);

            FailIfEditorIsBlocked(outerTcs);
            return outerTcs.Task;
        }

        internal void ProcessQueues()
        {
            Interlocked.Exchange(ref s_lastPumpUtcTicks, DateTime.UtcNow.Ticks);
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

                try
                {
                    var result = item.func();
                    item.tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    item.tcs.TrySetException(ex);
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
