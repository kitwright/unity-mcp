// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class CompiledCodeGuardTests
    {
        // The strict source rule anchors on "File." with a (?<![\w.]) lookbehind, or on the literal
        // "System.IO.File.", so a namespace alias slips past it while still binding to the same method.
        private const string AliasedWrite = @"
using IO = System.IO;

public class Aliased
{
    public static string Run()
    {
        IO.File.WriteAllText(""probe.txt"", ""hello"");
        return ""done"";
    }
}";

        private const string PlainWrite = @"
using System.IO;

public class Writer
{
    public static string Run()
    {
        File.WriteAllText(""probe.txt"", ""hello"");
        return ""done"";
    }
}";

        private const string ReflectedDelete = @"
public class Reflected
{
    public static string Run()
    {
        var handle = typeof(System.IO.File);
        handle.GetMethod(""Delete"", new[] { typeof(string) });
        return ""done"";
    }
}";

        private const string ModalDialog = @"
using UnityEditor;

public class Modal
{
    public static string Run()
    {
        EditorUtility.DisplayDialog(""Hi"", ""There"", ""OK"");
        return ""done"";
    }
}";

        // Any dialog in the editor is one menu path away, so this is the one entry in ModalMembers
        // that blocks a call which is harmless in every other context -- and therefore the one most
        // likely to be deleted again by whoever finds their own snippet refused.
        private const string MenuItemHop = @"
using UnityEditor;

public class MenuHop
{
    public static string Run()
    {
        return EditorApplication.ExecuteMenuItem(""Assets/Refresh"").ToString();
    }
}";


        private const string Benign = @"
using UnityEngine;

public class Benign
{
    public static string Run()
    {
        return Application.unityVersion;
    }
}";

        // GetMethods() (plural) slips past the source rule's \.GetMethod\s*\( and .Invoke with a var
        // slips past MethodInfo..Invoke, so the source policy misses this. The compiled snippet still
        // binds to MethodBase.Invoke, which is where it is caught.
        private const string ReflectedInvokeDelete = @"
using System;
using System.Linq;

public class RInvoke
{
    public static string Run()
    {
        var m = typeof(System.IO.File).GetMethods().First(x => x.Name == ""Delete"");
        m.Invoke(null, new object[] { ""probe.txt"" });
        return ""done"";
    }
}";

        // The type name is built at runtime and resolved through an Assembly instance, so neither the
        // literal appears in source nor a Process TypeRef in the metadata. Assembly.GetType is the bind.
        private const string RuntimeStringType = @"
using System;
using System.Linq;

public class RString
{
    public static string Run()
    {
        string n = string.Concat(""System.Diagnostics.Proc"", ""ess"");
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = a.GetType(n);
            if (t != null) return t.FullName;
        }
        return null;
    }
}";

        // A raw function pointer plus a marshalled delegate reaches the target through neither
        // MethodBase.Invoke nor CreateDelegate -- it survived the first reflection block.
        private const string FunctionPointerMarshal = @"
using System;
using System.Linq;
using System.Runtime.InteropServices;

public class FnPtr
{
    public static string Run()
    {
        var m = typeof(System.IO.File).GetMethods().First(x => x.Name == ""Delete"");
        var ptr = m.MethodHandle.GetFunctionPointer();
        var del = Marshal.GetDelegateForFunctionPointer(ptr, typeof(Action<string>));
        return del == null ? ""n"" : ""y"";
    }
}";

        // [DllImport] calls native code directly and references no blocked managed type or member,
        // so the ref-table scans are blind to it; the P/Invoke flag on the method is the signal.
        private const string PInvokeNative = @"
using System;
using System.Runtime.InteropServices;

public class Native
{
    [DllImport(""kernel32.dll"", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    public static object Run()
    {
        return GetModuleHandle(""kernel32.dll"").ToString();
    }
}";

        // A gadget-chain deserializer reaches RCE from a crafted blob without naming a blocked member;
        // the type itself is the catch, since constructing one is never legitimate in a snippet.
        private const string LegacyDeserializer = @"
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

public class Deser
{
    public static object Run()
    {
        var bf = new BinaryFormatter();
        using var ms = new MemoryStream(new byte[] { 0, 1, 0, 0, 0 });
        return bf.Deserialize(ms);
    }
}";

        [Test]
        public void SourcePolicy_MissesAnAliasedNamespace()
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(AliasedWrite, true, out _, out _));
        }

        [Test]
        public void Guard_CatchesTheCallTheSourcePolicyMissed()
        {
            var compilation = ScriptCompilerPipeline.Compile(AliasedWrite);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out var reference, out _));
            Assert.AreEqual("System.IO.File.WriteAllText", reference);
        }

        // Reflection that resolves the member by name is out of reach: the assembly binds to
        // Type.GetMethod, not to the method it ends up calling. The source rules catch this one.
        [Test]
        public void Guard_DoesNotSeeThroughReflectionDispatch()
        {
            var compilation = ScriptCompilerPipeline.Compile(ReflectedDelete);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsFalse(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out _, out _));
            Assert.IsTrue(ExecuteCodeSafetyPolicy.TryFindViolation(ReflectedDelete, true, out _, out _));
        }

        [Test]
        public void Guard_BlocksAStrictFilesystemWriteOnlyWhenStrict()
        {
            var compilation = ScriptCompilerPipeline.Compile(PlainWrite);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out var reference, out _));
            Assert.AreEqual("System.IO.File.WriteAllText", reference);

            Assert.IsFalse(CompiledCodeGuard.TryFindViolation(compilation.Assembly, false, out _, out _));
        }

        // A modal dialog hangs the request rather than damaging anything, so it is blocked
        // regardless of the strict filesystem setting.
        [Test]
        public void Guard_BlocksAModalDialogEvenWhenNotStrict()
        {
            var compilation = ScriptCompilerPipeline.Compile(ModalDialog);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, false, out var reference, out var reason));
            Assert.AreEqual("UnityEditor.EditorUtility.DisplayDialog", reference);
            Assert.That(reason, Does.Contain("modal"));
        }

        [Test]
        public void Guard_BlocksTheMenuItemRouteToEveryDialog()
        {
            var compilation = ScriptCompilerPipeline.Compile(MenuItemHop);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, false, out var reference, out var reason));
            Assert.AreEqual("UnityEditor.EditorApplication.ExecuteMenuItem", reference);
            Assert.That(reason, Does.Contain("execute_menu_item"));
        }


        [Test]
        public void Guard_LetsAnOrdinarySnippetThrough()
        {
            var compilation = ScriptCompilerPipeline.Compile(Benign);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsFalse(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out _, out _));
        }

        // Reflection that actually invokes launders a blocked call past both the source rule and the
        // metadata scan of the target member; the invocation primitive is the enforceable choke point.
        [Test]
        public void Guard_BlocksReflectionInvocation()
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(ReflectedInvokeDelete, true, out _, out _));

            var compilation = ScriptCompilerPipeline.Compile(ReflectedInvokeDelete);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out var reference, out _));
            Assert.AreEqual("System.Reflection.MethodBase.Invoke", reference);
        }

        // A type name built at runtime resolves through Assembly.GetType, leaving no dangerous TypeRef;
        // blocking the resolver closes the runtime-string path both strict settings share.
        [Test]
        public void Guard_BlocksRuntimeStringTypeResolution()
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(RuntimeStringType, true, out _, out _));

            var compilation = ScriptCompilerPipeline.Compile(RuntimeStringType);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, false, out var reference, out _));
            Assert.AreEqual("System.Reflection.Assembly.GetType", reference);
        }

        [Test]
        public void Guard_BlocksTheFunctionPointerMarshalChain()
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(FunctionPointerMarshal, true, out _, out _));

            var compilation = ScriptCompilerPipeline.Compile(FunctionPointerMarshal);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out var reference, out _));
            Assert.That(reference, Does.Contain("FunctionPointer"),
                "Either the raw pointer extraction or the marshalled delegate must be refused.");
        }

        [Test]
        public void Guard_BlocksNativePInvokeDeclarations()
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(PInvokeNative, true, out _, out _));

            var compilation = ScriptCompilerPipeline.Compile(PInvokeNative);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out var reference, out var reason));
            Assert.AreEqual("Native.GetModuleHandle", reference);
            Assert.That(reason, Does.Contain("P/Invoke"));
        }

        [Test]
        public void Guard_BlocksLegacyDeserializers()
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(LegacyDeserializer, true, out _, out _));

            var compilation = ScriptCompilerPipeline.Compile(LegacyDeserializer);
            Assert.AreEqual(ScriptCompilationStatus.Success, compilation.Status, compilation.Message);

            Assert.IsTrue(CompiledCodeGuard.TryFindViolation(compilation.Assembly, true, out var reference, out _));
            Assert.AreEqual("System.Runtime.Serialization.Formatters.Binary.BinaryFormatter", reference);
        }
    }
}
