// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Reflection;

namespace KitWright.Editor.Tools.Scripting
{
    /// <summary>
    /// Second safety pass, run after the snippet compiles and before it is invoked.
    /// <see cref="ExecuteCodeSafetyPolicy"/> matches patterns in source, which any alias, string
    /// concatenation or reflection hop defeats; this walks the compiled assembly's TypeRef and
    /// MemberRef tables instead, so what gets checked is what the snippet actually binds to.
    /// </summary>
    internal static class CompiledCodeGuard
    {
        // Metadata table prefixes (ECMA-335 II.22): TypeRef rows are 0x01xxxxxx, MemberRef 0x0Axxxxxx.
        private const int TypeRefTable = 0x01000000;
        private const int MemberRefTable = 0x0A000000;
        private const int MaxRows = 20000;

        private static readonly string[] BlockedTypes =
        {
            "System.Diagnostics.Process",
            "System.IO.FileSystemWatcher",
            // Gadget-chain deserializers (ysoserial.net): live on Unity mono, never legit in a snippet.
            "System.Runtime.Serialization.Formatters.Binary.BinaryFormatter",
            "System.Runtime.Serialization.Formatters.Soap.SoapFormatter",
            "System.Runtime.Serialization.NetDataContractSerializer"
        };

        private static readonly string[] BlockedMembers =
        {
            "System.IO.File.Delete",
            "System.IO.File.Replace",
            "System.IO.Directory.Delete",
            "System.IO.Directory.Move",
            "System.Environment.Exit",
            "System.Environment.FailFast",
            "UnityEditor.EditorApplication.Exit",
            "UnityEditor.AssetDatabase.DeleteAsset",
            "UnityEditor.AssetDatabase.DeleteAssets",
            "UnityEditor.FileUtil.DeleteFileOrDirectory"
        };

        // Reflection and expression trees launder a blocked call past every other check: the source
        // never names Process/File.Delete (so the L1 regex misses) and the type is resolved from a
        // runtime string (so no blocked TypeRef/MemberRef reaches the tables above). What the snippet
        // cannot hide is the invocation primitive itself, which is a real MemberRef here. Blocking the
        // primitives is the enforceable choke point.
        // ponytail: best-effort, not a boundary. A determined caller can still reach IL through paths
        // not listed here; the real boundary is safety_checks=false being unavailable, or out-of-process
        // execution. Legitimate reflection under safety_checks=true must pass safety_checks=false.
        private static readonly string[] BlockedReflectionMembers =
        {
            "System.Activator.CreateInstance",
            "System.Reflection.MethodBase.Invoke",
            // The one-argument overload lives on ConstructorInfo itself, so MethodBase.Invoke above
            // never sees it.
            "System.Reflection.ConstructorInfo.Invoke",
            "System.Delegate.DynamicInvoke",
            // Writes a private field or property, including a guard's own: no call to the target at all.
            "System.Reflection.FieldInfo.SetValue",
            "System.Reflection.PropertyInfo.SetValue",
            "System.Reflection.MethodInfo.CreateDelegate",
            "System.Delegate.CreateDelegate",
            "System.Type.InvokeMember",
            "System.Type.GetType",
            "System.Reflection.Assembly.GetType",
            "System.Reflection.Assembly.Load",
            "System.Reflection.Assembly.LoadFrom",
            "System.Reflection.Assembly.LoadFile",
            "System.Linq.Expressions.Expression.Call",
            "System.Reflection.Emit.ILGenerator.Emit",
            // Raw-IL injection without ILGenerator; mono-only, so unreachable on .NET Core today.
            "System.Reflection.Emit.MethodBuilder.SetMethodBody",
            // A raw function pointer + a marshalled delegate reaches the target through neither
            // MethodBase.Invoke nor CreateDelegate; block both ends of that chain.
            "System.RuntimeMethodHandle.GetFunctionPointer",
            "System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer"
        };

        // A modal dialog or file picker runs its own message loop, which stops the editor from
        // pumping MCP requests: the snippet never returns and the caller hangs until a human clicks.
        private static readonly string[] ModalMembers =
        {
            "UnityEditor.EditorUtility.DisplayDialog",
            "UnityEditor.EditorUtility.DisplayDialogComplex",
            "UnityEditor.EditorUtility.SaveFilePanel",
            "UnityEditor.EditorUtility.SaveFilePanelInProject",
            "UnityEditor.EditorUtility.SaveFolderPanel",
            "UnityEditor.EditorUtility.OpenFilePanel",
            "UnityEditor.EditorUtility.OpenFilePanelWithFilters",
            "UnityEditor.EditorUtility.OpenFolderPanel",
            "UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo",
            "UnityEditor.SceneManagement.EditorSceneManager.SaveModifiedScenesIfUserWantsTo",
            "UnityEditor.EditorWindow.ShowModal",
            "UnityEditor.EditorWindow.ShowModalUtility",
            // Reaches every dialog in the editor by menu path, so it bypasses the entries above.
            // The execute_menu_item tool is the guarded way in: it refuses modal paths itself.
            "UnityEditor.EditorApplication.ExecuteMenuItem",
            // Same reach, one identifier away. Internal on current Unity, so a snippet cannot bind
            // to it and there is no test for it -- listed because it has been public before.
            "UnityEditor.EditorApplication.ExecuteMenuItemWithTemporaryContext"
        };

        internal static bool IsModalMember(string reference)
        {
            return Matches(reference, ModalMembers);
        }

        private static readonly string[] StrictTypes =
        {
            "System.IO.FileStream",
            "System.IO.StreamWriter",
            "System.IO.BinaryWriter",
            "System.IO.FileInfo",
            "System.IO.DirectoryInfo",
            "System.Net.WebClient",
            "System.Net.WebRequest",
            "System.Net.Http.HttpClient",
            "System.Net.Sockets.Socket",
            "System.Net.Sockets.TcpClient"
        };

        private static readonly string[] StrictMembers =
        {
            "System.IO.File.WriteAllText",
            "System.IO.File.WriteAllBytes",
            "System.IO.File.WriteAllLines",
            "System.IO.File.AppendAllText",
            "System.IO.File.Create",
            "System.IO.File.Copy",
            "System.IO.File.Move",
            "System.IO.Directory.CreateDirectory"
        };

        public static bool TryFindViolation(Assembly assembly, bool strict, out string reference, out string reason)
        {
            return TryFindViolation(assembly, strict, MaxRows, out reference, out reason);
        }

        internal static bool TryFindViolation(Assembly assembly, bool strict, int maxRows, out string reference, out string reason)
        {
            reference = null;
            reason = null;

            foreach (var module in assembly.GetModules())
            {
                if (ScanTypeRefs(module, strict, maxRows, ref reference, ref reason) ||
                    ScanMemberRefs(module, strict, maxRows, ref reference, ref reason) ||
                    ScanForPInvoke(module, ref reference, ref reason))
                    return true;
            }

            return false;
        }

        private static bool ScanTypeRefs(Module module, bool strict, int maxRows, ref string reference, ref string reason)
        {
            for (var row = 1; ; row++)
            {
                var resolved = TryResolve(() => module.ResolveType(TypeRefTable | row), out Type type, out var exhausted);
                if (exhausted)
                    return false;
                if (row > maxRows)
                    return RefuseUnscanned(module, "TypeRef", maxRows, ref reference, ref reason);
                if (!resolved)
                    continue;

                var name = type?.FullName;
                if (name == null)
                    continue;

                if (Matches(name, BlockedTypes) || (strict && Matches(name, StrictTypes)))
                {
                    reference = name;
                    reason = $"The compiled snippet references '{name}', which execute_code refuses to run.";
                    return true;
                }
            }
        }

        private static bool ScanMemberRefs(Module module, bool strict, int maxRows, ref string reference, ref string reason)
        {
            for (var row = 1; ; row++)
            {
                var resolved = TryResolve(() => module.ResolveMember(MemberRefTable | row), out MemberInfo member, out var exhausted);
                if (exhausted)
                    return false;
                if (row > maxRows)
                    return RefuseUnscanned(module, "MemberRef", maxRows, ref reference, ref reason);
                if (!resolved)
                    continue;

                var declaring = member?.DeclaringType?.FullName;
                if (declaring == null)
                    continue;

                var name = $"{declaring}.{member.Name}";
                if (Matches(name, ModalMembers))
                {
                    reference = name;
                    reason = $"The compiled snippet calls '{name}', which opens a modal dialog and would " +
                             "freeze the editor until a human dismisses it, hanging this request. " +
                             "Decide in the snippet instead of asking the user, pass an explicit path, or use the " +
                             "dedicated tool (execute_menu_item refuses modal menu paths for you).";
                    return true;
                }

                if (Matches(name, BlockedMembers) || Matches(name, BlockedReflectionMembers) ||
                    (strict && Matches(name, StrictMembers)))
                {
                    reference = name;
                    reason = $"The compiled snippet calls '{name}', which execute_code refuses to run.";
                    return true;
                }
            }
        }

        // A [DllImport] method calls native code directly: it never references a blocked managed
        // type or member, so the ref-table scans above are blind to it. Its P/Invoke flag lives on
        // the method definition instead, so this pass walks the defined methods for it.
        private static bool ScanForPInvoke(Module module, ref string reference, ref string reason)
        {
            Type[] types;
            try { types = module.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { return false; }

            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            foreach (var type in types)
            {
                if (type == null)
                    continue;

                MethodInfo[] methods;
                try { methods = type.GetMethods(all); }
                catch { continue; }

                foreach (var method in methods)
                {
                    if ((method.Attributes & MethodAttributes.PinvokeImpl) == 0)
                        continue;

                    reference = $"{type.FullName}.{method.Name}";
                    reason = $"The compiled snippet declares a native P/Invoke method '{reference}', " +
                             "which calls unmanaged code directly and execute_code refuses to run.";
                    return true;
                }
            }

            return false;
        }

        // The cap bounds the scan, so a table longer than it holds references nobody checked. Letting
        // that through made padding past the cap a way around every list above; no snippet comes near it.
        private static bool RefuseUnscanned(Module module, string table, int maxRows, ref string reference, ref string reason)
        {
            reference = module.Name;
            reason = $"The compiled snippet has more than {maxRows} {table} rows, more than the safety scan reads, " +
                     "so it cannot vouch for the rest and execute_code refuses to run it. Split the snippet.";
            return true;
        }

        // Mono reports a row past the end of the table as ArgumentOutOfRangeException; anything else
        // (a reference this domain cannot load) is skipped rather than treated as the end of the table.
        private static bool TryResolve<T>(Func<T> resolve, out T value, out bool exhausted) where T : class
        {
            value = null;
            exhausted = false;

            try
            {
                value = resolve();
                return value != null;
            }
            catch (ArgumentOutOfRangeException)
            {
                exhausted = true;
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Matches(string candidate, IEnumerable<string> blocked)
        {
            foreach (var entry in blocked)
            {
                if (string.Equals(candidate, entry, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
