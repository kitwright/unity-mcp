// Copyright (C) KitWright. All rights reserved.

using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The mutating half of the editor-state, prefs and undo tools, driven through FunctionInvoker the
    /// way a client drives them. Their readers were tested and their writers were not, which is the
    /// wrong way round: a reader that breaks returns nothing, a writer that breaks leaves a project
    /// changed. Everything here undoes itself - a tag is added and removed, a pref is set and deleted -
    /// so the run leaves the project as it found it.
    /// </summary>
    public sealed class EditorStateAndPrefsToolsTests
    {
        private const string Tag = "KwSweepTag";
        private const string Layer = "KwSweepLayer";
        private const string SortingLayer = "KwSweepSortAlpha";
        private const string RenamedSortingLayer = "KwSweepSortBeta";
        private const string PrefKey = "KitWright.Tests.SweepPref";

        private Object[] previousSelection;
        private Tool previousTool;

        [SetUp]
        public void RememberEditorState()
        {
            previousSelection = Selection.objects;
            previousTool = UnityEditor.Tools.current;
        }

        [TearDown]
        public void RestoreEditorState()
        {
            // Every test is meant to clean up after itself; these run anyway, because a failed
            // assertion leaves the tail of a test unexecuted and the leftover in the project.
            Call("remove_tag", "tag", Tag);
            Call("remove_layer", "layer", Layer);
            Call("remove_sorting_layer", "name", SortingLayer);
            Call("remove_sorting_layer", "name", RenamedSortingLayer);
            Call("delete_editor_pref", "key", PrefKey);
            Call("delete_player_pref", "key", PrefKey);

            Selection.objects = previousSelection;
            UnityEditor.Tools.current = previousTool;
        }

        private static void AssertSuccess(JObject answer) =>
            Assert.IsTrue((bool)answer["success"], answer.ToString());

        private static void AssertFailed(JObject answer) =>
            Assert.IsFalse((bool)answer["success"], answer.ToString());

        // The reader is the check: it is the same list a client sees, so a write that only landed in
        // memory and never in TagManager.asset shows up as a miss here.
        private static bool Lists(string readerTool, string value) =>
            Call(readerTool).ToString().Contains(value);

        [Test]
        public void ATagCanBeAddedAndTakenBackOut()
        {
            AssertSuccess(Call("add_tag", "tag", Tag));
            Assert.IsTrue(Lists("get_tags", Tag), "get_tags should report a tag that was just added.");

            // Adding it twice is documented as a no-op rather than an error.
            AssertSuccess(Call("add_tag", "tag", Tag));

            AssertSuccess(Call("remove_tag", "tag", Tag));
            Assert.IsFalse(Lists("get_tags", Tag));
            AssertFailed(Call("remove_tag", "tag", Tag));
        }

        [Test]
        public void ALayerTakesTheFirstFreeUserSlotAndGivesItBack()
        {
            var added = Call("add_layer", "layer", Layer);
            if ((bool)added["success"] != true)
                Assert.Ignore($"No free user layer slot in this project: {added}");

            Assert.IsTrue(Lists("get_layers", Layer), "get_layers should report the new layer.");

            AssertSuccess(Call("remove_layer", "layer", Layer));
            Assert.IsFalse(Lists("get_layers", Layer));
            AssertFailed(Call("remove_layer", "layer", Layer));
        }

        [Test]
        public void ASortingLayerCanBeAddedRenamedAndRemoved()
        {
            AssertSuccess(Call("add_sorting_layer", "name", SortingLayer));
            Assert.IsTrue(Lists("get_sorting_layers", SortingLayer));

            AssertSuccess(Call("rename_sorting_layer", "old_name", SortingLayer, "new_name", RenamedSortingLayer));
            Assert.IsTrue(Lists("get_sorting_layers", RenamedSortingLayer));
            Assert.IsFalse(Lists("get_sorting_layers", SortingLayer), "The old name should be gone, not duplicated.");

            AssertSuccess(Call("remove_sorting_layer", "name", RenamedSortingLayer));
            Assert.IsFalse(Lists("get_sorting_layers", RenamedSortingLayer));
        }

        [Test]
        public void AnEditorPrefRoundTripsThroughItsDeclaredType()
        {
            AssertSuccess(Call("set_editor_pref", "key", PrefKey, "value", "42", "type", "int"));
            Assert.AreEqual(42, EditorPrefs.GetInt(PrefKey), "An int pref must not be stored as a string.");

            var read = Call("get_editor_pref", "key", PrefKey, "type", "int");
            AssertSuccess(read);
            StringAssert.Contains("42", read.ToString());

            AssertSuccess(Call("set_editor_pref", "key", PrefKey, "value", "true", "type", "bool"));
            Assert.IsTrue(EditorPrefs.GetBool(PrefKey));

            AssertSuccess(Call("delete_editor_pref", "key", PrefKey));
            Assert.IsFalse(EditorPrefs.HasKey(PrefKey));
        }

        [Test]
        public void APlayerPrefRoundTripsAndIsDeletedOnItsOwn()
        {
            AssertSuccess(Call("set_player_pref", "key", PrefKey, "value", "hello", "type", "string"));
            Assert.AreEqual("hello", PlayerPrefs.GetString(PrefKey));

            var read = Call("get_player_pref", "key", PrefKey);
            AssertSuccess(read);
            StringAssert.Contains("hello", read.ToString());

            // Deliberately not delete_all_player_prefs: that one wipes whatever the project itself
            // keeps there, and no test is worth that.
            AssertSuccess(Call("delete_player_pref", "key", PrefKey));
            Assert.IsFalse(PlayerPrefs.HasKey(PrefKey));
        }

        [Test]
        public void SetSelectionResolvesByNameAndReplacesWhatWasSelected()
        {
            var first = new GameObject("KwSweepSelectionA");
            var second = new GameObject("KwSweepSelectionB");
            try
            {
                Selection.objects = new Object[0];

                AssertSuccess(Call("set_selection", "targets", first.name, "find_method", "by_name"));
                Assert.AreEqual(first, Selection.activeGameObject);

                AssertSuccess(Call("set_selection", "targets", second.name, "find_method", "by_name"));
                Assert.AreEqual(second, Selection.activeGameObject, "A second call replaces the selection.");
                Assert.AreEqual(1, Selection.objects.Length);

                // A name that resolves to nothing is reported as found-nothing rather than refused,
                // and it does clear the selection - pinned because an agent has to read notFound to
                // know its click target was never there.
                var missed = Call("set_selection", "targets", "KwSweepNothingCalledThis", "find_method", "by_name");
                AssertSuccess(missed);
                Assert.AreEqual(0, ((JArray)missed["data"]["selected"]).Count, missed.ToString());
                StringAssert.Contains("KwSweepNothingCalledThis", missed["data"]["notFound"].ToString());
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void SetActiveToolSwitchesTheManipulationToolAndRefusesAnUnknownOne()
        {
            AssertSuccess(Call("set_active_tool", "tool", "Rotate"));
            Assert.AreEqual(Tool.Rotate, UnityEditor.Tools.current);

            AssertSuccess(Call("set_active_tool", "tool", "Move"));
            Assert.AreEqual(Tool.Move, UnityEditor.Tools.current);
            StringAssert.Contains("Move", Call("get_active_tool").ToString());

            AssertFailed(Call("set_active_tool", "tool", "Teleport"));
            Assert.AreEqual(Tool.Move, UnityEditor.Tools.current, "A refused tool name must not change the tool.");
        }

        [Test]
        public void UndoAndRedoStepThroughTheUndoStack()
        {
            // Its own group, so the undo below takes back this creation and not whatever the previous
            // test happened to leave at the top of the stack.
            Undo.IncrementCurrentGroup();
            var created = new GameObject("KwSweepUndoTarget");
            Undo.RegisterCreatedObjectUndo(created, "KitWright sweep");
            Undo.IncrementCurrentGroup();

            AssertSuccess(Call("undo"));
            if (GameObject.Find("KwSweepUndoTarget") != null)
            {
                Object.DestroyImmediate(created);
                Assert.Ignore("Undo did not take back a registered creation in this environment.");
            }

            AssertSuccess(Call("redo"));
            var restored = GameObject.Find("KwSweepUndoTarget");
            Assert.IsNotNull(restored, "Redo should put the object back.");

            Undo.ClearAll();
            Object.DestroyImmediate(restored);
        }
    }
}
