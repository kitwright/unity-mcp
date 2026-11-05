// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools.Helpers;
using KitWright.Editor.Tools.Scripting;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// GameObject, scene, play-mode, script and batch tools under the Unity CLI's names and parameter shapes, so a client written
    /// against that server finds them here. Where KitWright already had the capability under another name, these call it.
    /// </summary>
    [ToolProvider("Editor")]
    internal static class UnityCliEditorFunctions
    {
        private static readonly string[] Primitives = { "cube", "sphere", "capsule", "cylinder", "plane", "quad" };

        // ---- GameObjects ----

        [Description("Create an empty GameObject or a built-in primitive (cube, sphere, capsule, cylinder, plane, quad) in the active scene.")]
        [ToolProvider("GameObject")]
        public static object CreateGameobject(
            [ToolParam("Name; defaults to 'GameObject' or the primitive's name", Required = false)] string name = null,
            [ToolParam("Parent GameObject identifier", Required = false)] string parent = null,
            [ToolParam("Primitive: cube, sphere, capsule, cylinder, plane or quad. Omit for an empty GameObject.", Required = false)] string primitive = null)
        {
            return string.IsNullOrWhiteSpace(primitive)
                ? GameObjectFunctions.CreateGameObject(string.IsNullOrWhiteSpace(name) ? "GameObject" : name, parent)
                : GameObjectFunctions.CreatePrimitive(string.IsNullOrWhiteSpace(name) ? Capitalize(primitive) : name, primitive, parent: parent);
        }

        [Description("Create N empty GameObjects or primitives in one call, as one Undo step. positions, rotations and scales are JSON arrays of " +
                     "[x,y,z] with one entry per object. With count > 1 the names are Name1..NameN.")]
        [ToolProvider("GameObject")]
        public static object CreateGameobjects(
            [ToolParam("How many to create. Default 1.", Required = false)] int count = 1,
            [ToolParam("Base name", Required = false)] string name = null,
            [ToolParam("Parent GameObject identifier", Required = false)] string parent = null,
            [ToolParam("Primitive type, or omit for empty GameObjects", Required = false)] string primitive = null,
            [ToolParam("JSON array of local positions [[x,y,z],...]", Required = false)] string positions = null,
            [ToolParam("JSON array of local Euler rotations", Required = false)] string rotations = null,
            [ToolParam("JSON array of local scales", Required = false)] string scales = null)
        {
            if (count < 1 || count > 1000)
                return Response.Error("INVALID_PARAM", new { param = "count", provided = count }, "count must be from 1 to 1000.");

            if (!string.IsNullOrWhiteSpace(primitive) && Array.IndexOf(Primitives, primitive.Trim().ToLowerInvariant()) < 0)
                return Response.Error("UNKNOWN_PRIMITIVE", new { primitive, valid = Primitives });

            if (!TryVectors(positions, count, nameof(positions), out var places, out var error) ||
                !TryVectors(rotations, count, nameof(rotations), out var turns, out error) ||
                !TryVectors(scales, count, nameof(scales), out var sizes, out error))
                return error;

            Transform parentTransform = null;
            if (!string.IsNullOrWhiteSpace(parent))
            {
                var parentObject = ObjectsHelper.FindObject(parent, null);
                if (parentObject == null)
                    return ObjectsHelper.NotFound(nameof(parent), parent);
                parentTransform = parentObject.transform;
            }

            var baseName = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(primitive) ? "GameObject" : Capitalize(primitive)) : name;
            var created = new List<object>();

            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            for (var i = 0; i < count; i++)
            {
                var go = string.IsNullOrWhiteSpace(primitive)
                    ? new GameObject()
                    : GameObject.CreatePrimitive((PrimitiveType)Array.IndexOf(Primitives, primitive.Trim().ToLowerInvariant()));
                go.name = count > 1 ? baseName + (i + 1) : baseName;
                Undo.RegisterCreatedObjectUndo(go, "Create " + go.name);
                if (parentTransform != null) Undo.SetTransformParent(go.transform, parentTransform, "Parent " + go.name);
                if (places != null) go.transform.localPosition = places[i];
                if (turns != null) go.transform.localEulerAngles = turns[i];
                if (sizes != null) go.transform.localScale = sizes[i];
                created.Add(new { instanceId = ObjectIdCodec.GetSerializableId(go), name = go.name });
            }

            Undo.SetCurrentGroupName("Create " + baseName);
            Undo.CollapseUndoOperations(group);
            return Response.Success($"Created {count} GameObject(s).", new { created });
        }

        [Description("Delete a GameObject (undoable).")]
        [ToolProvider("GameObject")]
        public static object DeleteGameobject([ToolParam("Identifier of the GameObject")] string target) =>
            GameObjectFunctions.DeleteGameObject(target);

        [Description("Rename a GameObject.")]
        [ToolProvider("GameObject")]
        public static object RenameGameobject(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("New name")] string name) =>
            GameObjectFunctions.RenameGameObject(target, name);

        [Description("Find GameObjects in the loaded scenes by exact name, tag, component type and/or exact hierarchy path; the filters combine.")]
        [ReadOnlyTool]
        [ToolProvider("GameObject")]
        public static object FindGameobjects(
            [ToolParam("Exact name", Required = false)] string name = null,
            [ToolParam("Tag", Required = false)] string tag = null,
            [ToolParam("Component type name, e.g. 'Rigidbody' or 'UnityEngine.Camera'", Required = false)] string type = null,
            [ToolParam("Exact hierarchy path, e.g. '/Root/Child'", Required = false)] string hierarchy_path = null,
            [ToolParam("Include inactive objects. Default true.", Required = false)] bool include_inactive = true)
        {
            Type componentType = null;
            if (!string.IsNullOrWhiteSpace(type))
            {
                componentType = TypeResolver.ResolveComponent(type);
                if (componentType == null)
                    return TypeResolver.UnresolvedError(type, "COMPONENT_TYPE_NOT_FOUND", nameof(type));
            }

            var found = new List<object>();
            for (var i = 0; i < SceneManager.sceneCount && found.Count < 200; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                    foreach (var go in root.GetComponentsInChildren<Transform>(include_inactive).Select(t => t.gameObject))
                    {
                        if (!string.IsNullOrEmpty(name) && go.name != name) continue;
                        if (!string.IsNullOrEmpty(tag) && !go.CompareTag(tag)) continue;
                        if (componentType != null && go.GetComponent(componentType) == null) continue;

                        var path = HierarchyPath(go);
                        if (!string.IsNullOrEmpty(hierarchy_path) && path != hierarchy_path) continue;

                        found.Add(new { instanceId = ObjectIdCodec.GetSerializableId(go), name = go.name, hierarchyPath = path, scene = scene.name });
                        if (found.Count >= 200) break;
                    }
            }

            return Response.Success($"{found.Count} GameObject(s) found (at most 200 are listed).", new { count = found.Count, objects = found });
        }

        // ---- Scenes ----

        [Description("Create a new scene and save it under the given path (the Assets/ prefix and .unity are optional). template 'default' adds a " +
                     "Main Camera and a Directional Light; additive opens it next to the open scenes. An unsaved open scene is refused unless additive.")]
        [ToolProvider("Scene")]
        public static object CreateScene(
            [ToolParam("Scene path, e.g. Scenes/Level1")] string path,
            [ToolParam("Open it additively. Default false.", Required = false)] bool additive = false,
            [ToolParam("'empty' (default) or 'default'", Required = false)] string template = "empty")
        {
            if (!UnityCliPaths.Resolve(path, ".unity", out var assetPath, out var absolute, out var error))
                return error;

            var seeded = string.Equals(template, "default", StringComparison.OrdinalIgnoreCase);
            if (!seeded && !string.Equals(template ?? "empty", "empty", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PARAM", new { param = "template", provided = template }, "template is 'empty' or 'default'.");

            if (System.IO.File.Exists(absolute))
                return Response.Error("SCENE_EXISTS", new { path = assetPath });

            if (!additive && SceneManager.GetActiveScene().isDirty && !EditorApplication.isPlaying)
                return Response.Error("UNSAVED_CHANGES", new { scene = SceneManager.GetActiveScene().name }, "Save the open scene with save_scene first, or pass additive=true.");

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absolute));
            var scene = EditorSceneManager.NewScene(seeded ? NewSceneSetup.DefaultGameObjects : NewSceneSetup.EmptyScene,
                additive ? NewSceneMode.Additive : NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, assetPath))
                return Response.Error("SCENE_SAVE_FAILED", new { path = assetPath });

            return Response.Success($"Created scene '{assetPath}'.", new { path = assetPath, additive, template = seeded ? "default" : "empty" });
        }

        [Description("Add a scene to the Build Settings list (idempotent). The Assets/ prefix and .unity are optional. enabled sets its checkbox.")]
        [ToolProvider("Build")]
        public static object AddSceneToBuild(
            [ToolParam("Scene path")] string path,
            [ToolParam("Enable it in the build list. Default true.", Required = false)] bool enabled = true)
        {
            if (!UnityCliPaths.Resolve(path, ".unity", out var scene, out _, out var error))
                return error;

            if (AssetDatabase.GetMainAssetTypeAtPath(scene) != typeof(SceneAsset))
                return Response.Error("SCENE_NOT_FOUND", new { path = scene });

            var list = EditorBuildSettings.scenes.ToList();
            var index = list.FindIndex(s => string.Equals(s.path, scene, StringComparison.OrdinalIgnoreCase));
            if (index < 0) list.Add(new EditorBuildSettingsScene(scene, enabled));
            else list[index] = new EditorBuildSettingsScene(scene, enabled);
            EditorBuildSettings.scenes = list.ToArray();

            return Response.Success(index < 0 ? $"Added '{scene}' to the build list." : $"'{scene}' was already listed; enabled={enabled}.",
                new { path = scene, enabled, sceneCount = list.Count });
        }

        [Description("Remove a scene from the Build Settings list (idempotent).")]
        [ToolProvider("Build")]
        public static object RemoveSceneFromBuild([ToolParam("Scene path")] string path)
        {
            if (!UnityCliPaths.Resolve(path, ".unity", out var scene, out _, out var error))
                return error;

            var list = EditorBuildSettings.scenes.ToList();
            var removed = list.RemoveAll(s => string.Equals(s.path, scene, StringComparison.OrdinalIgnoreCase));
            EditorBuildSettings.scenes = list.ToArray();
            return Response.Success(removed > 0 ? $"Removed '{scene}'." : $"'{scene}' was not listed.", new { path = scene, removed = removed > 0, sceneCount = list.Count });
        }

        [Description("List the open scenes with their loaded, active and dirty state.")]
        [ReadOnlyTool]
        [ToolProvider("Scene")]
        public static object ListOpenScenes()
        {
            var active = SceneManager.GetActiveScene();
            var scenes = Enumerable.Range(0, SceneManager.sceneCount).Select(SceneManager.GetSceneAt).Select(s => new
            {
                name = s.name,
                path = s.path,
                isLoaded = s.isLoaded,
                isActive = s == active,
                isDirty = s.isDirty,
                rootCount = s.isLoaded ? s.rootCount : 0
            }).ToArray();
            return Response.Success($"{scenes.Length} open scene(s).", new { scenes });
        }

        [Description("Return the GameObject tree of an open scene (the active one when path is omitted); every node carries an instance id and a hierarchy path.")]
        [ReadOnlyTool]
        [ToolProvider("GameObject")]
        public static object GetSceneHierarchy(
            [ToolParam("Path of the open scene; the Assets/ prefix and .unity are optional", Required = false)] string path = null)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (!UnityCliPaths.Resolve(path, ".unity", out var scenePath, out _, out var error))
                    return error;

                var scene = SceneManager.GetSceneByPath(scenePath);
                if (!scene.IsValid() || !scene.isLoaded)
                    return Response.Error("SCENE_NOT_OPEN", new { path = scenePath });

                var nodes = scene.GetRootGameObjects().Select(Node).ToArray();
                return Response.Success($"Scene '{scene.name}' read.", new { scene = scene.path, roots = nodes });
            }

            var active = SceneManager.GetActiveScene();
            return Response.Success($"Scene '{active.name}' read.", new { scene = active.path, roots = active.GetRootGameObjects().Select(Node).ToArray() });
        }

        // ---- Editor and play mode ----

        [Description("Enter Play Mode.")]
        [ToolProvider("PlayMode")]
        public static object EditorPlay() => Response.Success(GameObjectlessResult(SceneFunctions.EnterPlayMode()));

        [Description("Exit Play Mode.")]
        [ToolProvider("PlayMode")]
        public static object EditorStop() => Response.Success(GameObjectlessResult(SceneFunctions.ExitPlayMode()));

        [Description("Toggle the pause state of Play Mode.")]
        [ToolProvider("PlayMode")]
        public static object EditorPause() => Response.Success(GameObjectlessResult(SceneFunctions.SetPaused(!EditorApplication.isPaused)));

        [Description("Detailed Editor status: project, Play Mode, compiling, updating.")]
        [ReadOnlyTool]
        public static object EditorStatus() => EditorStateFunctions.GetEditorState();

        [Description("Report the last script compile: 'compiling' while one runs, else 'idle'; compilation_failed says whether the current " +
                     "assemblies have compile errors. Start one with recompile.")]
        [ReadOnlyTool]
        [ToolProvider("Compilation")]
        public static object RecompileStatus()
        {
            var compiling = EditorApplication.isCompiling || EditorApplication.isUpdating;
            return Response.Success(compiling ? "Unity is compiling or importing." : "No compile is running.", new
            {
                status = compiling ? "compiling" : "idle",
                compilation_failed = EditorUtility.scriptCompilationFailed
            });
        }

        [Description("Force a script recompile. focus=true brings the Editor to the foreground first (Windows only). Poll recompile_status or " +
                     "pass nothing and read the answer, which waits up to 30 s.")]
        [ToolProvider("Compilation")]
        public static async Task<object> Recompile(
            [ToolParam("Bring the Editor to the foreground first. Default false.", Required = false)] bool focus = false)
        {
            if (focus)
                EditorUtilityFunctions.EditorFocus();

            return await CompilationFunctions.RequestRecompile(30, true);
        }

        [Description("Execute an Editor menu item by path, or list the available items when no path is given.")]
        public static object Menu(
            [ToolParam("Menu item path, e.g. 'Assets/Reimport All'. Omit to list the items.", Required = false)] string path = "")
        {
            if (!string.IsNullOrWhiteSpace(path))
                return MenuItemFunctions.ExecuteMenuItem(path);

            var type = typeof(EditorApplication).Assembly.GetType("UnityEditor.Menu");
            var method = type?.GetMethod("GetMenuItems", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public, null,
                new[] { typeof(string), typeof(bool), typeof(bool) }, null);
            if (method == null)
                return Response.Error("FEATURE_UNAVAILABLE", hint: "This Unity version does not expose its menu list.");

            var items = (method.Invoke(null, new object[] { string.Empty, true, false }) as string[] ?? new string[0]).Take(2000).ToArray();
            return Response.Success($"{items.Length} menu item(s) (at most 2000 are listed).", new { items });
        }

        // ---- Scripts ----

        [Description("Evaluate C# code, like execute_code. timeout is in milliseconds.")]
        [ToolProvider("ExecuteCode")]
        public static Task<object> Eval(
            [ToolParam("C# code to evaluate")] string code,
            [ToolParam("Timeout in milliseconds. Default 5000.", Required = false)] int timeout = 5000) =>
            ScriptExecutionFunctions.ExecuteCode(code, null, false, SecondsBeyondDefault(timeout));

        [Description("Evaluate C# code read from a .cs file inside the project, like execute_code_file.")]
        [ToolProvider("ExecuteCode")]
        public static Task<object> EvalFile(
            [ToolParam("Path of the .cs file")] string file,
            [ToolParam("Timeout in milliseconds. Default 5000.", Required = false)] int timeout = 5000) =>
            EditorUtilityFunctions.ExecuteCodeFile(file, timeout_seconds: SecondsBeyondDefault(timeout));

        [Description("Compile a project .cs file in memory (no domain reload) and run a named static entry point. entry is Namespace.Type.Method, " +
                     "Type.Method or Method; by default the single public static method, else one named Main. args is a JSON array coerced " +
                     "to the parameters (primitives, enum names, string[]; a UnityEngine.Object parameter takes an instanceId as tools return " +
                     "it, or an Assets/ or Packages/ path). A UnityEngine.Object result comes back as {instanceId, name, type}; compiler " +
                     "warnings, if any, as warnings. dry_run compiles only. defines are prepended as #define lines; " +
                     "mode 'hotpatch' applies [CodeReload] edits instead (needs com.unity.pipeline). The same safety checks as execute_code apply.")]
        [ToolProvider("ExecuteCode")]
        public static async Task<object> RunScript(
            [ToolParam("Path of the .cs file, relative to the project root")] string file,
            [ToolParam("Entry point", Required = false)] string entry = null,
            [ToolParam("JSON array of arguments", Required = false)] string args = null,
            [ToolParam("JSON array of extra #define symbols", Required = false)] string defines = null,
            [ToolParam("Compile only; run nothing", Required = false)] bool dry_run = false,
            [ToolParam("'ephemeral' (default) or 'hotpatch'", Required = false)] string mode = "ephemeral",
            [ToolParam("Milliseconds the entry may run. Default 30000.", Required = false)] int timeout_ms = 30000)
        {
            var path = PathSafety.ResolveProjectPath(file ?? string.Empty);
            if (!System.IO.File.Exists(path))
                return Response.Error("FILE_NOT_FOUND", new { file });

            if (string.Equals(mode, "hotpatch", StringComparison.OrdinalIgnoreCase))
                return CodeReloadFunctions.ReloadFile(file);

            var defineList = Defines(defines).ToList();
            var source = string.Concat(defineList.Select(d => "#define " + d + "\n")) + System.IO.File.ReadAllText(path);
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

            if (dry_run)
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
                var finished = await Task.WhenAny(task, Task.Delay(Math.Max(1000, timeout_ms)));
                if (finished != task)
                    return Response.Error("RUN_TIMEOUT", new { timeout_ms }, "The entry's task is still running detached.");
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

        // ---- Batch ----

        [Description("Run several tools in one request. operations is a JSON array of {id?, command, params}; on_error 'abort' (default) stops at " +
                     "the first failure, 'continue' runs them all. dry_run validates the command names without running anything. Same engine " +
                     "as batch_execute (one Undo step). With transactional=true and on_error 'abort', a failure (or the time budget) reverts the " +
                     "batch's Undo-tracked scene changes (not file, asset, settings or Play Mode changes), and undo/redo operations are refused. " +
                     "A string param whose whole value is \"$<id or index>.<path>\" is replaced by that earlier operation's result, read " +
                     "under its data first (\"$0.instanceId\"), then the whole result (\"$0.data.instanceId\"); the path starts with a letter " +
                     "or '_', so \"$0.99\" stays text, and \"$$<id>.<path>\" passes \"$<id>.<path>\" through literally. A reference to a later, " +
                     "failed or unknown operation, or a path that is not there, fails that operation with UNRESOLVED_REFERENCE.")]
        [ToolProvider("MCP")]
        public static async Task<object> Batch(
            [ToolParam("JSON array of {id?, command, params}; id matches ^[A-Za-z_][A-Za-z0-9_-]*$ and is unique")] string operations,
            [ToolParam("Group every operation into one Undo step and roll it back on failure. Default true.", Required = false)] bool transactional = true,
            [ToolParam("'abort' (default) or 'continue'", Required = false)] string on_error = "abort",
            [ToolParam("Validate only", Required = false)] bool dry_run = false)
        {
            JArray parsed;
            try { parsed = JsonParse.Array(operations); }
            catch (Exception ex) { return Response.Error("INVALID_OPERATIONS_JSON", new { message = ex.Message }); }

            var commands = new JArray();
            foreach (var item in parsed.OfType<JObject>())
            {
                var command = item.Value<string>("command");
                if (string.IsNullOrWhiteSpace(command) || ToolRegistry.GetMethod(command) == null)
                    return Response.Error("UNKNOWN_COMMAND", new { command }, "Every operation needs a command naming a registered tool.");

                var op = new JObject { ["name"] = command, ["params"] = item["params"] ?? new JObject() };
                if (item["id"] != null && item["id"].Type != JTokenType.Null)
                    op["id"] = item["id"];
                commands.Add(op);
            }

            if (dry_run)
                return Response.Success($"{commands.Count} operation(s) are valid; nothing ran.", new { count = commands.Count, dry_run = true });

            var abort = !string.Equals(on_error, "continue", StringComparison.OrdinalIgnoreCase);
            return await BatchFunctions.Run(commands, abort, transactional ? "batch" : null,
                rollback_on_error: transactional && abort, resolveReferences: true);
        }

        // ---- Tests, console, performance ----

        [Description("Cancel the running test execution.")]
        [ToolProvider("Testing")]
        public static object CancelTests() => TestRunnerFunctions.CancelTestRun();

        [Description("Status of the running or last test execution.")]
        [ReadOnlyTool]
        [ToolProvider("Testing")]
        public static Task<object> TestStatus() => TestRunnerFunctions.GetTestJob(null, 0);

        [Description("Read the captured console output. level is the lowest severity (log, warn, error); tail is how many entries. The CLI's " +
                     "follow cursor (since) is not kept: use get_console_logs with since_seconds to read only what is new.")]
        [ReadOnlyTool]
        [ToolProvider("Console")]
        public static string Console(
            [ToolParam("Lowest severity: log (default), warn or error", Required = false)] string level = "log",
            [ToolParam("How many recent entries. Default 100.", Required = false)] int tail = 100)
        {
            var type = (level ?? "log").Trim().ToLowerInvariant();
            return VisualFeedbackFunctions.GetConsoleLogs(type == "error" ? "error" : type == "warn" || type == "warning" ? "warning" : "all", Math.Max(1, Math.Min(tail, 1000)));
        }

        [Description("Console counters without the entries: whether compilation failed and how many errors, warnings and logs the Editor console holds.")]
        [ReadOnlyTool]
        [ToolProvider("Console")]
        public static object ConsoleStatus()
        {
            int errors = 0, warnings = 0, logs = 0;
            var type = typeof(EditorApplication).Assembly.GetType("UnityEditor.LogEntries");
            var counts = type?.GetMethod("GetCountsByType", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (counts != null)
            {
                var args = new object[] { 0, 0, 0 };
                counts.Invoke(null, args);
                errors = (int)args[0];
                warnings = (int)args[1];
                logs = (int)args[2];
            }

            return Response.Success("Console counters read.", new
            {
                compilationFailed = EditorUtility.scriptCompilationFailed,
                consoleErrors = errors,
                consoleWarnings = warnings,
                consoleLogs = logs
            });
        }

        [Description("Read render, memory and frame-timing stats.")]
        [ReadOnlyTool]
        [ToolProvider("Profiling")]
        public static string GetPerformanceStats() => PerformanceFunctions.GetPerformanceSnapshot();

        // ---- Bakes ----

        [Description("Start an asynchronous lightmap bake of the open scene(s), like bake_lightmaps. dry_run only reports whether a bake could start.")]
        [ToolProvider("Lighting")]
        public static object BakeLighting(
            [ToolParam("Accepted for parity; a bake overwrites existing lightmap data", Required = false)] bool confirm = false,
            [ToolParam("Do not bake; report whether one could start", Required = false)] bool dry_run = false)
        {
            if (!dry_run)
                return LightingFunctions.BakeLightmaps();

            return EditorApplication.isPlaying || Lightmapping.isRunning
                ? Response.Error("BAKE_NOT_POSSIBLE", new { playing = EditorApplication.isPlaying, running = Lightmapping.isRunning })
                : Response.Success("A lightmap bake could start.", new { dry_run = true });
        }

        // ---- helpers ----

        private static string GameObjectlessResult(string text) => text;

        private static string Capitalize(string text) => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text.Substring(1).ToLowerInvariant();

        private static int SecondsBeyondDefault(int timeoutMs) => timeoutMs > 20000 ? (int)Math.Ceiling(timeoutMs / 1000.0) : 0;

        private static string HierarchyPath(GameObject go)
        {
            var path = "/" + go.name;
            for (var t = go.transform.parent; t != null; t = t.parent)
                path = "/" + t.name + path;
            return path;
        }

        private static object Node(GameObject go) => new
        {
            instanceId = ObjectIdCodec.GetSerializableId(go),
            name = go.name,
            hierarchyPath = HierarchyPath(go),
            active = go.activeSelf,
            children = go.transform.Cast<Transform>().Select(t => Node(t.gameObject)).ToArray()
        };

        private static bool TryVectors(string json, int count, string param, out Vector3[] vectors, out object error)
        {
            vectors = null;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
                return true;

            try
            {
                var array = JsonParse.Array(json);
                if (array.Count != count)
                {
                    error = Response.Error("INVALID_PARAM", new { param, expected = count, provided = array.Count }, $"{param} needs one [x,y,z] per object.");
                    return false;
                }

                vectors = array.Select(v => new Vector3(v[0].Value<float>(), v[1].Value<float>(), v[2].Value<float>())).ToArray();
                return true;
            }
            catch (Exception ex)
            {
                error = Response.Error("INVALID_PARAM", new { param, message = ex.Message }, $"{param} is a JSON array of [x,y,z] arrays.");
                return false;
            }
        }

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

            MethodInfo[] matches;
            if (string.IsNullOrWhiteSpace(entry))
                matches = statics.Length == 1 ? statics : statics.Where(m => m.Name == "Main").ToArray();
            else
                matches = statics.Where(m => (m.DeclaringType.FullName + "." + m.Name).EndsWith(entry.Trim(), StringComparison.Ordinal) &&
                                              ((m.DeclaringType.FullName + "." + m.Name).Length == entry.Trim().Length ||
                                               (m.DeclaringType.FullName + "." + m.Name).EndsWith("." + entry.Trim(), StringComparison.Ordinal))).ToArray();

            if (matches.Length == 1)
                return matches[0];

            error = Response.Error(matches.Length == 0 ? "ENTRY_NOT_FOUND" : "AMBIGUOUS_ENTRY",
                new { entry, candidates = statics.Select(m => m.DeclaringType.FullName + "." + m.Name).ToArray() },
                "Name the entry as Namespace.Type.Method, or leave one public static method (or a Main) in the file.");
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

    /// <summary>Asset paths as the Unity CLI writes them: the Assets/ prefix and the extension are optional, bare paths follow the authoring root.</summary>
    internal static class UnityCliPaths
    {
        internal static bool Resolve(string path, string extension, out string assetPath, out string absolutePath, out object error)
        {
            assetPath = null;
            absolutePath = null;
            error = null;

            var text = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (text.Length > 0 && !text.EndsWith(extension, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(System.IO.Path.GetExtension(text)))
                text += extension;

            var bare = !text.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
            if (bare && AuthoringRoot.Explicit == null && text.Length > 0)
                text = "Assets/" + text.TrimStart('/');

            if (!AssetFunctions.TryResolveUnderAssetsOrPackages(text, out assetPath, out absolutePath))
            {
                error = Response.Error("INVALID_PATH", new { path }, "path must be a file under Assets/ (or under the authoring root) with no '..' segments.");
                return false;
            }

            return true;
        }
    }
}
