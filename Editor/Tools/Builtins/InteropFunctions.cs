// Copyright (C) KitWright. All rights reserved.

using System;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Interop;
using KitWright.Editor.Tools.Helpers;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Interop")]
    internal static class InteropFunctions
    {
        [Description("Report the status of known third-party code-patching plugins (currently: SingularityGroup Hot Reload). " +
                     "Returns per plugin: loaded, server health, whether its compile detour is installed, and its recent timeline " +
                     "(which members were hot-patched, which changes it could not patch, and its own compile errors). " +
                     "Use after editing .cs files while such a plugin is active to verify the new code actually reached the running domain: " +
                     "an AppliedChange naming your member means it is live; PartiallySupportedChange / UnsupportedChange / CompileError means it is not, " +
                     "and the plugin normally follows those with a full recompile of its own — read the timeline instead of assuming either way. " +
                     "The plugin patches method bodies in editor and player assemblies alike; what it cannot patch is the kind of change " +
                     "(const values, signatures, new types, lambda closures, attributes, field initializers). " +
                     "When compile_detour_installed is true, request_recompile and execute_code refreshes skip compile-escalation, since a detoured " +
                     "AssetDatabase.Refresh / RequestScriptCompilation is a no-op and waiting for a compile start would only run to its timeout.")]
        [ReadOnlyTool]
        public static object GetCodePatchingStatus()
        {
            try
            {
                var suppressed = HotReload.IsSuppressingCompilation;
                return Response.Success(
                    suppressed
                        ? "A code-patching plugin is active: it detours refresh/compile, so editor scripts cannot start a Unity compilation. Ordinary method-body edits are hot-patched; for a change it cannot patch (const values, signatures, new types, lambda closures, attributes, field initializers) it runs its own full recompile, so read the timeline below rather than assuming either outcome."
                        : "No code-patching plugin is suppressing the Unity compile pipeline.",
                    new { compile_pipeline_suppressed = suppressed, plugins = new[] { HotReload.GetStatus() } });
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }
    }
}
