// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEditor;

namespace KitWright.Editor.Tests
{
    public sealed class EditorRefreshPipelineTests
    {
        [Test]
        public void AnalyzeScriptChangeState_SourceNewerThanAssembly_IsPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Assembly-CSharp.dll");
                var source = Path.Combine(temp, "Example.cs");
                File.WriteAllText(output, "compiled");
                File.WriteAllText(source, "class Example {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));

                var state = EditorRefreshPipeline.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { source }) },
                    Array.Empty<string>(),
                    TimeSpan.FromSeconds(1));

                Assert.IsTrue(state.HasPendingScriptChanges);
                Assert.AreEqual(1, state.OutOfDateSourceCount);
                Assert.AreEqual(0, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void AnalyzeScriptChangeState_UnknownProjectScriptNewerThanAssembly_IsPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Assembly-CSharp.dll");
                var knownSource = Path.Combine(temp, "Known.cs");
                var newSource = Path.Combine(temp, "NewBehaviour.cs");
                File.WriteAllText(output, "compiled");
                File.WriteAllText(knownSource, "class Known {}");
                File.WriteAllText(newSource, "class NewBehaviour {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(knownSource, new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(newSource, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));

                var state = EditorRefreshPipeline.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { knownSource }) },
                    new[] { knownSource, newSource },
                    TimeSpan.FromSeconds(1));

                Assert.IsTrue(state.HasPendingScriptChanges);
                Assert.AreEqual(0, state.OutOfDateSourceCount);
                Assert.AreEqual(1, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void AnalyzeScriptChangeState_UpToDateSources_AreNotPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Assembly-CSharp.dll");
                var source = Path.Combine(temp, "Example.cs");
                var oldUnknownSource = Path.Combine(temp, "IgnoredOldScript.cs");
                File.WriteAllText(output, "compiled");
                File.WriteAllText(source, "class Example {}");
                File.WriteAllText(oldUnknownSource, "class IgnoredOldScript {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(oldUnknownSource, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

                var state = EditorRefreshPipeline.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { source }) },
                    new[] { source, oldUnknownSource },
                    TimeSpan.FromSeconds(1));

                Assert.IsFalse(state.HasPendingScriptChanges);
                Assert.AreEqual(0, state.OutOfDateSourceCount);
                Assert.AreEqual(0, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void AnalyzeScriptChangeState_ResolvedOutputNewerThanSource_IsNotPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var staleOutput = Path.Combine(temp, "ScriptAssemblies", "KitWright.Editor.dll");
                var source = Path.Combine(temp, "MCPServerService.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(staleOutput));
                File.WriteAllText(staleOutput, "old compiled copy");
                File.WriteAllText(source, "class MCPServerService {}");
                File.SetLastWriteTimeUtc(staleOutput, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));

                var resolvedOutputTime = new DateTime(2026, 1, 1, 0, 0, 10, DateTimeKind.Utc);
                var state = EditorRefreshPipeline.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(staleOutput, new[] { source }, resolvedOutputTime) },
                    Array.Empty<string>(),
                    TimeSpan.FromSeconds(1));

                Assert.IsFalse(state.HasPendingScriptChanges);
                Assert.AreEqual(0, state.OutOfDateSourceCount);
                Assert.AreEqual(0, state.UnknownProjectScriptCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void AnalyzeScriptChangeState_PackageCacheSourceNewerThanAssembly_IsNotPending()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Zego.Advertisement.Max.dll");
                var source = Path.Combine(temp, "Library", "PackageCache", "com.vendor.sdk@abc123", "SdkVersion.generated.cs");
                Directory.CreateDirectory(Path.GetDirectoryName(source));
                File.WriteAllText(output, "compiled");
                File.WriteAllText(source, "class SdkVersion {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc));

                var state = EditorRefreshPipeline.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { source }) },
                    Array.Empty<string>(),
                    TimeSpan.FromSeconds(1));

                Assert.IsFalse(state.HasPendingScriptChanges,
                    "A regenerated PackageCache source must not force a recompile, or every refresh escalates and regenerates it again.");
                Assert.AreEqual(0, state.OutOfDateSourceCount);
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        [Test]
        public void RefreshAndRequestCompilation_NothingStale_SkipsCompileStartDetection()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                Assert.Ignore("Editor is busy, so compile-start detection short-circuits regardless.");

            var task = EditorRefreshPipeline.RefreshAndRequestCompilationAsync(forceUpdate: false);

            if (task.IsCompleted && task.Result.LatestScriptState.HasPendingScriptChanges)
                Assert.Ignore("Project has genuinely stale scripts, so waiting for a compile start is correct here.");

            Assert.IsTrue(task.IsCompleted,
                "With nothing stale and an idle editor there is no compilation to wait for, so the refresh must not burn the detection timeout.");
            Assert.IsFalse(task.Result.ScriptChangesStillPending);
        }

        [Test]
        public void AnnotatePendingScriptChanges_AnnouncesStaleSourcesAndPointsAtRequestRecompile()
        {
            var temp = CreateTempDirectory();
            try
            {
                var output = Path.Combine(temp, "Assembly-CSharp.dll");
                var source = Path.Combine(temp, "Example.cs");
                File.WriteAllText(output, "compiled");
                File.WriteAllText(source, "class Example {}");
                File.SetLastWriteTimeUtc(output, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                File.SetLastWriteTimeUtc(source, new DateTime(2026, 1, 1, 0, 0, 5, DateTimeKind.Utc));

                var pending = EditorRefreshPipeline.AnalyzeScriptChangeState(
                    new[] { new ScriptCompilationArtifact(output, new[] { source }) },
                    Array.Empty<string>(),
                    TimeSpan.FromSeconds(1));

                var annotated = EditorRefreshPipeline.AnnotatePendingScriptChanges(
                    pending, "No compilation errors detected.");
                StringAssert.Contains("1 script file(s)", annotated);
                StringAssert.Contains("request_recompile", annotated);
                StringAssert.Contains("No compilation errors detected.", annotated);

                Assert.AreEqual(
                    "No compilation errors detected.",
                    EditorRefreshPipeline.AnnotatePendingScriptChanges(
                        new ScriptChangeState(), "No compilation errors detected."));
            }
            finally
            {
                DeleteTempDirectory(temp);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "KitWrightEditorRefreshTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTempDirectory(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
    }
}
