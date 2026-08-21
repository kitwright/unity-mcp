// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using KitWright.Editor.Services;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KitWright.Editor.MCP.Server
{
    [InitializeOnLoad]
    internal static class MCPBrokerProcessManager
    {
        private const int StartProbeAttempts = 40;
        private const int StartProbeDelayMs = 125;
        private static readonly object Gate = new object();
        private static readonly string ProjectRoot;
        private static readonly string RuntimeDirectory;
        private static MCPBrokerRuntimePaths _defaultPaths;

        // The asset database cannot be queried from the static constructor, so the broker
        // source path is resolved on first real use and cached only once it is found.
        private static MCPBrokerRuntimePaths DefaultPaths
        {
            get
            {
                if (_defaultPaths == null || string.IsNullOrEmpty(_defaultPaths.SourcePath))
                {
                    _defaultPaths = new MCPBrokerRuntimePaths(
                        Path.Combine(RuntimeDirectory, "broker.pid"),
                        RuntimeDirectory,
                        ResolveBrokerSourcePath(ProjectRoot));
                }

                return _defaultPaths;
            }
        }

        public static string LastError { get; private set; }

        static MCPBrokerProcessManager()
        {
            ProjectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? Directory.GetCurrentDirectory();
            RuntimeDirectory = Path.Combine(ProjectRoot, "Library", "KitWrightMcp", "Broker");

            EditorApplication.quitting += StopOnQuit;
        }

        public static Task<bool> EnsureRunningAsync(int port, string monoPathOverride)
        {
            return EnsureRunningAsync(port, monoPathOverride, DefaultPaths);
        }

        /// <summary>
        /// Spawns the broker if needed, then waits for its health probe off the editor thread.
        /// The wait is the slow part (a cold broker takes up to <see cref="StartProbeAttempts"/> x
        /// <see cref="StartProbeDelayMs"/> ms), so it must not run as a blocking sleep -- that
        /// freezes the editor UI, including the Connect button's own progress indicator.
        /// </summary>
        internal static async Task<bool> EnsureRunningAsync(
            int port, string monoPathOverride, MCPBrokerRuntimePaths paths)
        {
            var outcome = SpawnBroker(port, monoPathOverride, paths, out var pid, out var token);
            if (outcome != BrokerSpawn.Spawned)
                return outcome == BrokerSpawn.AlreadyRunning;

            for (var attempt = 0; attempt < StartProbeAttempts; attempt++)
            {
                if (TryProbeBroker(port, token, out var health) && health.Pid == pid)
                {
                    Debug.Log("[KitWright MCP Server] Broker started (pid=" + pid + ", port=" + port + ").");
                    return true;
                }

                await Task.Delay(StartProbeDelayMs).ConfigureAwait(false);
            }

            LastError = "Broker process started but did not pass health checks.";
            Debug.LogWarning("[KitWright MCP Server] " + LastError);
            lock (Gate)
            {
                KillVerifiedProcess(pid);
                DeletePidFile(paths.PidFilePath);
            }
            return false;
        }

        /// <summary>Synchronous wrapper for callers that cannot await (tests, shutdown paths).</summary>
        public static bool EnsureRunning(int port, string monoPathOverride)
        {
            return EnsureRunning(port, monoPathOverride, DefaultPaths);
        }

        internal static bool EnsureRunning(int port, string monoPathOverride, MCPBrokerRuntimePaths paths)
        {
            return EnsureRunningAsync(port, monoPathOverride, paths).GetAwaiter().GetResult();
        }

        private enum BrokerSpawn { AlreadyRunning, Spawned, Failed }

        // Everything here needs the editor thread (AssetDatabase lookups when preparing the
        // broker exe) and the process-wide gate, but all of it is fast.
        private static BrokerSpawn SpawnBroker(
            int port, string monoPathOverride, MCPBrokerRuntimePaths paths, out int pid, out string token)
        {
            pid = 0;
            token = null;

            lock (Gate)
            {
                LastError = null;

                if (TryReadState(paths.PidFilePath, out var existing))
                {
                    if (existing.Port == port &&
                        TryProbeBroker(existing.Port, existing.Token, out var health) &&
                        health.Pid == existing.Pid)
                    {
                        return BrokerSpawn.AlreadyRunning;
                    }

                    // The pid file points at a broker we previously started, either on this
                    // port (but it no longer passes the health probe -- typically a
                    // protocol-version mismatch after a package upgrade) or on a different
                    // port (the Server Port setting changed). Either way it's ours: shut it
                    // down with its recorded token so its port frees up, instead of leaving
                    // it orphaned and squatting on that port forever.
                    if (IsTcpPortOpen(existing.Port))
                    {
                        var shutdownAccepted = SendShutdown(existing.Port, existing.Token);
                        WaitForExit(existing.Pid, 2500);
                        if (shutdownAccepted)
                            KillVerifiedProcess(existing.Pid);
                    }

                    DeletePidFile(paths.PidFilePath);
                }

                if (IsTcpPortOpen(port))
                {
                    LastError = existing != null && existing.Port == port
                        ? "Port is already in use, but it is not a verified KitWright broker."
                        : "Port is already in use by another process.";
                    return BrokerSpawn.Failed;
                }

                var mono = ResolveMono(monoPathOverride);
                if (string.IsNullOrEmpty(mono))
                {
                    LastError = "Unity-bundled Mono runtime was not found.";
                    Debug.LogWarning("[KitWright MCP Server] " + LastError);
                    return BrokerSpawn.Failed;
                }

                var brokerExe = EnsureBrokerExe(paths, mono);
                if (string.IsNullOrEmpty(brokerExe))
                {
                    LastError = LastError ?? "Broker executable could not be prepared.";
                    Debug.LogWarning("[KitWright MCP Server] " + LastError);
                    return BrokerSpawn.Failed;
                }

                var spawnToken = Guid.NewGuid().ToString("N");
                Process process;
                try
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = mono,
                        Arguments = BuildSpawnArguments(brokerExe, port, spawnToken,
                            ProjectIdentity.PinFromProjectPath(ApplicationPaths.ProjectRoot)),
                        WorkingDirectory = Path.GetDirectoryName(brokerExe),
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    process = Process.Start(startInfo);
                    if (process == null)
                    {
                        LastError = "Failed to start broker process.";
                        return BrokerSpawn.Failed;
                    }
                }
                catch (Exception ex)
                {
                    LastError = "Failed to start broker process: " + ex.Message;
                    Debug.LogError("[KitWright MCP Server] " + LastError);
                    return BrokerSpawn.Failed;
                }

                var state = new BrokerProcessState
                {
                    Pid = process.Id,
                    Port = port,
                    Token = spawnToken,
                    Protocol = MCPBrokerProtocol.Version
                };
                WriteState(paths.PidFilePath, state);

                pid = process.Id;
                token = spawnToken;
                return BrokerSpawn.Spawned;
            }
        }

        public static bool IsRunning(out int pid, out int port)
        {
            return IsRunning(DefaultPaths, out pid, out port);
        }

        public static bool TryGetConnectionInfo(int expectedPort, out BrokerConnectionInfo connection)
        {
            return TryGetConnectionInfo(DefaultPaths, expectedPort, out connection);
        }

        internal static bool TryGetConnectionInfo(MCPBrokerRuntimePaths paths, int expectedPort, out BrokerConnectionInfo connection)
        {
            connection = null;
            if (!TryReadState(paths.PidFilePath, out var state) || state.Port != expectedPort)
                return false;

            if (!TryProbeBroker(state.Port, state.Token, out var health) || health.Pid != state.Pid)
                return false;

            connection = new BrokerConnectionInfo
            {
                Pid = state.Pid,
                Port = state.Port,
                Token = state.Token
            };
            return true;
        }

        internal static bool IsRunning(MCPBrokerRuntimePaths paths, out int pid, out int port)
        {
            pid = 0;
            port = 0;

            if (!TryReadState(paths.PidFilePath, out var state))
                return false;

            if (!TryProbeBroker(state.Port, state.Token, out var health) || health.Pid != state.Pid)
                return false;

            pid = state.Pid;
            port = state.Port;
            return true;
        }

        public static void Stop()
        {
            Stop(DefaultPaths);
        }

        // A -batchmode editor (our own CI runs -batchmode -runTests) shares the broker with any
        // interactive editor on the same project, so its exit must not take the broker down.
        internal static bool ShouldStopOnQuit(bool isBatchMode) => !isBatchMode;

        private static void StopOnQuit()
        {
            if (ShouldStopOnQuit(Application.isBatchMode))
                Stop();
        }

        internal static void Stop(MCPBrokerRuntimePaths paths)
        {
            lock (Gate)
            {
                if (!TryReadState(paths.PidFilePath, out var state))
                    return;

                var verified = TryProbeBroker(state.Port, state.Token, out var health) && health.Pid == state.Pid;
                if (verified)
                {
                    SendShutdown(state.Port, state.Token);
                    WaitForExit(state.Pid, 2500);
                    KillVerifiedProcess(state.Pid);
                }

                DeletePidFile(paths.PidFilePath);
            }
        }

        internal static bool TryProbeBroker(int port, string token, out BrokerHealth health)
        {
            health = null;
            if (port <= 0 || string.IsNullOrEmpty(token))
                return false;

            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + MCPBrokerProtocol.HealthPath);
                request.Method = "GET";
                request.Timeout = 500;
                request.ReadWriteTimeout = 500;
                request.KeepAlive = false;
                request.Headers[MCPBrokerProtocol.TokenHeader] = token;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null))
                {
                    if (response.StatusCode != HttpStatusCode.OK)
                        return false;

                    if (!string.Equals(response.Headers[MCPBrokerProtocol.BrokerHeader],
                            MCPBrokerProtocol.Version.ToString(), StringComparison.Ordinal))
                        return false;

                    var json = reader.ReadToEnd();
                    var dict = JsonCodec.Deserialize(json) as Dictionary<string, object>;
                    if (dict == null)
                        return false;

                    health = new BrokerHealth
                    {
                        Name = GetString(dict, "name"),
                        Pid = GetInt(dict, "pid"),
                        Protocol = GetInt(dict, "protocol"),
                        Pending = GetInt(dict, "pending")
                    };

                    return string.Equals(health.Name, MCPBrokerProtocol.Name, StringComparison.Ordinal) &&
                           health.Protocol == MCPBrokerProtocol.Version &&
                           health.Pid > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        internal static string ResolveMono(string overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
                return overridePath;

            var exe = Application.platform == RuntimePlatform.WindowsEditor ? "mono.exe" : "mono";
            var contents = EditorApplication.applicationContentsPath ?? string.Empty;
            var candidates = new[]
            {
                Path.Combine(contents, "MonoBleedingEdge", "bin", exe),
                Path.Combine(contents, "Resources", "Scripting", "MonoBleedingEdge", "bin", exe),
                Path.Combine(contents, "Data", "MonoBleedingEdge", "bin", exe)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            try
            {
                if (Directory.Exists(contents))
                {
                    foreach (var dir in Directory.GetDirectories(contents, "MonoBleedingEdge", SearchOption.AllDirectories))
                    {
                        var candidate = Path.Combine(dir, "bin", exe);
                        if (File.Exists(candidate))
                            return candidate;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static string EnsureBrokerExe(MCPBrokerRuntimePaths paths, string mono)
        {
            try
            {
                if (string.IsNullOrEmpty(paths.SourcePath) || !File.Exists(paths.SourcePath))
                {
                    LastError = "Broker source file is missing from the package.";
                    return null;
                }

                Directory.CreateDirectory(paths.CacheDirectory);
                var cacheSource = Path.Combine(paths.CacheDirectory, "keepalive-broker.cs");
                var cacheExe = Path.Combine(paths.CacheDirectory, "keepalive-broker.exe");

                if (File.Exists(cacheExe) &&
                    File.GetLastWriteTimeUtc(cacheExe) >= File.GetLastWriteTimeUtc(paths.SourcePath))
                {
                    return cacheExe;
                }

                File.Copy(paths.SourcePath, cacheSource, true);

                var compiler = ResolveCompiler(mono);
                if (string.IsNullOrEmpty(compiler))
                {
                    LastError = "Unity-bundled C# compiler was not found.";
                    return null;
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = mono,
                    Arguments = Quote(compiler) + " -nologo -target:exe -out:" + Quote(cacheExe) + " " + Quote(cacheSource),
                    WorkingDirectory = paths.CacheDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        LastError = "Failed to start Unity-bundled C# compiler.";
                        return null;
                    }

                    // Drained concurrently: a compiler that filled the stderr buffer blocked, so
                    // stdout never hit EOF and the sequential read hung past the timeout below.
                    var stdoutRead = process.StandardOutput.ReadToEndAsync();
                    var stderrRead = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(20000))
                    {
                        try { process.Kill(); } catch { }
                        LastError = "Broker compile timed out.";
                        return null;
                    }

                    // Bounded: EOF needs every writer to close, and a diagnostic is not worth
                    // blocking the editor for if one does not.
                    try { Task.WaitAll(new Task[] { stdoutRead, stderrRead }, 2000); } catch { }
                    var stdout = stdoutRead.Status == TaskStatus.RanToCompletion ? stdoutRead.Result : string.Empty;
                    var stderr = stderrRead.Status == TaskStatus.RanToCompletion ? stderrRead.Result : string.Empty;

                    if (process.ExitCode != 0 || !File.Exists(cacheExe))
                    {
                        LastError = "Broker compile failed: " + (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
                        return null;
                    }
                }

                return cacheExe;
            }
            catch (Exception ex)
            {
                LastError = "Broker compile failed: " + ex.Message;
                return null;
            }
        }

        private static string ResolveCompiler(string mono)
        {
            var monoBin = Path.GetDirectoryName(mono);
            if (string.IsNullOrEmpty(monoBin))
                return null;

            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(monoBin, "..", "lib", "mono", "4.5", "mcs.exe")),
                Path.GetFullPath(Path.Combine(monoBin, "..", "lib", "mono", "4.5", "csc.exe")),
                Path.GetFullPath(Path.Combine(monoBin, "..", "lib", "mono", "msbuild", "Current", "bin", "Roslyn", "csc.exe"))
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static string ResolveBrokerSourcePath(string projectRoot)
        {
            try
            {
                var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(MCPBrokerProcessManager).Assembly);
                if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                {
                    var path = Path.Combine(package.resolvedPath, "Editor", "MCP", "Server", "Broker", "keepalive-broker.cs.txt");
                    if (File.Exists(path))
                        return path;
                }
            }
            catch
            {
            }

            // Installed from the Asset Store the package lives under Assets/ at a path
            // the buyer can rename or move, so PackageInfo returns null and guessing
            // folder names cannot work. Ask the asset database where the file actually is.
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("keepalive-broker"))
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (assetPath.EndsWith("/keepalive-broker.cs.txt", StringComparison.Ordinal))
                        return Path.GetFullPath(assetPath);
                }
            }
            catch
            {
            }

            var candidates = new[]
            {
                Path.Combine(Application.dataPath, "unity-mcp", "Editor", "MCP", "Server", "Broker", "keepalive-broker.cs.txt"),
                Path.Combine(projectRoot, "Packages", "com.kitwright.unity.mcp", "Editor", "MCP", "Server", "Broker", "keepalive-broker.cs.txt")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static bool SendShutdown(int port, string token)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + MCPBrokerProtocol.ShutdownPath);
                request.Method = "POST";
                request.Timeout = 1000;
                request.ReadWriteTimeout = 1000;
                request.ContentLength = 0;
                request.KeepAlive = false;
                request.Headers[MCPBrokerProtocol.TokenHeader] = token;
                using (var response = (HttpWebResponse)request.GetResponse())
                    return response.StatusCode == HttpStatusCode.OK;
            }
            catch
            {
                return false;
            }
        }

        private static void WriteState(string pidFilePath, BrokerProcessState state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(pidFilePath));
            File.WriteAllText(pidFilePath,
                state.Pid + "\n" +
                state.Port + "\n" +
                state.Token + "\n" +
                state.Protocol + "\n");
        }

        private static bool TryReadState(string pidFilePath, out BrokerProcessState state)
        {
            state = null;
            try
            {
                if (!File.Exists(pidFilePath))
                    return false;

                var lines = File.ReadAllLines(pidFilePath);
                if (lines.Length < 4)
                    return false;

                if (!int.TryParse(lines[0], out var pid) ||
                    !int.TryParse(lines[1], out var port) ||
                    !int.TryParse(lines[3], out var protocol))
                {
                    return false;
                }

                state = new BrokerProcessState
                {
                    Pid = pid,
                    Port = port,
                    Token = lines[2],
                    Protocol = protocol
                };

                // Keep stale protocol records readable so upgrades can shut down a
                // previously started broker with its recorded token before starting
                // the new protocol version.
                return state.Pid > 0 &&
                       state.Port > 0 &&
                       !string.IsNullOrEmpty(state.Token);
            }
            catch
            {
                return false;
            }
        }

        private static void DeletePidFile(string pidFilePath)
        {
            try
            {
                if (File.Exists(pidFilePath))
                    File.Delete(pidFilePath);
            }
            catch
            {
            }
        }

        private static bool IsTcpPortOpen(int port)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var result = client.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!result.AsyncWaitHandle.WaitOne(250))
                        return false;

                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static void WaitForExit(int pid, int timeoutMs)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                    process.WaitForExit(timeoutMs);
            }
            catch
            {
            }
        }

        private static void KillVerifiedProcess(int pid)
        {
            try
            {
                var process = Process.GetProcessById(pid);
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch
            {
            }
        }

        // The broker reads its protocol version from here rather than declaring its own, so a bump
        // touches one file. Dropping the argument would make it answer 0 and fail every health
        // probe, which is the same silent fallback-to-HTTP a mismatched constant used to cause.
        internal static string BuildSpawnArguments(string brokerExe, int port, string token, string pin)
        {
            return Quote(brokerExe) + " --port " + port + " --token " + token +
                   " --pin " + pin + " --protocol " + MCPBrokerProtocol.Version;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static string GetString(Dictionary<string, object> dict, string key)
        {
            return dict.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty;
        }

        private static int GetInt(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var value) || value == null)
                return 0;

            if (value is int intValue)
                return intValue;

            if (value is long longValue)
                return (int)longValue;

            if (value is double doubleValue)
                return (int)doubleValue;

            if (int.TryParse(value.ToString(), out var parsed))
                return parsed;

            return 0;
        }

        internal sealed class BrokerHealth
        {
            public string Name;
            public int Pid;
            public int Protocol;
            public int Pending;
        }

        internal sealed class BrokerConnectionInfo
        {
            public int Pid;
            public int Port;
            public string Token;
        }

        internal sealed class MCPBrokerRuntimePaths
        {
            public MCPBrokerRuntimePaths(string pidFilePath, string cacheDirectory, string sourcePath)
            {
                PidFilePath = pidFilePath;
                CacheDirectory = cacheDirectory;
                SourcePath = sourcePath;
            }

            public string PidFilePath { get; }
            public string CacheDirectory { get; }
            public string SourcePath { get; }
        }

        private sealed class BrokerProcessState
        {
            public int Pid;
            public int Port;
            public string Token;
            public int Protocol;
        }
    }
}
