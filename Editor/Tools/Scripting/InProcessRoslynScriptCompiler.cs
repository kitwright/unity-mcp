// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace KitWright.Editor.Tools.Scripting
{
    // Compiles in the editor with the Roslyn that ships in every Unity install (MonoBleedingEdge/lib/mono/4.5,
    // 3.7, netstandard2.0). It is reached through reflection, so the package carries no Roslyn of its own and
    // never clashes with a package that does. Reusing the MetadataReference objects is what makes a warm
    // compile ~20 ms against ~580 ms for spawning csc: Roslyn keeps each reference's parsed metadata.
    // Any error is handed back to the pipeline so csc (4.3) compiles it again: Roslyn 3.7 is C# 8
    // plus early C# 9 previews and reports newer syntax as plain parse errors, not as a language-version code.
    [InitializeOnLoad]
    internal sealed class InProcessRoslynScriptCompiler : IScriptCompiler
    {
        private const string UsedThisSessionKey = "KitWright.InProcessRoslyn.Used";
        private static readonly string[] OwnAssemblies =
        {
            "Microsoft.CodeAnalysis",
            "Microsoft.CodeAnalysis.CSharp",
            "System.Collections.Immutable",
            "System.Reflection.Metadata"
        };

        private static readonly HashSet<string> MutedWarnings = new HashSet<string> { "CS1701", "CS1702" };
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, object> ReferenceCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // Roslyn 3.7 compiles these as plain attributes and drops what they do; csc honours them.
        private static readonly string[] AttributesItIgnores = { "ModuleInitializer", "SkipLocalsInit" };

        private static RoslynApi _api;
        private static string _disabledReason;
        private const int MainThreadTimeoutSeconds = 15;

        private static volatile bool _warm;
        private static volatile bool _referencesStale;
        private static int _warming;

        internal const string CompilerName = "RoslynInProcess";

        internal static bool IsWarm => _warm;

        public string Name => CompilerName;

        static InProcessRoslynScriptCompiler()
        {
            // An agent's edit-recompile-run loop hits the cold compile after every reload, so a session that
            // used execute_code warms it in the background; one that never did loads no Roslyn at all.
            if (SessionState.GetBool(UsedThisSessionKey, false))
                EditorApplication.delayCall += WarmUp;

            // A compile without a domain reload after it (deferred in Play Mode, reloads locked) rewrites DLLs
            // the cached references were read from.
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += _ => _referencesStale = true;
        }

        // The cold compile takes seconds, so the main thread never waits for it:
        // until the warm-up is done, or while it holds the lock, csc compiles the snippet instead.
        public ScriptCompilationResult Compile(string code)
        {
            SessionState.SetBool(UsedThisSessionKey, true);
            if (_disabledReason != null)
                return ScriptCompilationResult.Unavailable(CompilerName, _disabledReason);

            var ignored = AttributesItIgnores.FirstOrDefault(a => code != null && code.Contains(a));
            if (ignored != null)
                return ScriptCompilationResult.HandOff(CompilerName, $"Roslyn 3.7 ignores [{ignored}]; compiling with csc.");

            if (!_warm)
            {
                WarmUp();
                return ScriptCompilationResult.HandOff(CompilerName, "In-process Roslyn is warming up; compiling with csc.");
            }

            if (!Monitor.TryEnter(Gate))
                return ScriptCompilationResult.HandOff(CompilerName, "In-process Roslyn is busy; compiling with csc.");
            // The editor thread is held while it compiles, like csc's process wait, so it gets the same limit.
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(MainThreadTimeoutSeconds)))
                    return CompileLocked(code, DefaultRoslynDirectory(), ReferencePaths(), load: true, timeout.Token);
            }
            finally
            {
                Monitor.Exit(Gate);
            }
        }

        // Paths are resolved on the main thread: EditorApplication.applicationContentsPath is main-thread only.
        private static void WarmUp()
        {
            if (_warm || _disabledReason != null || Interlocked.Exchange(ref _warming, 1) == 1)
                return;

            var directory = DefaultRoslynDirectory();
            var paths = ReferencePaths();
            Task.Run(() =>
            {
                lock (Gate)
                {
                    var result = CompileLocked("internal static class KitWrightWarmUp {}", directory, paths, load: false, CancellationToken.None);
                    _warm = result.Status == ScriptCompilationStatus.Success;
                }
                Interlocked.Exchange(ref _warming, 0);
            });
        }

        private static string[] ReferencePaths() => RoslynCscScriptCompiler.GetReferencePaths(
            ScriptCompilerReferences.GetLoadedPaths(), RoslynCscScriptCompiler.ResolveMonoLibRoot());

        private static ScriptCompilationResult CompileLocked(string code, string roslynDirectory, string[] referencePaths, bool load,
            CancellationToken cancellation)
        {
            if (_api == null)
            {
                try
                {
                    _api = RoslynApi.Load(roslynDirectory);
                }
                catch (Exception ex)
                {
                    // A Roslyn that will not load or bind (a different System.Collections.Immutable won the
                    // binding, a missing member) fails the same way on every call, so stop trying until reload.
                    var inner = Builtins.ScriptExecutionFunctions.UnwrapTargetInvocationException(ex);
                    _disabledReason = $"In-process Roslyn disabled until the next domain reload: {inner.GetType().Name}: {inner.Message}";
                    return ScriptCompilationResult.Unavailable(CompilerName, _disabledReason);
                }
            }

            try
            {
                return CompileWith(code, referencePaths, load, cancellation);
            }
            catch (Exception ex)
            {
                // One bad or timed-out compile (a reference file mid-write) says nothing about the next one.
                var inner = Builtins.ScriptExecutionFunctions.UnwrapTargetInvocationException(ex);
                return ScriptCompilationResult.HandOff(CompilerName, $"In-process compile failed ({inner.GetType().Name}: {inner.Message}); compiling with csc.");
            }
        }

        private static ScriptCompilationResult CompileWith(string code, string[] referencePaths, bool load, CancellationToken cancellation)
        {
            if (_referencesStale)
            {
                _referencesStale = false;
                ReferenceCache.Clear();
            }

            var references = _api.NewReferenceList();
            foreach (var path in referencePaths)
            {
                if (!ReferenceCache.TryGetValue(path, out var reference))
                    ReferenceCache[path] = reference = _api.CreateReference(path);
                references.Add(reference);
            }

            var emitted = _api.Compile(code ?? string.Empty, references, cancellation, out var diagnostics);

            var errors = new List<ScriptCompilationError>();
            var warnings = new List<ScriptCompilationError>();
            foreach (var diagnostic in diagnostics)
            {
                if (diagnostic.Severity == "Error")
                    errors.Add(diagnostic.Error);
                else if (diagnostic.Severity == "Warning" && !MutedWarnings.Contains(diagnostic.Error.code))
                    warnings.Add(diagnostic.Error);
            }

            if (emitted == null || errors.Count > 0)
            {
                var first = errors.FirstOrDefault();
                return ScriptCompilationResult.HandOff(CompilerName,
                    $"{errors.Count} error(s) in-process, retrying with csc" + (first == null ? "." : $" (first: {first.code} {first.text})."));
            }

            return ScriptCompilationResult.Success(CompilerName, load ? Assembly.Load(emitted) : null, warnings);
        }

        internal static string DefaultRoslynDirectory() =>
            Path.Combine(EditorApplication.applicationContentsPath, "MonoBleedingEdge", "lib", "mono", "4.5");

        internal struct Diagnostic
        {
            public string Severity;
            public ScriptCompilationError Error;
        }

        private sealed class RoslynApi
        {
            private MethodInfo _parse;
            private object _parseOptions;
            private MethodInfo _createReference;
            private Type _referenceListType;
            private Type _syntaxTreeType;
            private object _compilationOptions;
            private MethodInfo _create;

            public static RoslynApi Load(string directory)
            {
                foreach (var name in OwnAssemblies)
                {
                    if (!File.Exists(Path.Combine(directory, name + ".dll")))
                        throw new FileNotFoundException($"Roslyn assembly not found: {Path.Combine(directory, name + ".dll")}");
                }

                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                {
                    var requested = new AssemblyName(args.Name).Name;
                    return OwnAssemblies.Contains(requested)
                        ? Assembly.LoadFrom(Path.Combine(directory, requested + ".dll"))
                        : null;
                };

                var core = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.CodeAnalysis.dll"));
                var csharp = Assembly.LoadFrom(Path.Combine(directory, "Microsoft.CodeAnalysis.CSharp.dll"));

                var api = new RoslynApi();
                var parseOptionsType = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpParseOptions", true);
                api._parse = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree", true).GetMethods()
                    .Single(m => m.Name == "ParseText" && m.GetParameters().Length == 5 &&
                                 m.GetParameters()[0].ParameterType == typeof(string) &&
                                 m.GetParameters()[1].ParameterType == parseOptionsType);
                // Preview matches csc's -langversion:preview, so the C# 9 that Roslyn 3.7 knows compiles here.
                api._parseOptions = Invoke(parseOptionsType.GetConstructors().Single(c => c.GetParameters().Length == 4), null,
                    ("languageVersion", Enum.Parse(csharp.GetType("Microsoft.CodeAnalysis.CSharp.LanguageVersion", true), "Preview")));

                var referenceType = core.GetType("Microsoft.CodeAnalysis.MetadataReference", true);
                api._createReference = referenceType.GetMethods().Single(m => m.Name == "CreateFromFile");
                api._referenceListType = typeof(List<>).MakeGenericType(referenceType);
                api._syntaxTreeType = core.GetType("Microsoft.CodeAnalysis.SyntaxTree", true);

                var optionsType = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions", true);
                api._compilationOptions = Invoke(
                    optionsType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First(), null,
                    ("outputKind", Enum.Parse(core.GetType("Microsoft.CodeAnalysis.OutputKind", true), "DynamicallyLinkedLibrary")));
                api._create = csharp.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation", true).GetMethods()
                    .Single(m => m.Name == "Create" && m.GetParameters().Length == 4);
                return api;
            }

            public IList NewReferenceList() => (IList)Activator.CreateInstance(_referenceListType);

            public object CreateReference(string path) => Invoke(_createReference, null, ("path", path));

            public byte[] Compile(string code, IList references, CancellationToken cancellation, out List<Diagnostic> diagnostics)
            {
                var tree = Invoke(_parse, null, ("text", code), ("options", _parseOptions), ("cancellationToken", cancellation));
                var trees = Array.CreateInstance(_syntaxTreeType, 1);
                trees.SetValue(tree, 0);
                var compilation = Invoke(_create, null,
                    ("assemblyName", "Snippet"), ("syntaxTrees", trees), ("references", references), ("options", _compilationOptions));

                var emit = compilation.GetType().GetMethods()
                    .Where(m => m.Name == "Emit" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(Stream))
                    .OrderByDescending(m => m.GetParameters().Length)
                    .First();

                using (var peStream = new MemoryStream())
                {
                    var result = Invoke(emit, compilation, ("peStream", peStream), ("cancellationToken", cancellation));
                    diagnostics = ReadDiagnostics((IEnumerable)result.GetType().GetProperty("Diagnostics").GetValue(result));
                    return (bool)result.GetType().GetProperty("Success").GetValue(result) ? peStream.ToArray() : null;
                }
            }

            private static List<Diagnostic> ReadDiagnostics(IEnumerable diagnostics)
            {
                var list = new List<Diagnostic>();
                foreach (var diagnostic in diagnostics)
                {
                    var type = diagnostic.GetType();
                    var location = type.GetProperty("Location").GetValue(diagnostic);
                    var line = 0;
                    var column = 0;
                    if ((bool)location.GetType().GetProperty("IsInSource").GetValue(location))
                    {
                        var span = location.GetType().GetMethod("GetLineSpan", Type.EmptyTypes).Invoke(location, null);
                        var start = span.GetType().GetProperty("StartLinePosition").GetValue(span);
                        line = (int)start.GetType().GetProperty("Line").GetValue(start) + 1;
                        column = (int)start.GetType().GetProperty("Character").GetValue(start) + 1;
                    }

                    list.Add(new Diagnostic
                    {
                        Severity = type.GetProperty("Severity").GetValue(diagnostic).ToString(),
                        Error = new ScriptCompilationError
                        {
                            line = line,
                            column = column,
                            code = (string)type.GetProperty("Id").GetValue(diagnostic),
                            text = (string)type.GetMethod("GetMessage", new[] { typeof(IFormatProvider) }).Invoke(diagnostic, new object[] { null })
                        }
                    });
                }

                return list;
            }

            // Roslyn's factories take long lists of optional parameters; name the ones that matter, default the rest.
            private static object Invoke(MethodBase method, object target, params (string name, object value)[] arguments)
            {
                var parameters = method.GetParameters();
                var values = new object[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    var given = arguments.FirstOrDefault(a => a.name == parameters[i].Name);
                    if (given.name != null)
                        values[i] = given.value;
                    else if (parameters[i].HasDefaultValue)
                        values[i] = parameters[i].DefaultValue;
                    else if (parameters[i].ParameterType.IsValueType)
                        values[i] = Activator.CreateInstance(parameters[i].ParameterType);
                }

                return method is ConstructorInfo constructor ? constructor.Invoke(values) : method.Invoke(target, values);
            }
        }
    }
}
