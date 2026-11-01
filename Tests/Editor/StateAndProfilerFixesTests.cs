// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class StateAndProfilerFixesTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly List<string> _addedSortingLayers = new List<string>();
        private string _sortingLayersBefore;
        private UnityEngine.Object[] _selectionBefore;
        private bool _profilerEnabledBefore;

        [SetUp]
        public void RememberEditorState()
        {
            _sortingLayersBefore = SortingLayerSnapshot();
            _selectionBefore = Selection.objects;
            _profilerEnabledBefore = Profiler.enabled;
        }

        [TearDown]
        public void RestoreEditorState()
        {
            foreach (var go in _made)
            {
                if (go != null)
                    UnityEngine.Object.DestroyImmediate(go);
            }
            _made.Clear();

            foreach (var name in _addedSortingLayers)
                Call("remove_sorting_layer", "name", name);
            _addedSortingLayers.Clear();

            ProfilerFunctions.ProfilerStop();
            Profiler.enabled = _profilerEnabledBefore;
            Selection.objects = _selectionBefore;

            Assert.AreEqual(_sortingLayersBefore, SortingLayerSnapshot(), "The test left the sorting layer list changed.");
        }

        private static string Unique(string stem) => stem + "_" + System.Guid.NewGuid().ToString("N");

        private GameObject Make(string name)
        {
            var go = new GameObject(name);
            _made.Add(go);
            return go;
        }

        private static string SortingLayerSnapshot() =>
            string.Join("|", ((JArray)Call("get_sorting_layers")["data"]).Select(l => $"{l["name"]}:{l["id"]}"));

        private JObject AddSortingLayer(string name)
        {
            var answer = Call("add_sorting_layer", "name", name);
            if ((bool)answer["success"])
                _addedSortingLayers.Add(name);
            return answer;
        }

        private static IEnumerable<string> HashedNames(string stem) =>
            Enumerable.Range(0, 1000).Select(i => stem + i);

        [Test]
        public void AddSortingLayer_GivesPositiveUniqueIdsEvenWhenTheNameHashIsNegative()
        {
            var names = HashedNames("KwSortId").Where(n => n.GetHashCode() < 0).Take(4)
                .Concat(HashedNames("KwSortId").Where(n => n.GetHashCode() >= 0).Take(2))
                .ToList();
            Assert.AreEqual(6, names.Count);

            foreach (var name in names)
            {
                var added = AddSortingLayer(name);
                Assert.IsTrue((bool)added["success"], added.ToString());
                Assert.That((uint)added["data"]["id"], Is.GreaterThan(0u), name);
            }

            var layers = (JArray)Call("get_sorting_layers")["data"];
            CollectionAssert.AllItemsAreUnique(layers.Select(l => (uint)l["id"]).ToList());
            foreach (var name in names)
                Assert.That((uint)layers.Single(l => (string)l["name"] == name)["id"], Is.GreaterThan(0u), name);
        }

        [Test]
        public void NextSortingLayerId_IsPositiveAndSkipsIdsAlreadyTaken()
        {
            var negativeName = HashedNames("KwHash").First(n => n.GetHashCode() < 0);
            var used = new HashSet<uint> { 0u };

            for (var i = 0; i < 5; i++)
            {
                var id = EditorStateFunctions.NextSortingLayerId(negativeName, used);
                Assert.That(id, Is.GreaterThan(0u));
                Assert.That(id, Is.LessThanOrEqualTo((uint)int.MaxValue));
                Assert.IsTrue(used.Add(id), "An id that is already taken was handed out again.");
            }
        }

        [Test]
        public void SortingLayers_RefuseADuplicateNameOnAddAndOnRename()
        {
            const string first = "KwDupSortA";
            const string second = "KwDupSortB";
            Assert.IsTrue((bool)AddSortingLayer(first)["success"]);
            Assert.IsTrue((bool)AddSortingLayer(second)["success"]);
            var before = SortingLayerSnapshot();

            Assert.AreEqual("SORTING_LAYER_EXISTS", Code("add_sorting_layer", "name", first));
            Assert.AreEqual("SORTING_LAYER_EXISTS", Code("add_sorting_layer", "name", "Default"));
            Assert.AreEqual("SORTING_LAYER_EXISTS", Code("rename_sorting_layer", "old_name", first, "new_name", second));
            Assert.AreEqual("SORTING_LAYER_EXISTS", Code("rename_sorting_layer", "old_name", first, "new_name", "Default"));
            Assert.AreEqual(before, SortingLayerSnapshot());
        }

        [Test]
        public void RenameSortingLayer_ChecksTheOldNameFirstAndTreatsTheSameNameAsNothingToDo()
        {
            const string layer = "KwRenameSortA";
            Assert.IsTrue((bool)AddSortingLayer(layer)["success"]);
            var before = SortingLayerSnapshot();

            Assert.AreEqual("SORTING_LAYER_NOT_FOUND",
                Code("rename_sorting_layer", "old_name", Unique("KwNoSuchSort"), "new_name", layer));
            Assert.AreEqual("SORTING_LAYER_NOT_FOUND",
                Code("rename_sorting_layer", "old_name", Unique("KwNoSuchSort"), "new_name", "Default"));

            var same = Ok("rename_sorting_layer", "old_name", layer, "new_name", layer);
            StringAssert.Contains("nothing changed", (string)same["message"]);
            Assert.AreEqual(before, SortingLayerSnapshot());
        }

        [Test]
        public void GetSortingLayers_WarnsAboutALayerThatSharesDefaultsId()
        {
            const string layer = "KwLegacyIdSort";
            Assert.IsTrue((bool)AddSortingLayer(layer)["success"]);

            var tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var layers = tagManager.FindProperty("m_SortingLayers");
            for (var i = 0; i < layers.arraySize; i++)
            {
                var element = layers.GetArrayElementAtIndex(i);
                if (element.FindPropertyRelative("name").stringValue == layer)
                    element.FindPropertyRelative("uniqueID").uintValue = 0;
            }
            tagManager.ApplyModifiedProperties();

            var listed = ((JArray)Call("get_sorting_layers")["data"]).Single(l => (string)l["name"] == layer);
            Assert.AreEqual(0, (int)listed["id"]);
            StringAssert.Contains("Default", (string)listed["warning"]);

            var healthy = ((JArray)Call("get_sorting_layers")["data"]).Single(l => (string)l["name"] == "Default");
            Assert.IsNull(healthy["warning"]);
        }

        [Test]
        public void SetSelection_AMissIsAnErrorAndLeavesTheSelectionAlone()
        {
            var kept = Make(Unique("KwSelKept"));
            Selection.objects = new UnityEngine.Object[] { kept };

            var ghost = Unique("KwSelGhost");
            var missed = Refused("set_selection", "targets", ghost, "find_method", "by_name");

            Assert.AreEqual("NO_TARGETS_RESOLVED", (string)missed["code"]);
            StringAssert.Contains(ghost, missed["data"]["notFound"].ToString());
            CollectionAssert.AreEqual(new UnityEngine.Object[] { kept }, Selection.objects);
        }

        [Test]
        public void SetSelection_CountsADuplicateTokenOnceAndReportsTheUnresolvedOne()
        {
            var go = Make(Unique("KwSelDup"));
            var ghost = Unique("KwSelGhost");

            var answer = Ok("set_selection", "targets", $"{go.name}, {go.name},{ghost},{ghost}", "find_method", "by_name");

            Assert.AreEqual(1, ((JArray)answer["data"]["selected"]).Count, answer.ToString());
            Assert.AreEqual(1, ((JArray)answer["data"]["notFound"]).Count, answer.ToString());
            StringAssert.Contains("Selected 1 object(s)", (string)answer["message"]);
            Assert.AreEqual(1, Selection.objects.Length);
        }

        [Test]
        public void ProfilerStart_StartsEveryValidRecorder()
        {
            StringAssert.Contains("Recorders running:", ProfilerFunctions.ProfilerStart());

            var status = ProfilerFunctions.ProfilerStatus();
            StringAssert.Contains("Session running (profiler_start): True", status);
            var valid = status.Split('\n').Where(l => l.Contains("valid=True")).ToList();
            Assert.IsNotEmpty(valid, status);
            foreach (var line in valid)
                StringAssert.Contains("running=True", line);
        }

        [Test]
        public void GetCounters_WithoutAStartedSessionLeavesNoRecorderAlive()
        {
            ProfilerFunctions.ProfilerStop();

            var counters = ProfilerFunctions.GetCounters();

            StringAssert.Contains("profiler_start was not called", counters);
            var status = ProfilerFunctions.ProfilerStatus();
            StringAssert.Contains("Active recorders: 0", status);
            StringAssert.Contains("Session running (profiler_start): False", status);
        }

        [Test]
        public void GetCounters_WithoutAStartedSessionReadsOnlyTheMemoryCounters()
        {
            ProfilerFunctions.ProfilerStop();

            var counters = ProfilerFunctions.GetCounters();

            StringAssert.Contains("Memory/", counters);
            StringAssert.DoesNotContain("Render/", counters);
            StringAssert.Contains("UNKNOWN_COUNTERS", ProfilerFunctions.GetCounters("Draw Calls Count"));
        }

        [Test]
        public void ProfilerStop_AfterADomainReloadStillTurnsOffTheProfilerItStarted()
        {
            ProfilerFunctions.ProfilerStart();
            var recorders = typeof(ProfilerFunctions).GetField("_recorders", BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var recorder in ((Dictionary<string, Unity.Profiling.ProfilerRecorder>)recorders.GetValue(null)).Values)
                recorder.Dispose();
            recorders.SetValue(null, null);

            var answer = ProfilerFunctions.ProfilerStop();

            Assert.IsFalse(Profiler.enabled, "the recorders were lost to a reload, but the Profiler it enabled is still on");
            StringAssert.Contains("Profiler stopped", answer);
        }

        [Test]
        public void ProfilerStop_WithoutASessionClaimsNothingAndLeavesTheProfilerOn()
        {
            ProfilerFunctions.ProfilerStop();
            Profiler.enabled = true;

            var answer = ProfilerFunctions.ProfilerStop();

            Assert.IsTrue(Profiler.enabled, "profiler_stop must not turn off a profiler it never started.");
            StringAssert.Contains("nothing to stop", answer);
            StringAssert.DoesNotContain("Profiler stopped", answer);
        }

        [Test]
        public void GetObjectMemory_AnAmbiguousTargetReachesTheInvokerAsAmbiguousTarget()
        {
            var name = Unique("KwAmbiguousMemory");
            Make(name);
            Make(name);

            var answer = Refused("get_object_memory", "target", name);

            Assert.AreEqual("AMBIGUOUS_TARGET", (string)answer["code"], answer.ToString());
        }
    }
}
