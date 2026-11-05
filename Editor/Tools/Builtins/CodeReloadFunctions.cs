// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// In-place [CodeReload] edits, through the Unity Pipeline package (com.unity.pipeline). The attribute is woven into IL when the
    /// code compiles and the engine that applies a reload (Assembly.Load backend, IL interpreter VM, player push) ships in that
    /// package, so these tools call its commands by reflection instead of carrying a second engine. Without the package they answer
    /// PACKAGE_NOT_INSTALLED. The package's command classes are internal, hence the reflection.
    /// </summary>
    [ToolProvider("CodeReload")]
    [RequiresPackage("com.unity.pipeline")]
    internal static class CodeReloadFunctions
    {
        private const string RuntimeAssembly = "Unity.Pipeline";
        private const string EditorAssembly = "Unity.Pipeline.Editor";
        private const string RuntimeCommands = "Unity.Pipeline.Runtime.Commands.CodeReloadCommands";
        private const string PushCommand = "Unity.Pipeline.Editor.Commands.PushReloadCommand";

        [Description("Compile the edited methods tagged [CodeReload] in a source file and apply them in place to the running code, through " +
                     "the Unity Pipeline package's Assembly.Load backend (Editor Play Mode). The tag must already be on the method when the " +
                     "code compiled, since the dispatch is woven in then. The body may only touch public members of its type. Needs " +
                     "com.unity.pipeline; the file must be inside the project.")]
        public static object ReloadFile(
            [ToolParam("Source file with [CodeReload] methods, e.g. Assets/Scripts/Player.cs")] string filename,
            [ToolParam("Compilation timeout in milliseconds. Default 30000.", Required = false)] int timeout = 30000,
            [ToolParam("Folder to also save the compiled assembly to (default: in memory only)", Required = false)] string assembly_dir = null,
            [ToolParam("Emit a portable PDB mapped to the source so breakpoints bind (compiles unoptimized)", Required = false)] bool pdb = false)
        {
            var file = ResolveInsideProject(filename, out var error);
            return error ?? Invoke(RuntimeAssembly, RuntimeCommands, "ReloadFile", file, timeout, assembly_dir, pdb);
        }

        [Description("Like reload_file, but the reloaded methods run through the Pipeline package's IL interpreter VM in this process " +
                     "instead of Assembly.Load. Use it to check that an edit stays within the interpreter's C# subset before pushing it " +
                     "to a device; private members are reachable here. Needs com.unity.pipeline.")]
        public static object ReloadFileEditorInterpreter(
            [ToolParam("Source file with [CodeReload] methods, e.g. Assets/Scripts/Player.cs")] string filename,
            [ToolParam("Compilation timeout in milliseconds. Default 30000.", Required = false)] int timeout = 30000,
            [ToolParam("Folder to also save the compiled IL to (default: in memory only)", Required = false)] string assembly_dir = null,
            [ToolParam("Emit a portable PDB mapped to the source", Required = false)] bool pdb = false)
        {
            var file = ResolveInsideProject(filename, out var error);
            return error ?? Invoke(RuntimeAssembly, RuntimeCommands, "ReloadFileEditorInterpreter", file, timeout, assembly_dir, pdb);
        }

        [Description("Compile a file's (or a folder's) [CodeReload] methods in the editor and push the IL to a connected development player " +
                     "over PlayerConnection, where the interpreter applies it (IL2CPP-safe). A folder is walked recursively and every .cs " +
                     "that mentions CodeReload is pushed once. The player's acknowledgement arrives later in the console. Needs " +
                     "com.unity.pipeline and a connected player.")]
        public static object ReloadFilePlayerInterpreter(
            [ToolParam("Source .cs file, or a folder with [CodeReload] methods, inside the project")] string filename,
            [ToolParam("Connected player id; -1 (default) broadcasts to every connected player", Required = false)] int player = -1)
        {
            var path = ResolveInsideProject(filename, out var error);
            return error ?? Invoke(EditorAssembly, PushCommand, "PushReload", path, player);
        }

        private static string ResolveInsideProject(string filename, out object error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(filename))
            {
                error = Response.Error("INVALID_PARAM", new { param = "filename", provided = filename });
                return null;
            }

            return PathSafety.ResolveProjectPath(filename.Trim());
        }

        private static object Invoke(string assemblyName, string typeName, string method, params object[] args)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName)
                ?.GetType(typeName, false);
            var target = type?.GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (target == null)
                return Response.Error("PACKAGE_NOT_INSTALLED", new { package = "com.unity.pipeline", missing = $"{typeName}.{method}" },
                    "Install com.unity.pipeline (the Unity CLI's package) to use the [CodeReload] tools. A newer version may have renamed the command.");

            object result;
            try
            {
                result = target.Invoke(null, args);
            }
            catch (TargetInvocationException ex)
            {
                return Response.Error("CODE_RELOAD_FAILED", new { message = (ex.InnerException ?? ex).Message });
            }

            var json = result == null ? new JObject() : JObject.FromObject(result);
            var success = (json["Success"] ?? json["success"])?.Value<bool>() ?? true;
            return success
                ? Response.Success((json["Message"] ?? json["message"])?.ToString() ?? "Done.", json)
                : Response.Error("CODE_RELOAD_FAILED", json);
        }
    }
}
