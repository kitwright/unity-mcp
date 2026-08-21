// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Api.Models;
using KitWright.Editor.Settings;
using UnityEngine;

namespace KitWright.Editor.Tools
{
    /// <summary>
    /// Scans assemblies for classes marked with [ToolProvider]
    /// and discovers all public static methods as tool functions.
        /// Blocked tools (e.g. hidden evaluation helpers, input simulation) are filtered out.
    /// Also supports manual tool registration for external plugins.
    /// </summary>
    internal static class ToolRegistry
    {
        private static readonly object _lock = new object();
        private static volatile Dictionary<string, MethodInfo> _methodCache;
        private static volatile HashSet<string> _customToolNames;

        /// <summary>
        /// Manually registered tools from external plugins.
        /// Key = snake_case tool name.
        /// </summary>
        private static readonly Dictionary<string, ManualToolEntry> _manualTools =
            new Dictionary<string, ManualToolEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> BlockedTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "evaluate_expression",
        };

        /// <summary>
        /// Represents a manually registered tool function.
        /// </summary>
        internal class ManualToolEntry
        {
            public ToolDefinition Definition;
            public Func<Dictionary<string, string>, string> Handler;
        }

        public static IReadOnlyDictionary<string, MethodInfo> MethodCache
        {
            get
            {
                if (_methodCache == null)
                    lock (_lock) { if (_methodCache == null) ScanAssemblies(); }
                return _methodCache;
            }
        }

        public static void ScanAssemblies()
        {
            ScanAssemblies(AppDomain.CurrentDomain.GetAssemblies());
        }

        internal static void ScanAssemblies(IEnumerable<Assembly> assemblies)
        {
            var methodCache = new Dictionary<string, MethodInfo>(StringComparer.OrdinalIgnoreCase);
            var customToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var assembly in assemblies)
                {
                    if (assembly.IsDynamic) continue;

                    try
                    {
                        foreach (var type in assembly.GetTypes())
                        {
                            try
                            {
                                if (type.GetCustomAttribute<ToolProviderAttribute>() == null)
                                    continue;

                                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                                foreach (var method in methods)
                                {
                                    var snakeName = ToSnakeCase(method.Name);

                                    if (BlockedTools.Contains(snakeName))
                                        continue;

                                    if (!methodCache.ContainsKey(snakeName))
                                    {
                                        methodCache[snakeName] = method;
                                        if (!IsPackageAssembly(assembly))
                                            customToolNames.Add(snakeName);
                                    }
                                    else
                                    {
                                        Debug.LogWarning($"[KitWright] Duplicate tool function name: {snakeName}");
                                    }
                                }
                            }
                            catch (Exception)
                            {
                                // One unloadable type must not cost us the rest of the assembly
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Skip assemblies that can't be loaded
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright] Error scanning assemblies for tool functions: {ex.Message}");
            }

            _methodCache = methodCache;
            _customToolNames = customToolNames;
        }

        /// <summary>
        /// True when the tool was declared outside this package — i.e. by project or
        /// third-party code marked with [ToolProvider]. Those tools are exposed regardless of
        /// the active profile, since the project author opted in by writing them.
        /// </summary>
        public static bool IsCustomTool(string snakeCaseName)
        {
            if (string.IsNullOrEmpty(snakeCaseName)) return false;
            if (_customToolNames == null)
                lock (_lock) { if (_customToolNames == null) ScanAssemblies(); }
            return _customToolNames.Contains(snakeCaseName);
        }

        // Listed explicitly rather than matched on a "KitWright." prefix, so a project that happens
        // to name an assembly KitWright.Something does not get its tools silently treated as built-in.
        private static readonly HashSet<string> PackageAssemblyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "KitWright.Editor",
            "KitWright.Editor.Bootstrap",
            "KitWright.Editor.InputSystem",
            "KitWright.Editor.Pro"
        };

        internal static bool IsPackageAssembly(Assembly assembly)
        {
            var name = assembly?.GetName().Name;
            return name != null && PackageAssemblyNames.Contains(name);
        }

        public static MethodInfo GetMethod(string snakeCaseName)
        {
            if (_methodCache == null) ScanAssemblies();
            _methodCache.TryGetValue(snakeCaseName, out var method);
            return method;
        }

        public static bool IsReadOnly(MethodInfo method)
        {
            return method.GetCustomAttribute<ReadOnlyToolAttribute>() != null;
        }

        /// <summary>Default per-request budget, matching the plain HTTP transport's historical cap.</summary>
        public const int DefaultToolTimeoutSeconds = 180;

        /// <summary>
        /// Seconds a transport should wait for a JSON-RPC request before calling it a timeout:
        /// tools/call gets its tool's budget, everything else the fallback. A build or a bake keeps
        /// running on the pinned editor thread after the transport gives up, so answering
        /// "Request timeout" makes the agent retry and queue a second one behind the first.
        /// </summary>
        public static int TimeoutSecondsForRequest(
            string rpcMethod, IDictionary<string, object> parameters, int fallback = DefaultToolTimeoutSeconds)
        {
            if (!string.Equals(rpcMethod, "tools/call", StringComparison.Ordinal))
                return fallback;

            object name = null;
            parameters?.TryGetValue("name", out name);
            if (string.IsNullOrEmpty(name as string))
                return fallback;

            var budget = GetMethod((string)name)?.GetCustomAttribute<LongRunningToolAttribute>()?.Seconds ?? 0;
            return budget > fallback ? budget : fallback;
        }

        public static bool RunsOffEditorThread(string snakeCaseName)
        {
            if (string.IsNullOrEmpty(snakeCaseName))
                return false;

            var method = GetMethod(snakeCaseName);
            return method != null && method.IsDefined(typeof(OffEditorThreadAttribute), false);
        }

        // --- Public Registration API ---

        /// <summary>
        /// Manually register a tool function. External plugins can use this
        /// to add tools without using the [ToolProvider] attribute.
        /// </summary>
        /// <param name="name">Snake_case tool name (e.g. "my_custom_tool")</param>
        /// <param name="definition">Tool definition with JSON schema</param>
        /// <param name="handler">Function that receives parameters and returns a result string</param>
        public static void Register(string name, ToolDefinition definition,
            Func<Dictionary<string, string>, string> handler)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentNullException(nameof(name));
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (BlockedTools.Contains(name))
            {
                Debug.LogWarning($"[KitWright] Cannot register blocked tool: {name}");
                return;
            }

            lock (_lock)
            {
                _manualTools[name] = new ManualToolEntry
                {
                    Definition = definition,
                    Handler = handler
                };
            }

            PluginDebugLogger.Log($"[KitWright] Registered manual tool: {name}");
        }

        /// <summary>
        /// Unregister a manually registered tool.
        /// </summary>
        public static void Unregister(string name)
        {
            lock (_lock)
            {
                _manualTools.Remove(name);
            }
        }

        public static bool IsEnabled(string name)
        {
            return !BlockedTools.Contains(name);
        }

        /// <summary>
        /// Get all registered manual tools (for use by ToolSchemaBuilder and FunctionInvoker).
        /// </summary>
        public static IReadOnlyDictionary<string, ManualToolEntry> ManualTools => _manualTools;

        // --- Utility ---

        public static string ToSnakeCase(string pascalCase)
        {
            if (string.IsNullOrEmpty(pascalCase)) return pascalCase;

            var chars = new List<char>();
            for (int i = 0; i < pascalCase.Length; i++)
            {
                var c = pascalCase[i];
                if (char.IsUpper(c) && i > 0)
                {
                    // Add underscore before uppercase if previous char is lowercase
                    // or if next char is lowercase (handles "XMLParser" -> "xml_parser")
                    bool prevIsLower = char.IsLower(pascalCase[i - 1]);
                    bool nextIsLower = i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1]);
                    if (prevIsLower || nextIsLower)
                        chars.Add('_');
                }
                chars.Add(char.ToLowerInvariant(c));
            }
            return new string(chars.ToArray());
        }

    }
}
