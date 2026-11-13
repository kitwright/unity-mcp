// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Interop;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class HotReloadInteropTests
    {
        [Test]
        public void Suppression_NeedsThePluginLoaded()
        {
            Assert.IsFalse(HotReload.ResolveSuppression(false, true, true));
        }

        // The case that used to be wrong: the server is up but the user left
        // disableCompilingFromEditorScripts off, so nothing is detoured and Unity still compiles.
        [Test]
        public void Suppression_FollowsTheDetourNotTheServerHealth()
        {
            Assert.IsFalse(HotReload.ResolveSuppression(true, false, true));
            Assert.IsTrue(HotReload.ResolveSuppression(true, true, false));
        }

        [Test]
        public void Suppression_FallsBackToServerHealthWhenTheDetourIsUnreadable()
        {
            Assert.IsTrue(HotReload.ResolveSuppression(true, null, true));
            Assert.IsFalse(HotReload.ResolveSuppression(true, null, false));
            Assert.IsTrue(HotReload.ResolveSuppression(true, null, null));
        }

        private static readonly DateTime Edit = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

        private static HotReload.TimelineEntry Entry(string type, int secondsAfterEdit, string role = "Parent", string error = null) =>
            new HotReload.TimelineEntry { Type = type, Role = role, Error = error, CreatedAtUtc = Edit.AddSeconds(secondsAfterEdit) };

        [TestCase("PatchApplied", "applied")]
        [TestCase("PartiallySupportedChange", "partial")]
        [TestCase("Failure", "not_applied")]
        [TestCase("UndetectedChange", "no_change")]
        [TestCase("InlinedMethod", "inlined")]
        public void Outcome_IsTheNewestParentEntryAfterTheEdit(string type, string verdict)
        {
            var newestFirst = new[] { Entry(type, 2), Entry("PatchApplied", -30) };

            Assert.AreEqual(verdict, HotReload.ClassifyOutcome(newestFirst, Edit));
        }

        [Test]
        public void Outcome_IgnoresEntriesFromBeforeTheEdit_AndWaitsForOne()
        {
            Assert.IsNull(HotReload.ClassifyOutcome(new[] { Entry("PatchApplied", -1) }, Edit));
            Assert.IsNull(HotReload.ClassifyOutcome(new HotReload.TimelineEntry[0], Edit));
        }

        // A failed patch writes Child details under a Parent outcome; the details must not decide it.
        [Test]
        public void Outcome_SkipsChildDetails()
        {
            var newestFirst = new[] { Entry("Failure", 3, role: "Child"), Entry("PartiallySupportedChange", 2) };

            Assert.AreEqual("partial", HotReload.ClassifyOutcome(newestFirst, Edit));
        }

        // A compile error gets Error entries and no outcome entry; an Error without "error CS" is an unsupported change.
        [Test]
        public void Outcome_IsACompileErrorOnlyWhenAnErrorQuotesCsc()
        {
            var compileError = new[] { Entry("Error", 1, role: "Standalone", error: "Assets/A.cs(3,1): error CS1002: ; expected") };
            var unsupported = new[] { Entry("Error", 1, role: "Child", error: "Adding a field is not supported"), Entry("Failure", 1) };

            Assert.AreEqual("compile_error", HotReload.ClassifyOutcome(compileError, Edit));
            Assert.AreEqual("not_applied", HotReload.ClassifyOutcome(unsupported, Edit));
        }

        [Test]
        public void PersistedEntries_NameTheEnumsThePluginWritesAsNumbers()
        {
            var entries = HotReload.ParsePersistedEntries(
                "{\"alertDatas\":[{\"alertEntryType\":3,\"entryType\":2,\"createdAt\":\"2026-11-10T09:00:05Z\",\"patchedMembersDisplayNames\":[\"Player.Jump\"]}," +
                "{\"alertEntryType\":1,\"entryType\":1,\"errorString\":\"boom\",\"methodName\":\"Player.Run\"}]}");

            Assert.AreEqual("PatchApplied", entries[0].Type);
            Assert.AreEqual("Standalone", entries[0].Role);
            Assert.AreEqual(Edit.AddSeconds(5), entries[0].CreatedAtUtc);
            Assert.AreEqual("Player.Jump", entries[0].PatchedMembers.Single());
            Assert.AreEqual("Failure", entries[1].Type);
            Assert.AreEqual("Child", entries[1].Role);
            Assert.AreEqual("Player.Run", entries[1].Method);
            Assert.AreEqual("applied", HotReload.ClassifyOutcome(entries, Edit));
        }

        [Test]
        public void ScriptChangeState_KeepsTheNewestPendingWriteTime()
        {
            var state = new ScriptChangeState();
            Assert.IsNull(state.NewestChangeUtc);

            state.AddOutOfDateSource("Assets/A.cs", Edit);
            state.AddOutOfDateSource("Assets/B.cs", null);
            state.AddUnknownProjectScript("Assets/C.cs", Edit.AddSeconds(-5));

            Assert.AreEqual(Edit, state.NewestChangeUtc);
        }

        // Canary: the detour flag is a private field of a third-party package, so a plugin update
        // that renames it silently drops us back to guessing from server health.
        [Test]
        public void DetourFieldIsStillWhereWeReflectOnIt()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, HotReload.EditorAssembly, StringComparison.OrdinalIgnoreCase))
                ?.GetType(HotReload.DetourerType, false);

            if (type == null)
                Assert.Ignore("SingularityGroup Hot Reload is not installed in this project.");

            var field = type.GetField(HotReload.DetourField, BindingFlags.NonPublic | BindingFlags.Static);

            Assert.IsNotNull(field, $"{HotReload.DetourerType}.{HotReload.DetourField} is gone — HotReload.IsSuppressingCompilation is back to guessing.");
            Assert.AreEqual(typeof(bool), field.FieldType);
        }

        [Test]
        public void EnginePatches_ShowInRecentActivityWithTheirSeverity()
        {
            Assert.AreEqual(MCPToolCallStatus.Success, KitWrightEngine.StatusOf("applied"));
            Assert.AreEqual(MCPToolCallStatus.Success, KitWrightEngine.StatusOf("no_change"));
            Assert.AreEqual(MCPToolCallStatus.Interrupted, KitWrightEngine.StatusOf("partial"));
            Assert.AreEqual(MCPToolCallStatus.Interrupted, KitWrightEngine.StatusOf("inlined"));
            Assert.AreEqual(MCPToolCallStatus.Error, KitWrightEngine.StatusOf("compile_error"));
            Assert.AreEqual(MCPToolCallStatus.Error, KitWrightEngine.StatusOf("not_applied"));
        }

        // Canary for what request_recompile's verdict reads: a renamed member turns every verdict into
        // HOT_RELOAD_NO_OUTCOME, or stops the full recompile it falls back to.
        [Test]
        public void VerdictMembersAreStillWhereWeReflectOnThem()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, HotReload.EditorAssembly, StringComparison.OrdinalIgnoreCase));
            if (assembly == null)
                Assert.Ignore("SingularityGroup Hot Reload is not installed in this project.");

            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            Type Find(string name) => assembly.GetType("SingularityGroup.HotReload.Editor." + name, false);

            Assert.IsNotNull(Find("HotReloadTimelineHelper")?.GetProperty("EventsTimeline", any), "HotReloadTimelineHelper.EventsTimeline");
            Assert.IsNotNull(Find("AlertEntry")?.GetField("alertData", any), "AlertEntry.alertData");
            foreach (var field in new[] { "alertEntryType", "entryType", "createdAt", "errorString", "methodName", "patchedMembersDisplayNames" })
                Assert.IsNotNull(Find("AlertData")?.GetField(field, any), "AlertData." + field);
            Assert.IsNotNull(Find("EditorCodePatcher")?.GetField("patchStatus", any), "EditorCodePatcher.patchStatus");
            Assert.IsNotNull(Find("EditorCodePatcher")?.GetField("config", any), "EditorCodePatcher.config");
            Assert.IsNotNull(Find("Config")?.GetField("patchEditModeOnlyOnEditorFocus", any), "Config.patchEditModeOnlyOnEditorFocus");
            Assert.IsNotNull(Find("HotReloadRunTab")?.GetMethod("Recompile", any, null, Type.EmptyTypes, null), "HotReloadRunTab.Recompile()");
        }
    }
}
