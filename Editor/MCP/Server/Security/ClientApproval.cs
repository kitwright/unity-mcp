// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.DI;
using KitWright.Editor.Settings;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.MCP.Server.Security
{
    /// <summary>
    /// First-connect approval for MCP clients. Loopback alone lets any local process call
    /// every tool (including execute_code and write_file); this gate identifies the client
    /// process behind the TCP connection and asks the user once per executable.
    /// </summary>
    [InitializeOnLoad]
    internal static class ClientApprovalGate
    {
        // Test seams.
        internal static Func<bool> RequireApprovalOverride;
        internal static Func<int, int, TcpClientProcessResolver.ClientProcessInfo> ResolverOverride;

        // Batch mode short-circuits before any of the logic below, so without a seam every test of
        // it is green locally and vacuous on CI.
        internal static bool? BatchModeOverride;

        private static readonly object s_lock = new object();

        // Refused clients, until this domain goes away. See the comment at the Deny branch below.
        private static readonly HashSet<string> s_deniedThisSession =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Clients allowed for this session only: what an unidentified client can be granted, since
        // its identity is a wildcard covering every process the resolver cannot name.
        private static readonly HashSet<string> s_allowedThisSession =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private const string UnidentifiedIdentity = "unidentified process";

        private static readonly Dictionary<string, Task<bool>> s_pendingPrompts =
            new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

        // Source ports already resolved, and executables already named, so the gate can stay quiet
        // without going silent. Both bounded: a long session would otherwise grow them forever.
        private static readonly HashSet<int> s_notedPorts = new HashSet<int>();
        private static readonly HashSet<string> s_notedIdentities =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const int NotedPortCap = 512;

        private static SynchronizationContext s_mainContext;
        private static readonly bool s_isBatchMode;
        private static readonly int s_editorPid;

        static ClientApprovalGate()
        {
            s_isBatchMode = Application.isBatchMode;
            s_editorPid = Process.GetCurrentProcess().Id;

            // Captured here rather than inline: [InitializeOnLoad] runs before Unity installs the
            // main-thread context, so Current is still null at this point.
            EditorApplication.delayCall += () => s_mainContext = SynchronizationContext.Current;
        }

        public static Task<bool> AuthorizeAsync(TcpClient client, int serverPort)
        {
            int clientPort;
            try
            {
                clientPort = ((IPEndPoint)client.Client.RemoteEndPoint).Port;
            }
            catch
            {
                clientPort = 0;
            }

            return AuthorizeAsync(clientPort, serverPort);
        }

        /// <summary>
        /// Broker mode overload: the broker forwards the client's TCP port, so the owning
        /// process is resolved here instead of from a socket this process owns.
        /// </summary>
        public static Task<bool> AuthorizeAsync(int clientPort, int serverPort)
        {
            if (BatchModeOverride ?? s_isBatchMode)
                return Task.FromResult(true);

            if (!RequireApproval())
            {
                NoteClient(clientPort, serverPort);
                return Task.FromResult(true);
            }

            TcpClientProcessResolver.ClientProcessInfo info;
            try
            {
                info = (ResolverOverride ?? TcpClientProcessResolver.Resolve)(clientPort, serverPort);
            }
            catch
            {
                info = null;
            }

            var decided = Decide(info, out var identity);
            if (decided.HasValue)
                return Task.FromResult(decided.Value);

            return PromptAsync(identity, info);
        }

        /// <summary>
        /// true = allow, false = already denied by the user, null = ask. Denials short-circuit
        /// here so a retrying client cannot re-open the modal dialog and wedge the editor loop.
        /// </summary>
        internal static bool? Decide(TcpClientProcessResolver.ClientProcessInfo info, out string identity)
        {
            if (IsPreApproved(info, out identity))
                return true;

            lock (s_lock)
                return s_deniedThisSession.Contains(identity) ? (bool?)false : null;
        }

        // The gate ships off, so with approval disabled nothing named the process talking to this
        // editor: the interaction log records tool calls without an identity. Resolving costs a scan
        // of the machine's whole TCP table, so it runs once per source port and logs once per
        // executable rather than once per request.
        private static void NoteClient(int clientPort, int serverPort)
        {
            lock (s_lock)
            {
                if (!s_notedPorts.Add(clientPort))
                    return;
                if (s_notedPorts.Count > NotedPortCap)
                    s_notedPorts.Clear();
            }

            TcpClientProcessResolver.ClientProcessInfo info;
            try
            {
                info = (ResolverOverride ?? TcpClientProcessResolver.Resolve)(clientPort, serverPort);
            }
            catch
            {
                return;
            }

            if (!IsIdentified(info) || info.Pid == s_editorPid)
                return;

            var identity = string.IsNullOrEmpty(info.ExecutablePath) ? info.ProcessName : info.ExecutablePath;
            lock (s_lock)
            {
                if (!s_notedIdentities.Add(identity))
                    return;
            }

            UnityEngine.Debug.Log($"[KitWright MCP Server] Client connected: {identity} (pid {info.Pid}). " +
                                  "Client approval is off; turn it on in the Safety tab to be asked first.");
        }

        internal static void DenyThisSession(string identity)
        {
            lock (s_lock)
                s_deniedThisSession.Add(identity);
        }

        internal static void AllowThisSession(string identity)
        {
            lock (s_lock)
                s_allowedThisSession.Add(identity);
        }

        internal static void ClearSessionDenials()
        {
            lock (s_lock)
                s_deniedThisSession.Clear();
        }

        internal static void ClearSessionAllowances()
        {
            lock (s_lock)
                s_allowedThisSession.Clear();
        }

        internal static void ClearNotedClients()
        {
            lock (s_lock)
            {
                s_notedPorts.Clear();
                s_notedIdentities.Clear();
            }
        }

        internal static bool IsIdentified(TcpClientProcessResolver.ClientProcessInfo info)
        {
            return info != null &&
                   !(string.IsNullOrEmpty(info.ExecutablePath) && string.IsNullOrEmpty(info.ProcessName));
        }

        internal static bool IsPreApproved(TcpClientProcessResolver.ClientProcessInfo info, out string identity)
        {
            identity = IsIdentified(info)
                ? (string.IsNullOrEmpty(info.ExecutablePath) ? info.ProcessName : info.ExecutablePath)
                : UnidentifiedIdentity;

            // The editor calling its own server (in-editor tests, broker pull/push) needs no prompt.
            if (info != null && info.Pid == s_editorPid)
                return true;

            // The stdio broker is spawned by this package with the mono path from settings.
            if (IsConfiguredBrokerPath(identity) || ClientApprovalStore.IsApproved(identity))
                return true;

            lock (s_lock)
                return s_allowedThisSession.Contains(identity);
        }

        private static Task<bool> PromptAsync(string identity, TcpClientProcessResolver.ClientProcessInfo info)
        {
            lock (s_lock)
            {
                if (s_pendingPrompts.TryGetValue(identity, out var pending))
                    return pending;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                s_pendingPrompts[identity] = tcs.Task;

                if (s_mainContext == null)
                {
                    UnityEngine.Debug.LogWarning($"[KitWright MCP Server] Refused \"{identity}\" without asking: " +
                                                 "no main-thread context yet, so no approval dialog could be shown.");
                    Finish(identity, tcs, false);
                    return tcs.Task;
                }

                // ponytail: modal DisplayDialog blocks the editor loop until clicked; upgrade to a
                // non-modal approval window with a timeout if unattended connects become common.
                s_mainContext.Post(_ =>
                {
                    bool? approved;
                    var identified = IsIdentified(info);
                    try
                    {
                        // An unidentified client gets no permanent choice: that identity is every
                        // process the resolver cannot name, so remembering it would hand the whole
                        // blanket away for good.
                        approved = EditorUtility.DisplayDialog(
                            identified ? "KitWright MCP: new client" : "KitWright MCP: unidentified client",
                            identified
                                ? $"\"{info.ProcessName ?? identity}\" is connecting to this project's MCP server." +
                                  $"\n\n{identity}\n\nAllow it to call Unity editor tools? This is remembered for all projects."
                                : "A local process is connecting to this project's MCP server, but its executable " +
                                  "could not be identified.\n\nAllowing covers every unidentified process until this " +
                                  "editor session ends, and is never remembered. If you did not start a tool that " +
                                  "drives Unity, deny this.",
                            identified ? "Allow" : "Allow this session",
                            "Deny");
                    }
                    catch
                    {
                        // No answer from the user: refuse this attempt without remembering it.
                        approved = null;
                    }

                    if (approved == true)
                    {
                        if (identified)
                            ClientApprovalStore.Approve(identity);
                        else
                            AllowThisSession(identity);
                    }
                    else if (approved == false)
                        // Session-scoped on purpose: DisplayDialog returns false for Deny, Escape AND
                        // the window's X, and on non-Windows every client collapses to one identity,
                        // so persisting this would let a stray Escape lock the user out with no UI to
                        // clear it. Stopping the modal storm inside the session is what this is for.
                        lock (s_lock)
                            s_deniedThisSession.Add(identity);
                    Finish(identity, tcs, approved == true);
                }, null);

                return tcs.Task;
            }
        }

        private static void Finish(string identity, TaskCompletionSource<bool> tcs, bool approved)
        {
            lock (s_lock)
                s_pendingPrompts.Remove(identity);
            tcs.TrySetResult(approved);
        }

        private static bool IsConfiguredBrokerPath(string identity)
        {
            var settings = RootScopeServices.Services?.GetService(typeof(SettingsController)) as SettingsController;
            var brokerPath = settings?.MCPBrokerMonoPath;
            return !string.IsNullOrEmpty(brokerPath) &&
                   string.Equals(identity, brokerPath, StringComparison.OrdinalIgnoreCase);
        }

        private static bool RequireApproval()
        {
            if (RequireApprovalOverride != null)
                return RequireApprovalOverride();

            return RequireApprovalFrom(
                RootScopeServices.Services?.GetService(typeof(SettingsController)) as SettingsController);
        }

        /// <summary>
        /// Falls back to the shipped default rather than to "on": autostart plus broker mode means
        /// requests arrive while the domain is still reloading and this service is not resolvable
        /// yet, and defaulting to on there prompted users who had the gate switched off.
        /// </summary>
        internal static bool RequireApprovalFrom(SettingsController settings)
        {
            return settings?.RequireClientApprovalEnabled
                   ?? SettingsController.DefaultRequireClientApprovalEnabled;
        }
    }

    /// <summary>
    /// Per-user list of client executables approved to call this MCP server. Stored beside the
    /// instance registry so one answer covers every project. Refusals are deliberately NOT stored
    /// here - see the Deny branch in ClientApprovalGate.
    /// </summary>
    internal static class ClientApprovalStore
    {
        // Test seam: point the store at a temp directory instead of the user profile.
        internal static string RootOverride;

        private static readonly object s_lock = new object();

        // Windows paths are case-insensitive; identities are exe paths.
        private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

        public static bool IsApproved(string identity) => Contains(FilePath("approved-clients.json"), identity);

        public static void Approve(string identity) => Add(FilePath("approved-clients.json"), identity);

        private static bool Contains(string filePath, string identity)
        {
            if (string.IsNullOrEmpty(identity))
                return false;

            lock (s_lock)
                return Load(filePath).Contains(identity, PathComparer);
        }

        private static void Add(string filePath, string identity)
        {
            if (string.IsNullOrEmpty(identity))
                return;

            lock (s_lock)
            {
                var entries = Load(filePath);
                if (entries.Contains(identity, PathComparer))
                    return;

                entries.Add(identity);
                Save(filePath, entries);
            }
        }

        private static string FilePath(string fileName) =>
            Path.Combine(
                RootOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KitWright"),
                fileName);

        private static List<string> Load(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return new List<string>();

                return JsonConvert.DeserializeObject<List<string>>(File.ReadAllText(filePath)) ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static void Save(string filePath, List<string> entries)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, JsonConvert.SerializeObject(entries, Formatting.Indented));
            }
            catch (Exception ex)
            {
                PluginDebugLogger.Log($"[KitWright] Could not save client approvals: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Resolves which local process owns the client side of a loopback TCP connection,
    /// via GetExtendedTcpTable. Windows-only; other platforms return null and the caller
    /// treats the client as unidentified.
    /// </summary>
    internal static class TcpClientProcessResolver
    {
        internal sealed class ClientProcessInfo
        {
            public int Pid;
            public string ExecutablePath;
            public string ProcessName;
        }

        public static ClientProcessInfo Resolve(int clientPort, int serverPort)
        {
#if UNITY_EDITOR_WIN
            try
            {
                var pid = FindOwningPid(clientPort, serverPort);
                if (pid <= 0)
                    return null;

                var info = new ClientProcessInfo { Pid = pid };
                try
                {
                    using (var process = Process.GetProcessById(pid))
                    {
                        info.ProcessName = process.ProcessName;
                        // MainModule throws for elevated/protected processes; name alone still identifies.
                        try { info.ExecutablePath = process.MainModule?.FileName; }
                        catch { }
                    }
                }
                catch
                {
                    return null;
                }
                return info;
            }
            catch
            {
                return null;
            }
#else
            return null;
#endif
        }

        // Row ports are the low 16 bits in network byte order.
        internal static int DecodePort(uint rawPort)
        {
            return (int)(((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF));
        }

#if UNITY_EDITOR_WIN
        private const int AF_INET = 2;
        private const int TCP_TABLE_OWNER_PID_CONNECTIONS = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tableClass, uint reserved);

        private static int FindOwningPid(int clientPort, int serverPort)
        {
            int bufferSize = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0);
            if (bufferSize <= 0)
                return -1;

            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (GetExtendedTcpTable(buffer, ref bufferSize, false, AF_INET, TCP_TABLE_OWNER_PID_CONNECTIONS, 0) != 0)
                    return -1;

                int rowCount = Marshal.ReadInt32(buffer);
                var rowPtr = buffer + 4;
                var rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();

                for (int i = 0; i < rowCount; i++)
                {
                    var row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr + i * rowSize);
                    if (DecodePort(row.localPort) == clientPort && DecodePort(row.remotePort) == serverPort)
                        return (int)row.owningPid;
                }
                return -1;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
#endif
    }
}
