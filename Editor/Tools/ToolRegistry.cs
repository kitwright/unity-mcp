// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Api.Models;
using KitWright.Editor.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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

        // These succeed in Play Mode, but the edit is thrown away when it ends; MCPExecutionBridge says so.
        internal static readonly HashSet<string> SceneEditingTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create_game_object",
            "create_primitive",
            "instantiate_prefab",
            "delete_game_object",
            "duplicate_game_object",
            "set_transform",
            "set_parent",
            "set_active",
            "rename_game_object",
            "add_component",
            "remove_component",
            "set_component_properties",
            "set_component_enabled",
            "set_tag_and_layer",
            "set_rect_transform",
            "create_canvas",
            "create_button",
            "create_text",
            "create_image",
            "create_ui_element",
            "add_audio_source",
            "add_audio_listener",
            "add_nav_mesh_agent",
            "add_nav_mesh_obstacle",
            "create_volume",
            "add_volume_override",
            "set_volume_override_property",
            "remove_volume_override",
            "set_camera_settings",
            "set_camera_projection",
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
            ScanAssemblies(AppDomain.CurrentDomain.GetAssemblies().Where(a => !IsTestAssembly(a)));
        }

        // A [ToolProvider] inside a test assembly is a fixture, not a product tool, and scanning it
        // would put it in every client's tools/list: the package's own tests compile in any project
        // that embeds the package, and a project's test asmdef is scanned like any other assembly.
        // The explicit-list overload does not filter, so a test can still scan its own fixture.
        internal static bool IsTestAssembly(Assembly assembly)
        {
            if (assembly == null) return false;

            try
            {
                return assembly.GetReferencedAssemblies()
                    .Any(reference => string.Equals(reference.Name, "nunit.framework", StringComparison.Ordinal));
            }
            catch (Exception)
            {
                return false;
            }
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
                        foreach (var type in LoadableTypes(assembly))
                        {
                            try
                            {
                                if (type.GetCustomAttribute<ToolProviderAttribute>() == null)
                                    continue;

                                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                                foreach (var method in methods)
                                {
                                    if (method.IsSpecialName)
                                        continue;

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
                                        Debug.LogWarning(DuplicateToolMessage(snakeName, methodCache[snakeName], method));
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

        // One type that fails to load must not cost the assembly's other tools. Only an assembly that can
        // hold tools warns: the scan walks every assembly in the domain, and many unrelated ones load partially.
        private static IEnumerable<Type> LoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = (ex.Types ?? Type.EmptyTypes).Where(type => type != null).ToList();
                if (CanDeclareTools(assembly))
                {
                    var cause = ex.LoaderExceptions?.FirstOrDefault(inner => inner != null)?.Message;
                    Debug.LogWarning($"[KitWright] Scanning '{assembly.GetName().Name}' for tools: some types failed to load " +
                                     $"and were skipped, {loaded.Count} loaded. {cause}");
                }

                return loaded;
            }
        }

        private static bool CanDeclareTools(Assembly assembly)
        {
            try
            {
                return IsPackageAssembly(assembly) ||
                       assembly.GetReferencedAssemblies().Any(reference => IsPackageAssembly(reference.Name));
            }
            catch (Exception)
            {
                return false;
            }
        }

        internal static string DuplicateToolMessage(string snakeName, MethodInfo kept, MethodInfo ignored) =>
            $"[KitWright] Duplicate tool function name '{snakeName}': keeping {DescribeTool(kept)}, ignoring {DescribeTool(ignored)}.";

        private static string DescribeTool(MethodInfo method) =>
            $"{method.DeclaringType?.FullName}.{method.Name} ({method.DeclaringType?.Assembly.GetName().Name})";

        /// <summary>
        /// True when the tool was declared outside this package — i.e. by project or
        /// third-party code marked with [ToolProvider], or registered at runtime with
        /// <see cref="Register"/>. Those tools are exposed regardless of the active profile,
        /// since the project author opted in by writing them.
        /// </summary>
        public static bool IsCustomTool(string snakeCaseName)
        {
            if (string.IsNullOrEmpty(snakeCaseName)) return false;
            if (IsManualTool(snakeCaseName)) return true;
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
            "KitWright.Editor.InputSystem"
        };

        internal static bool IsPackageAssembly(Assembly assembly)
        {
            return IsPackageAssembly(assembly?.GetName().Name);
        }

        // Add-on assemblies are deliberately absent from PackageAssemblyNames. A name listed there is
        // profile-filtered, and a tool that no default set names is then invisible under core - the
        // profile most clients connect with. Staying out of the list puts add-on tools in the custom
        // bucket, which CustomToolIsExposedUnderNonFullProfiles pins as always exposed.
        internal static bool IsPackageAssembly(string assemblyName)
        {
            return assemblyName != null && PackageAssemblyNames.Contains(assemblyName);
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

        public static bool IsReplaySafe(string snakeCaseName)
        {
            var method = string.IsNullOrEmpty(snakeCaseName) ? null : GetMethod(snakeCaseName);
            return method != null && method.GetCustomAttribute<ReplaySafeToolAttribute>() != null;
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

            if (string.Equals((string)name, BatchToolName, StringComparison.OrdinalIgnoreCase))
                return BatchBudgetSeconds(BatchCommandNames(parameters), fallback);

            if (string.Equals((string)name, ExecuteCodeToolName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals((string)name, ReplayExecuteCodeToolName, StringComparison.OrdinalIgnoreCase))
            {
                var requested = ExecuteCodeTimeoutSeconds(parameters);
                return requested > 0 ? Math.Max(requested + ExecuteCodeBudgetMarginSeconds, fallback) : fallback;
            }

            var budget = LongRunningSeconds((string)name);
            return budget > fallback ? budget : fallback;
        }

        internal const string BatchToolName = "batch_execute";

        // A batch holding build_player was cut off at the ordinary ceiling while the build ran on.
        // It gets the ordinary ceiling once, plus each long-running command's own budget, capped
        // below the broker's hold deadline so the editor still answers before the broker gives up.
        internal static int BatchBudgetSeconds(IEnumerable<string> commandNames, int fallback)
        {
            var budget = fallback;
            foreach (var name in commandNames ?? Enumerable.Empty<string>())
                budget += LongRunningSeconds(name);
            return Math.Min(budget, MaxBatchBudgetSeconds);
        }

        private const int MaxBatchBudgetSeconds = 1800;

        private const string ExecuteCodeToolName = "execute_code";

        internal const string ReplayExecuteCodeToolName = "replay_execute_code";

        // Room for the pre-compile refresh (up to 120 s) and the compile, which run before the snippet's own budget starts.
        private const int ExecuteCodeBudgetMarginSeconds = 120;

        // Capped so that the call ceiling stays at a batch's 1800 s, under the broker's hold deadline.
        internal const int MaxExecuteCodeTimeoutSeconds = MaxBatchBudgetSeconds - ExecuteCodeBudgetMarginSeconds;

        internal static int ClampExecuteCodeTimeout(long seconds) =>
            (int)Math.Min(Math.Max(seconds, 0), MaxExecuteCodeTimeoutSeconds);

        // Absent, zero or unreadable all mean "no request": the ordinary ceiling stays.
        internal static int ExecuteCodeTimeoutSeconds(IDictionary<string, object> parameters)
        {
            if (!(parameters.TryGetValue("arguments", out var arguments) && arguments is IDictionary<string, object> args) ||
                !args.TryGetValue("timeout_seconds", out var value) || value == null)
                return 0;

            try
            {
                return ClampExecuteCodeTimeout(Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                return 0;
            }
        }

        private static int LongRunningSeconds(string toolName) =>
            string.IsNullOrEmpty(toolName)
                ? 0
                : GetMethod(toolName)?.GetCustomAttribute<LongRunningToolAttribute>()?.Seconds ?? 0;

        // The commands argument is a JSON array in a string; a client that sends the array itself
        // arrives here as the parsed list.
        private static IEnumerable<string> BatchCommandNames(IDictionary<string, object> parameters)
        {
            if (!(parameters.TryGetValue("arguments", out var arguments) && arguments is IDictionary<string, object> args) ||
                !args.TryGetValue("commands", out var commands))
                return Enumerable.Empty<string>();

            if (commands is string text)
            {
                try
                {
                    return JArray.Parse(text).Select(command => (command as JObject)?["name"]?.ToString()).ToList();
                }
                catch (JsonException)
                {
                    // The batch refuses it as INVALID_COMMANDS itself, well inside the ordinary ceiling.
                    return Enumerable.Empty<string>();
                }
            }

            return commands is IEnumerable<object> list
                ? list.Select(command => command is IDictionary<string, object> c && c.TryGetValue("name", out var n) ? n as string : null).ToList()
                : Enumerable.Empty<string>();
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

            // The exporter reads both unguarded, and tools/list is built from the definition, not from `name`.
            definition.parameters ??= new ToolParametersDef();
            definition.parameters.properties ??= new Dictionary<string, ToolPropertyDef>();
            if (string.IsNullOrEmpty(definition.name))
                definition.name = name;

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

        public static bool IsManualTool(string name)
        {
            lock (_lock)
            {
                return _manualTools.ContainsKey(name);
            }
        }

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
