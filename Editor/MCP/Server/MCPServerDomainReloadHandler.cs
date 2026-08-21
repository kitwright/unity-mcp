// Copyright (C) KitWright. Licensed under MIT.

using System;
using KitWright.Editor.Services;
using KitWright.Editor.Settings;
using UnityEditor;
using KitWright.Editor.DI;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Handles Unity domain reload for the MCP server.
    /// Saves server state before reload and restarts after reload if it was running.
    /// </summary>
    [InitializeOnLoad]
    internal static class MCPServerDomainReloadHandler
    {
        private const string WasRunningKey = "KitWright_MCPServer_WasRunning";
        private const string PortKey = "KitWright_MCPServer_Port";
        private const string RestartDeadlineTicksKey = "KitWright_MCPServer_RestartDeadlineTicks";
        private static readonly TimeSpan RestartRetryWindow = TimeSpan.FromMinutes(5);
        private static bool _restartScheduled;
        private static bool _restartInProgress;

        static MCPServerDomainReloadHandler()
        {
            if (Application.isBatchMode)
                return;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterReload;

            if (IsPendingPostReloadRestart())
                SchedulePostReloadRestart();
        }

        internal static void PrepareForReload(IServiceProvider services)
        {
            try
            {
                var mcpServer = services?.GetService(typeof(MCPServerService)) as MCPServerService;
                if (mcpServer?.IsRunning != true)
                    return;

                PluginDebugLogger.Log("[KitWright MCP Server] Saving state before domain reload");
                SessionState.SetBool(WasRunningKey, true);
                SessionState.SetInt(PortKey, mcpServer.Port);
                SessionState.SetString(RestartDeadlineTicksKey, DateTime.UtcNow.Add(RestartRetryWindow).Ticks.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error preparing reload state: {ex.Message}");
            }
        }

        private static void OnBeforeReload()
        {
            try
            {
                PrepareForReload(RootScopeServices.Services);

                var mcpServer = RootScopeServices.Services?.GetService(typeof(MCPServerService)) as MCPServerService;
                if (mcpServer?.IsRunning == true)
                {
                    // Must run synchronously: Unity unloads the AppDomain right after this callback
                    // returns and will not wait for a fire-and-forget Stop task. Awaiting was the
                    // root cause of port 8765 staying bound across reloads (issue #1).
                    mcpServer.StopSync();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error in OnBeforeReload: {ex.Message}");
            }
            finally
            {
                // Runs even when the stop above was skipped or threw: a listener left bound here
                // survives the reload as an orphan and pushes the next domain onto another port.
                HttpMCPTransport.CloseActiveListener();
            }
        }

        /// <summary>
        /// True when a domain reload just happened and the post-reload auto-restart in
        /// <see cref="OnAfterReload"/> is going to (re)start the server. Other startup paths
        /// (eg. <c>RootScopeServices.Initialize</c>) should skip auto-start in this case to avoid
        /// racing two <c>StartAsync</c> calls against a port that may still be releasing.
        /// </summary>
        internal static bool IsPendingPostReloadRestart()
        {
            return SessionState.GetBool(WasRunningKey, false);
        }

        private static void OnAfterReload()
        {
            try
            {
                if (SessionState.GetBool(WasRunningKey, false))
                {
                    PluginDebugLogger.Log("[KitWright MCP Server] Restarting server after domain reload");
                    SchedulePostReloadRestart();
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error in OnAfterReload: {ex.Message}");
            }
        }

        private static void SchedulePostReloadRestart()
        {
            if (_restartScheduled)
                return;

            _restartScheduled = true;
            EditorApplication.delayCall += RestartWhenEditorIsReady;
            EditorApplication.update += RestartWhenEditorIsReady;
            RestartWhenEditorIsReady();
        }

        private static async void RestartWhenEditorIsReady()
        {
            if (_restartInProgress)
                return;

            if (!SessionState.GetBool(WasRunningKey, false))
            {
                ClearScheduledRestart();
                return;
            }

            if (ShouldWaitForCompilationBeforeRestart())
                return;

            if (RestartDeadlineExpired())
            {
                Debug.LogError("[KitWright MCP Server] Timed out restarting after domain reload.");
                ClearPendingRestart();
                ClearScheduledRestart();
                return;
            }

            var services = RootScopeServices.Services;
            if (services == null)
                return;

            var mcpServer = services.GetService(typeof(MCPServerService)) as MCPServerService;
            if (mcpServer == null)
                return;

            _restartInProgress = true;

            try
            {
                int savedPort = SessionState.GetInt(PortKey, -1);
                if (savedPort > 0)
                    MCPServerService.PreferredStartupPort = savedPort;

                if (!mcpServer.IsRunning)
                {
                    HttpMCPTransport.ArmOrphanReclaim();
                    var started = await mcpServer.StartAsync();
                    if (started)
                    {
                        ClearPendingRestart();
                        ClearScheduledRestart();
                    }
                    else if (RestartDeadlineExpired())
                    {
                        Debug.LogError("[KitWright MCP Server] Failed to restart after domain reload.");
                        ClearPendingRestart();
                        ClearScheduledRestart();
                    }
                }
                else
                {
                    ClearPendingRestart();
                    ClearScheduledRestart();
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright MCP Server] Error restarting after reload: {ex.Message}");
            }
            finally
            {
                _restartInProgress = false;
            }
        }

        /// <summary>Restart gate of <see cref="RestartWhenEditorIsReady"/>, extracted so it is testable:
        /// a compile in flight means another domain reload is coming, so binding the port now would
        /// only orphan the listener.</summary>
        internal static bool ShouldWaitForCompilationBeforeRestart()
        {
            return CompilationService.IsActuallyCompiling;
        }

        private static bool RestartDeadlineExpired()
        {
            var deadlineText = SessionState.GetString(RestartDeadlineTicksKey, string.Empty);
            if (!long.TryParse(deadlineText, out var deadlineTicks))
            {
                SessionState.SetString(RestartDeadlineTicksKey, DateTime.UtcNow.Add(RestartRetryWindow).Ticks.ToString());
                return false;
            }

            return DateTime.UtcNow.Ticks >= deadlineTicks;
        }

        private static void ClearPendingRestart()
        {
            SessionState.EraseBool(WasRunningKey);
            SessionState.EraseInt(PortKey);
            SessionState.EraseString(RestartDeadlineTicksKey);
        }

        private static void ClearScheduledRestart()
        {
            EditorApplication.delayCall -= RestartWhenEditorIsReady;
            EditorApplication.update -= RestartWhenEditorIsReady;
            _restartScheduled = false;
        }
    }
}
