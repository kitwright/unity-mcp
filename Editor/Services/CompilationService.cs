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
            }
            // Raw flag on purpose: this await is timeout-bounded, so a compile queued in a fresh
            // domain (where compilationStarted never fired) must still be waited out.
            else if (!EditorApplication.isCompiling)
            {
                return true;
            }

            TaskCompletionSource<bool> waitSource;
            lock (SyncRoot)
            {
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

        public string GetCompilationErrors(int maxEntries = 50, bool includeWarnings = false)
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

            var filtered = matching
                .Take(maxEntries)
                .ToList();

            if (filtered.Count == 0)
            {
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
                ? $"Compilation issues ({matching.Count} total, showing first {filtered.Count}; raise maxEntries for the rest):"
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
