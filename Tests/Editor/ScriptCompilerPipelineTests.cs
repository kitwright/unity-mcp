// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class ScriptCompilerPipelineTests
    {
        [Test]
        public void RoslynCompiler_ToolchainResolves()
        {
            var compiler = new RoslynCscScriptCompiler();

            Assert.IsTrue(compiler.TryResolveToolchain(out var compilerHostPath, out var cscPath, out var monoLibRoot, out var error), error);
            Assert.IsTrue(compilerHostPath.EndsWith("mono") || compilerHostPath.EndsWith("mono.exe") ||
                          compilerHostPath.EndsWith("dotnet") || compilerHostPath.EndsWith("dotnet.exe"), compilerHostPath);
            Assert.IsTrue(cscPath.EndsWith("csc.exe") || cscPath.EndsWith("csc.dll"), cscPath);
            StringAssert.Contains("MonoBleedingEdge", monoLibRoot);
        }

        [Test]
        public void CompilerPipeline_FallsBackToCodeDomWhenRoslynUnavailable()
        {
            var result = ScriptCompilerPipeline.Compile(
                "public class FallbackSmoke { public static string Run() { return \"ok\"; } }",
                new IScriptCompiler[]
                {
                    new FakeUnavailableCompiler("Roslyn"),
                    new FakeSuccessCompiler("CodeDom")
                });

            Assert.AreEqual(ScriptCompilationStatus.Success, result.Status);
            Assert.AreEqual("CodeDom", result.CompilerName);
            Assert.NotNull(result.Assembly);
            Assert.AreEqual(2, result.Attempts.Count);
            Assert.AreEqual("Unavailable", result.Attempts[0].status);
            Assert.AreEqual("Success", result.Attempts[1].status);
        }

        [UnityTest]
        public IEnumerator ExecuteCode_TraditionalSyntax_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class TraditionalSyntax { public static string Run() { var value = 1 + 2; return \"legacy:\" + value; } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("legacy:3", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        // The generated Run() used to be declared `string`, so a bare body returning anything else
        // failed to compile -- the shortest snippet form only worked for one return type.
        [UnityTest]
        public IEnumerator ExecuteCode_BareBody_ReturnsANonStringValue()
        {
            return ExecuteCodeAndAssert(
                "return 1 + 2;",
                result =>
                {
                    AssertSuccess(result);
                    Assert.AreEqual("3", GetProperty<string>(GetProperty<object>(result, "data"), "result"));
                },
                skipRefresh: true);
        }

        [UnityTest]
        public IEnumerator ExecuteCode_BareBodyWithNoReturn_StillSucceeds()
        {
            return ExecuteCodeAndAssert(
                "var unused = UnityEngine.Application.unityVersion;",
                result =>
                {
                    AssertSuccess(result);
                    Assert.AreEqual("OK", GetProperty<string>(GetProperty<object>(result, "data"), "result"));
                },
                skipRefresh: true);
        }

        [UnityTest]
        public IEnumerator ExecuteCode_SkipRefresh_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class SkipRefreshSyntax { public static string Run() { return \"skip-refresh-ok\"; } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("skip-refresh-ok", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                },
                skipRefresh: true);
        }

        [UnityTest]
        public IEnumerator ExecuteCode_TargetTypedNew_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class TargetTypedNewSyntax { public static string Run() { System.Text.StringBuilder sb = new(); sb.Append(\"modern\"); return sb.ToString(); } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("modern", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_SwitchExpression_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                "public class SwitchExpressionSyntax { public static string Run() { var value = 2; return value switch { 2 => \"two\", _ => \"other\" }; } }",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("two", GetProperty<string>(data, "result"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_IKitWrightCommand_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                @"using KitWright.Editor.Tools.Scripting;

public class CommandSyntax : IKitWrightCommand
{
    public void Execute(KitWright.Editor.Tools.Scripting.ExecutionContext ctx)
    {
        System.Collections.Generic.List<string> values = new() { ""a"", ""b"" };
        ctx.Log(""count="" + values.Count);
        ctx.ReturnValue = values.Count switch { 2 => ""two"", _ => ""other"" };
    }
}",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                    Assert.AreEqual("two", GetProperty<object>(data, "returnValue"));

                    var logs = (IEnumerable)GetProperty<object>(data, "logs");
                    var found = false;
                    foreach (var log in logs)
                    {
                        if (GetField<string>(log, "Message") == "count=2")
                            found = true;
                    }
                    Assert.IsTrue(found, "Expected IKitWrightCommand ctx.Log output in response data.");
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_IKitWrightCommandMissingUsing_RunsWithRoslyn()
        {
            return ExecuteCodeAndAssert(
                @"public class CommandSyntaxWithoutUsing : IKitWrightCommand
{
    public void Execute(ExecutionContext ctx)
    {
        ctx.Log(""auto using ok"");
        ctx.ReturnValue = ""ok"";
    }
}",
                result =>
                {
                    AssertSuccess(result);
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                    Assert.AreEqual("ok", GetProperty<object>(data, "returnValue"));
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_CompilationError_ReturnsStructuredErrorFormat()
        {
            return ExecuteCodeAndAssert(
                "public class BadSyntax { public static string Run() { return \"oops\" } }",
                result =>
                {
                    AssertError(result, "COMPILATION_FAILED");
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));

                    var errors = (IEnumerable)GetProperty<object>(data, "errors");
                    var sawDiagnostic = false;
                    foreach (var error in errors)
                    {
                        Assert.GreaterOrEqual(GetField<int>(error, "line"), 0);
                        Assert.GreaterOrEqual(GetField<int>(error, "column"), 0);
                        Assert.IsNotEmpty(GetField<string>(error, "text"));
                        StringAssert.StartsWith("CS", GetField<string>(error, "code"));
                        sawDiagnostic = true;
                    }

                    Assert.IsTrue(sawDiagnostic, "Expected at least one Roslyn diagnostic.");
                });
        }

        [UnityTest]
        public IEnumerator ExecuteCode_RuntimeError_ReturnsStructuredErrorFormat()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(@"\[KitWright\] Script runtime error: boom from test"));
            return ExecuteCodeAndAssert(
                "public class RuntimeBoom { public static string Run() { throw new System.InvalidOperationException(\"boom from test\"); } }",
                result =>
                {
                    AssertError(result, "RUNTIME_ERROR");
                    var data = GetProperty<object>(result, "data");
                    Assert.AreEqual("boom from test", GetProperty<string>(data, "message"));
                    Assert.AreEqual("Roslyn", GetProperty<string>(data, "compiler"));
                });
        }

        // Drives the tool's undo helper directly: compiling a real snippet here would pay a compile
        // per assert and needs a quiet editor, while the grouping is what this test is about.
        [Test]
        public void UndoGroup_CollapsesSnippetMutationsIntoOneNamedStep()
        {
            GameObject first = null;
            GameObject second = null;
            var wasDirty = SceneManager.GetActiveScene().isDirty;
            try
            {
                var result = ScriptExecutionFunctions.RunInSingleUndoGroup("execute_code: Probe", () =>
                {
                    first = new GameObject("KitWrightUndoProbeA");
                    Undo.RegisterCreatedObjectUndo(first, "execute_code: create");
                    second = new GameObject("KitWrightUndoProbeB");
                    Undo.RegisterCreatedObjectUndo(second, "execute_code: create");
                    return "ok";
                });

                Assert.AreEqual("ok", result);
                // The group name is the regression guard. Undo.PerformUndo is deliberately not
                // asserted: undo execution is unproven headless, and a no-op there would fail this
                // test for a reason that has nothing to do with the grouping.
                Assert.AreEqual("execute_code: Probe", Undo.GetCurrentGroupName());
            }
            finally
            {
                if (first != null) UnityEngine.Object.DestroyImmediate(first);
                if (second != null) UnityEngine.Object.DestroyImmediate(second);
                if (!wasDirty)
                    HierarchyFunctionsTests.ClearSceneDirtiness(SceneManager.GetActiveScene());
            }
        }

        [Test]
        public void UndoGroup_ThrowingSnippetStillCollapses()
        {
            Assert.Throws<InvalidOperationException>(() =>
                ScriptExecutionFunctions.RunInSingleUndoGroup("execute_code: Boom",
                    () => throw new InvalidOperationException("boom")));

            Assert.AreEqual("execute_code: Boom", Undo.GetCurrentGroupName());
        }

        [TestCase("public class Snippet : IKitWrightCommand { }", "execute_code: Snippet")]
        [TestCase("\n  return 1 + 2;\n", "execute_code: return 1 + 2;")]
        public void UndoGroupName_NamesTheStepAfterTheSnippet(string code, string expected)
        {
            Assert.AreEqual(expected, ScriptExecutionFunctions.UndoGroupName(code));
        }

        // Paths are built with Path.Combine rather than written literally: a backslash is an
        // ordinary filename character on Linux, so a hardcoded Windows path makes the filter see
        // one long name and the test pass only on Windows.
        private static string Lib(string file) => System.IO.Path.Combine("lib", file);

        [Test]
        public void CodeDomFilter_DropsForwardedAssembliesOnlyWithNetstandard()
        {
            var withNetstandard = new[]
            {
                Lib("netstandard.dll"),
                Lib("mscorlib.dll"),
                Lib("System.Collections.dll"),
                Lib("UnityEngine.dll")
            };

            Assert.AreEqual(
                new[] { Lib("netstandard.dll"), Lib("UnityEngine.dll") },
                ScriptCompilerReferences.FilterForCodeDom(withNetstandard));

            var withoutNetstandard = new[] { Lib("mscorlib.dll"), Lib("UnityEngine.dll") };
            Assert.AreEqual(withoutNetstandard, ScriptCompilerReferences.FilterForCodeDom(withoutNetstandard));
        }

        [Test]
        public void CodeDomErrors_SkipMcsPhantomBomEntryButKeepRealDiagnostics()
        {
            var errors = new[]
            {
                new FakeCompilerError { ErrorNumber = "", ErrorText = "\uFEFF" },
                new FakeCompilerError { ErrorNumber = null, ErrorText = "  \t\r\n" },
                new FakeCompilerError { ErrorNumber = "CS0103", ErrorText = "The name 'x' does not exist", Line = 7 }
            };

            var result = CodeDomScriptCompiler.GetCodeDomErrors(errors);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("CS0103", result[0].code);
            Assert.AreEqual(7, result[0].line);
        }

        [Test]
        public void LoadedPaths_AreCachedAndDeduplicatedBySimpleName()
        {
            var first = ScriptCompilerReferences.GetLoadedPaths();
            Assert.AreSame(first, ScriptCompilerReferences.GetLoadedPaths());
            CollectionAssert.IsNotEmpty(first);
            CollectionAssert.AllItemsAreUnique(
                first.Select(path => System.IO.Path.GetFileNameWithoutExtension(path).ToLowerInvariant()).ToArray());
        }

        private static IEnumerator ExecuteCodeAndAssert(string code, Action<object> assert, bool skipRefresh = false)
        {
            var task = ScriptExecutionFunctions.ExecuteCode(code, false, skipRefresh);
            while (!task.IsCompleted)
                yield return null;

            if (task.Exception != null)
                throw task.Exception;

            assert(task.Result);
        }

        private static void AssertSuccess(object result)
        {
            Assert.IsTrue(GetProperty<bool>(result, "success"), Describe(result));
        }

        private static void AssertError(object result, string expectedCode)
        {
            Assert.IsFalse(GetProperty<bool>(result, "success"));
            Assert.AreEqual(expectedCode, GetProperty<string>(result, "code"));
            Assert.AreEqual(expectedCode, GetProperty<string>(result, "error"));
        }

        private static T GetProperty<T>(object obj, string name)
        {
            var prop = obj.GetType().GetProperty(name);
            Assert.NotNull(prop, $"Missing property {name} on {obj.GetType().FullName}");
            return (T)prop.GetValue(obj);
        }

        private static T GetField<T>(object obj, string name)
        {
            var field = obj.GetType().GetField(name);
            Assert.NotNull(field, $"Missing field {name} on {obj.GetType().FullName}");
            return (T)field.GetValue(obj);
        }

        private static string Describe(object obj, int depth = 0)
        {
            if (obj == null)
                return "null";
            if (depth > 5)
                return "...";
            if (obj is string s)
                return s;
            if (obj is IEnumerable enumerable && !(obj is string))
            {
                var list = new StringBuilder("[");
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (index++ > 0)
                        list.Append(", ");
                    if (index > 8)
                    {
                        list.Append("...");
                        break;
                    }
                    list.Append(Describe(item, depth + 1));
                }
                list.Append("]");
                return list.ToString();
            }

            var type = obj.GetType();
            if (type.IsPrimitive || type.IsEnum)
                return obj.ToString();

            var sb = new StringBuilder(type.Name).Append("{");
            var first = true;
            foreach (var prop in type.GetProperties())
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(prop.Name).Append("=").Append(Describe(prop.GetValue(obj), depth + 1));
            }
            foreach (var field in type.GetFields())
            {
                if (!first)
                    sb.Append(", ");
                first = false;
                sb.Append(field.Name).Append("=").Append(Describe(field.GetValue(obj), depth + 1));
            }
            sb.Append("}");
            return sb.ToString();
        }

        // GetCodeDomErrors reads its members reflectively by name, so a duck-typed fake exercises the
        // real filter without needing System.CodeDom or a compiler run.
        private sealed class FakeCompilerError
        {
            public bool IsWarning { get; set; }
            public int Line { get; set; }
            public int Column { get; set; }
            public string ErrorNumber { get; set; }
            public string ErrorText { get; set; }
        }

        private sealed class FakeUnavailableCompiler : IScriptCompiler
        {
            public FakeUnavailableCompiler(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public ScriptCompilationResult Compile(string code)
            {
                return ScriptCompilationResult.Unavailable(Name, "forced unavailable");
            }
        }

        private sealed class FakeSuccessCompiler : IScriptCompiler
        {
            public FakeSuccessCompiler(string name)
            {
                Name = name;
            }

            public string Name { get; }

            public ScriptCompilationResult Compile(string code)
            {
                return ScriptCompilationResult.Success(Name, typeof(string).Assembly);
            }
        }
    }
}
