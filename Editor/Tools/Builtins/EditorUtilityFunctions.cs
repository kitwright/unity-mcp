// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using KitWright.Editor.Tools.Scripting;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Editor")]
    internal static class EditorUtilityFunctions
    {
        private const int MaxCodeFileBytes = 256 * 1024;

#if UNITY_EDITOR_WIN
        private const int SwRestore = 9;

        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
#endif

        [Description("Save every open scene that has unsaved changes, then every modified asset. A scene that was never saved " +
                     "(no path) is skipped and listed, since saving it would open a modal Save dialog. Edit Mode only.")]
        public static object SaveAll()
        {
            if (EditorApplication.isPlaying)
                return Response.Error("EDIT_MODE_REQUIRED", hint: "Scenes cannot be saved in Play Mode. Call exit_play_mode first.");

            var saved = new List<string>();
            var untitled = new List<string>();
            var failed = new List<string>();

            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || !scene.isDirty)
                    continue;

                if (string.IsNullOrEmpty(scene.path))
                    untitled.Add(scene.name);
                else if (EditorSceneManager.SaveScene(scene))
                    saved.Add(scene.path);
                else
                    failed.Add(scene.path);
            }

            AssetDatabase.SaveAssets();

            var data = new { savedScenes = saved, skippedUntitled = untitled, failedScenes = failed };
            return failed.Count > 0
                ? Response.Error("SCENE_SAVE_FAILED", data)
                : Response.Success($"Saved {saved.Count} scene(s) and the modified assets.", data,
                    untitled.Count > 0 ? "Untitled scenes were skipped: save them with save_scene and a path." : null);
        }

        [Description("Bring the Unity Editor window to the foreground. Windows only. The OS may refuse to move focus to a " +
                     "window while another application is receiving input; the reply says so when it does.")]
        public static object EditorFocus()
        {
#if UNITY_EDITOR_WIN
            var handle = Process.GetCurrentProcess().MainWindowHandle;
            if (handle == IntPtr.Zero)
                return Response.Error("NO_MAIN_WINDOW");

            if (IsIconic(handle))
                ShowWindow(handle, SwRestore);

            return SetForegroundWindow(handle)
                ? Response.Success("The Editor window is in the foreground.")
                : Response.Error("FOCUS_REFUSED", hint: "Windows only lets the foreground application hand focus over. Click the Editor window once.");
#else
            return Response.Error("PLATFORM_UNSUPPORTED", hint: "editor_focus is implemented for Windows only.");
#endif
        }

        [Description("Copy an external file (a texture, model, audio clip...) into the project and import it. The source is an " +
                     "absolute path anywhere on disk; the destination is an asset path under Assets/. Refuses to replace an " +
                     "existing asset unless confirm=true.")]
        [ToolProvider("Asset")]
        public static object ImportAsset(
            [ToolParam("Absolute filesystem path of the file to import")] string source,
            [ToolParam("Destination asset path under Assets/, with extension (e.g. Assets/Art/Wall.png)")] string path,
            [ToolParam("Must be true to replace an asset that already exists at the destination", Required = false)] bool confirm = false)
        {
            if (string.IsNullOrWhiteSpace(source) || !Path.IsPathRooted(source))
                return Response.Error("INVALID_PARAM", new { param = "source", provided = source }, "source must be an absolute path.");

            if (!File.Exists(source))
                return Response.Error("SOURCE_NOT_FOUND", new { source }, "source must be an existing file, not a folder.");

            if (!AssetFunctions.TryResolveUnderAssetsOrPackages(path, out var assetPath, out var absolutePath)
                || Path.GetFileName(assetPath).EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PATH", new { path },
                    "path must be an asset file under Assets/ with no '..' segments.");

            if (File.Exists(absolutePath) && !confirm)
                return Response.Error("ASSET_EXISTS", new { path = assetPath },
                    "An asset is already at this path. Pass confirm=true to replace it.");

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));
            File.Copy(source, absolutePath, true);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var imported = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return Response.Success($"Imported '{Path.GetFileName(source)}' as '{assetPath}'.", new
            {
                path = assetPath,
                guid = AssetDatabase.AssetPathToGUID(assetPath),
                type = imported != null ? imported.GetType().Name : null
            });
        }

        [Description("Run the C# in a .cs file inside the project exactly as execute_code would run it (same templates, safety " +
                     "checks and Undo grouping). Useful for a long snippet kept in a file, e.g. under a folder outside Assets/ " +
                     "so writing it triggers no import. With entry, the file is instead compiled in memory (no domain reload) and " +
                     "that public static method is called: entry is Namespace.Type.Method, Type.Method or Method. args is a JSON array " +
                     "coerced to its parameters (primitives, enum names, string[]; a UnityEngine.Object parameter takes an instanceId as " +
                     "tools return it, or an Assets/ or Packages/ path). A UnityEngine.Object result comes back as {instanceId, name, type}; " +
                     "compiler warnings, if any, as warnings. defines are prepended as #define lines; dry_run compiles only. The safety " +
                     "checks always apply to an entry. To apply [CodeReload] edits to a loaded type, use reload_file.")]
        [ToolProvider("ExecuteCode")]
        public static async Task<object> ExecuteCodeFile(
            [ToolParam("Path of the .cs file, relative to the project root or absolute inside it")] string file_path,
            [ToolParam("Same as execute_code's safety_checks. If omitted, the MCP Settings window default is used. Ignored with entry.", Required = false)] bool? safety_checks = null,
            [ToolParam("Same as execute_code's timeout_seconds (up to 1680): raises the call ceiling and the snippet's loop budget. 0 (default) keeps both. With entry, how long its task may run (default 30).", Required = false)] int timeout_seconds = 0,
            [ToolParam("Static method to call: Namespace.Type.Method, Type.Method or Method. Omit to run the file as an execute_code snippet.", Required = false)] string entry = null,
            [ToolParam("JSON array of arguments for entry", Required = false)] string args = null,
            [ToolParam("JSON array of extra #define symbols, for entry", Required = false)] string defines = null,
            [ToolParam("With entry: compile and find it, run nothing", Required = false)] bool dry_run = false)
        {
            var resolved = PathSafety.ResolveProjectPath(file_path);
            if (!File.Exists(resolved))
                return Response.Error("FILE_NOT_FOUND", new { file_path });

            if (new FileInfo(resolved).Length > MaxCodeFileBytes)
                return Response.Error("FILE_TOO_LARGE", new { file_path, max_bytes = MaxCodeFileBytes });

            if (!string.IsNullOrWhiteSpace(entry))
                return await RunEntry(resolved, entry, args, defines, dry_run, timeout_seconds > 0 ? ToolRegistry.ClampExecuteCodeTimeout(timeout_seconds) * 1000 : 30000);

            if (dry_run)
                return Response.Error("INVALID_PARAM", new { param = "dry_run" }, "dry_run needs entry: a snippet cannot be compiled without running it.");

            return await ScriptExecutionFunctions.ExecuteCode(File.ReadAllText(resolved), safety_checks, timeout_seconds: timeout_seconds);
        }

        private static async Task<object> RunEntry(string path, string entry, string args, string defines, bool dryRun, int timeoutMs)
        {
            var defineList = Defines(defines).ToList();
            var source = string.Concat(defineList.Select(d => "#define " + d + "\n")) + File.ReadAllText(path);
            if (ExecuteCodeSafetyPolicy.TryFindViolation(source, true, out var pattern, out var reason, out var alternative))
                return Response.Error("SAFETY_CHECK_BLOCKED", new { pattern, reason, alternative });

            var compilation = ScriptCompilerPipeline.Compile(source);
            if (compilation.Status != ScriptCompilationStatus.Success || compilation.Assembly == null)
                return Response.Error("COMPILATION_FAILED", new { compiler = compilation.CompilerName, errors = compilation.Errors.Select(e => e.ToString()).ToArray(), message = compilation.Message });

            if (CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out var reference, out var guardReason))
                return Response.Error("SAFETY_CHECK_BLOCKED", new { reference, reason = guardReason, stage = "compiled" });

            var method = FindEntry(compilation.Assembly, entry, out var entryError);
            if (method == null)
                return entryError;

            if (dryRun)
                return Response.Success("Compiled; nothing was run.", new { entry = method.DeclaringType.FullName + "." + method.Name, dry_run = true });

            if (!TryArguments(method, args, out var values, out var argError))
                return argError;

            object result;
            try
            {
                result = method.Invoke(null, values);
            }
            catch (TargetInvocationException ex)
            {
                return RunFailed(ex.InnerException ?? ex);
            }

            if (result is Task task)
            {
                var finished = await Task.WhenAny(task, Task.Delay(Math.Max(1000, timeoutMs)));
                if (finished != task)
                    return Response.Error("RUN_TIMEOUT", new { timeout_ms = timeoutMs }, "The entry's task is still running detached.");
                // A faulted non-generic Task has no Result to throw from, so it would otherwise read as a success.
                if (task.IsFaulted)
                    return RunFailed(task.Exception.GetBaseException());
                if (task.IsCanceled)
                    return RunFailed(new TaskCanceledException(task));

                result = task.GetType().IsGenericType ? task.GetType().GetProperty("Result")?.GetValue(task) : null;
            }

            JToken resultToken;
            try
            {
                resultToken = result == null ? null
                    : result is UnityEngine.Object unityObject
                        ? JToken.FromObject(new { instanceId = ObjectIdCodec.GetSerializableId(unityObject), name = unityObject.name, type = unityObject.GetType().Name })
                        : JToken.FromObject(result);
            }
            catch (Exception ex) when (ex is Newtonsoft.Json.JsonException || ex is InvalidOperationException || ex is NotSupportedException)
            {
                return Response.Error("RUN_FAILED", new { stage = "serialize", type = ex.GetType().Name, message = ex.Message },
                    "The entry ran, but its return value could not be turned into JSON. Return a string or a plain object.");
            }

            var ran = $"Ran {method.DeclaringType.Name}.{method.Name}.";
            var warnings = compilation.Warnings
                .Select(w => new ScriptCompilationError { line = w.line - defineList.Count, column = w.column, text = w.text, code = w.code })
                .Where(w => w.line >= 1)
                .ToList();
            return Response.Success(ran, warnings.Count == 0
                ? (object)new { result = resultToken }
                : new { result = resultToken, warnings = ScriptExecutionFunctions.Capped(warnings), warning_count = warnings.Count });
        }

        private static object RunFailed(Exception ex) =>
            Response.Error("RUN_FAILED", new { type = ex.GetType().Name, message = ex.Message, stack = ex.StackTrace });

        private static IEnumerable<string> Defines(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Enumerable.Empty<string>();
            try { return JsonParse.Array(json).Select(t => t.ToString()).Where(d => System.Text.RegularExpressions.Regex.IsMatch(d, "^[A-Za-z_][A-Za-z0-9_]*$")); }
            catch { return Enumerable.Empty<string>(); }
        }

        private static MethodInfo FindEntry(Assembly assembly, string entry, out object error)
        {
            error = null;
            var statics = assembly.GetTypes().Where(t => !t.IsGenericTypeDefinition)
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)).ToArray();

            var wanted = entry.Trim();
            var matches = statics.Where(m =>
            {
                var full = m.DeclaringType.FullName + "." + m.Name;
                return full == wanted || full.EndsWith("." + wanted, StringComparison.Ordinal);
            }).ToArray();

            if (matches.Length == 1)
                return matches[0];

            error = Response.Error(matches.Length == 0 ? "ENTRY_NOT_FOUND" : "AMBIGUOUS_ENTRY",
                new { entry, candidates = statics.Select(m => m.DeclaringType.FullName + "." + m.Name).ToArray() },
                "Name the entry as Namespace.Type.Method, Type.Method or Method.");
            return null;
        }

        private static bool TryArguments(MethodInfo method, string json, out object[] values, out object error)
        {
            error = null;
            var parameters = method.GetParameters();
            values = new object[parameters.Length];
            JArray supplied;
            try { supplied = string.IsNullOrWhiteSpace(json) ? new JArray() : JsonParse.Array(json); }
            catch (Exception ex)
            {
                error = Response.Error("INVALID_ARGS_JSON", new { message = ex.Message });
                return false;
            }

            for (var i = 0; i < parameters.Length; i++)
            {
                if (i >= supplied.Count)
                {
                    if (!parameters[i].HasDefaultValue)
                    {
                        error = Response.Error("MISSING_ARGUMENT", new { parameter = parameters[i].Name, method = method.Name });
                        return false;
                    }

                    values[i] = parameters[i].DefaultValue;
                    continue;
                }

                var parameterType = parameters[i].ParameterType;
                if (typeof(UnityEngine.Object).IsAssignableFrom(parameterType) &&
                    (supplied[i].Type == JTokenType.String || supplied[i].Type == JTokenType.Integer))
                {
                    var handle = supplied[i].ToString();
                    var found = handle.StartsWith("Assets/", StringComparison.Ordinal) || handle.StartsWith("Packages/", StringComparison.Ordinal)
                        ? AssetDatabase.LoadAssetAtPath(handle, parameterType)
                        : ObjectIdCodec.ToObject(handle);
                    if (found == null || !parameterType.IsInstanceOfType(found))
                    {
                        error = Response.Error("INVALID_ARGUMENT", new { parameter = parameters[i].Name, expected = parameterType.Name, value = handle },
                            "Pass an object's instanceId as a tool returned it, or an Assets/ or Packages/ path.");
                        return false;
                    }

                    values[i] = found;
                    continue;
                }

                try { values[i] = supplied[i].ToObject(parameterType); }
                catch (Exception ex)
                {
                    error = Response.Error("INVALID_ARGUMENT", new { parameter = parameters[i].Name, expected = parameters[i].ParameterType.Name, message = ex.Message });
                    return false;
                }
            }

            return true;
        }
    }
}
