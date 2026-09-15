// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.Compilation;

namespace KitWright.Editor.Services
{
    [InitializeOnLoad]
    internal class CompilationService
    {
        private static readonly object SyncRoot = new object();
        private static readonly List<CompilerMessage> LatestMessages = new List<CompilerMessage>();
        private static TaskCompletionSource<bool> _compilationFinishedTcs = CreateCompletionSource();
        private static bool _subscribed;
        private static volatile bool _pipelineCompilationRunning;

        public static CompilationService Instance { get; private set; }

        public bool IsCompiling => IsActuallyCompiling;
        public EditorRefreshResult LastRefreshResult { get; private set; }

        /// <summary>Test seam: when set, stands in for the resolved flag so the gates that only run
        /// while compiling are reachable without a real compile. Always null in production.</summary>
        internal static bool? IsCompilingOverride;

        /// <summary>Adapted from CoplayDev/unity-mcp MCPForUnity/Editor/Services/EditorStateCache.cs (MIT).
        /// EditorApplication.isCompiling stays true with nothing compiling whenever an assembly reload is
        /// deferred (LockReloadAssemblies, Recompile-After-Finished-Playing), so gates that wait for it to
        /// clear would never release. The event-tracked pipeline flag is authoritative there.</summary>
        internal static bool IsActuallyCompiling =>
            IsCompilingOverride ?? ResolveIsCompiling(EditorApplication.isCompiling, _pipelineCompilationRunning);

        internal static bool ResolveIsCompiling(bool rawIsCompiling, bool pipelineRunning) => rawIsCompiling && pipelineRunning;

        static CompilationService()
        {
            EnsureInitialized();
            Instance = new CompilationService();
        }

        public CompilationService()
        {
            EnsureInitialized();
            Instance = this;
        }

        public async Task<bool> WaitForCompilationAsync(bool forceRefresh, int timeoutSeconds)
        {
            if (forceRefresh)
            {
                LastRefreshResult = await EditorRefreshPipeline.RefreshAndRequestCompilationAsync(
                    forceUpdate: true,
                    verifyScriptChanges: true);

                if (LastRefreshResult.ScriptChangesStillPending)
                    return false;

                // Nothing stale means nothing will compile, so skip waiting for a start.
                if (!LastRefreshResult.CompilationOrImportStarted &&
                    (!LastRefreshResult.LatestScriptState.HasPendingScriptChanges ||
                     !await WaitForCompilationToStartAsync(timeoutSeconds).ConfigureAwait(false)))
                {
                    return true;
                }

                // What the pipeline saw start may have been an import, which queues the compile it
                // triggers a few frames later with the raw flag clear in between. Polling it here
                // waits for a compile about to begin instead of reporting it as already finished.
                if (!EditorApplication.isCompiling)
                    await WaitForCompilationToStartAsync(timeoutSeconds).ConfigureAwait(false);
            }
            TaskCompletionSource<bool> waitSource;
            lock (SyncRoot)
            {
                // Raw flag on purpose: this await is timeout-bounded, so a compile queued in a fresh
                // domain (where compilationStarted never fired) must still be waited out.
                // Checked inside the lock so a compile that finishes between here and the lock
                // cannot leave a fresh TCS nothing will complete.
                // An import fires no compilationFinished, so a refresh that only imported has to
                // answer here rather than spend the whole timeout on a source nothing completes.
                if (!EditorApplication.isCompiling)
                    return true;

                if (_compilationFinishedTcs == null || _compilationFinishedTcs.Task.IsCompleted)
                {
                    _compilationFinishedTcs = CreateCompletionSource();
                }

                waitSource = _compilationFinishedTcs;
            }

            var completedTask = await Task.WhenAny(
                waitSource.Task,
                Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).ConfigureAwait(false);

            return completedTask == waitSource.Task && waitSource.Task.IsCompletedSuccessfully;
        }

        public string GetCompilationErrors(int maxEntries = 50, bool includeWarnings = false, int cursor = 0)
        {
            maxEntries = Math.Max(1, maxEntries);

            List<CompilerMessage> messages;
            lock (SyncRoot)
            {
                messages = LatestMessages.ToList();
            }

            var matching = messages
                .Where(message => message.type == CompilerMessageType.Error ||
                                  (includeWarnings && message.type == CompilerMessageType.Warning))
                .ToList();

            var filtered = Paging.Page(matching, cursor, maxEntries);

            if (filtered.Count == 0)
            {
                if (matching.Count > 0)
                    return $"Compilation issues ({matching.Count} total).{Paging.Suffix(cursor, 0, matching.Count)}";

                return includeWarnings
                    ? "No compilation errors or warnings detected."
                    : "No compilation errors detected.";
            }

            var lines = filtered.Select(message =>
            {
                var location = string.IsNullOrEmpty(message.file)
                    ? string.Empty
                    : $" ({message.file}:{message.line})";
                return $"- [{message.type}] {message.message}{location}";
            });

            var header = filtered.Count < matching.Count
                ? $"Compilation issues ({matching.Count} total).{Paging.Suffix(cursor, filtered.Count, matching.Count)}"
                : $"Compilation issues ({matching.Count} total):";

            return header + "\n" + string.Join("\n", lines);
        }

        private static void EnsureInitialized()
        {
            if (_subscribed)
                return;

            _subscribed = true;
            CompilationPipeline.compilationStarted += HandleCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += HandleAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += HandleCompilationFinished;
        }

        private static async Task<bool> WaitForCompilationToStartAsync(int timeoutSeconds)
        {
            if (EditorApplication.isCompiling)
                return true;

            var waitUntil = DateTime.UtcNow.AddSeconds(Math.Min(timeoutSeconds, 2));
            while (DateTime.UtcNow < waitUntil)
            {
                if (EditorApplication.isCompiling)
                    return true;

                await Task.Delay(100).ConfigureAwait(false);
            }

            return false;
        }

        private static void HandleCompilationStarted(object context)
        {
            _pipelineCompilationRunning = true;
            lock (SyncRoot)
            {
                LatestMessages.Clear();

                // A waiter that got here first is holding the current source, and only the source
                // live when compilationFinished fires is completed - replacing it would leave that
                // waiter timing out on a compile that in fact finished.
                if (_compilationFinishedTcs == null || _compilationFinishedTcs.Task.IsCompleted)
                    _compilationFinishedTcs = CreateCompletionSource();
            }
        }

        private static void HandleAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0)
            {
                return;
            }

            lock (SyncRoot)
            {
                LatestMessages.AddRange(messages);
            }
        }

        private static void HandleCompilationFinished(object obj)
        {
            _pipelineCompilationRunning = false;
            TaskCompletionSource<bool> waitSource = null;
            lock (SyncRoot)
            {
                waitSource = _compilationFinishedTcs;
            }

            waitSource?.TrySetResult(true);
        }

        private static TaskCompletionSource<bool> CreateCompletionSource()
        {
            return new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
