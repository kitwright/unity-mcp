// Copyright (C) KitWright. Licensed under MIT.

using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class PrefsFunctionsTests
    {
        [Test]
        public void ResolvePrefType_KnownTypes()
        {
            Assert.AreEqual("int", PrefsFunctions.ResolvePrefType("int"));
            Assert.AreEqual("float", PrefsFunctions.ResolvePrefType("float"));
            Assert.AreEqual("bool", PrefsFunctions.ResolvePrefType("bool"));
            Assert.AreEqual("string", PrefsFunctions.ResolvePrefType("string"));
        }

        [Test]
        public void ResolvePrefType_CaseInsensitive()
        {
            Assert.AreEqual("int", PrefsFunctions.ResolvePrefType("INT"));
        }

        [Test]
        public void ResolvePrefType_UnknownReturnsAuto()
        {
            Assert.AreEqual("auto", PrefsFunctions.ResolvePrefType("wobble"));
            Assert.AreEqual("auto", PrefsFunctions.ResolvePrefType(null));
        }

        [Test]
        public void AutoReadsAKeyBackAsTheTypeItWasWrittenWith()
        {
            const string prefix = "KwPrefsAutoTests_";
            var keys = new[] { prefix + "i", prefix + "f", prefix + "s", prefix + "b", prefix + "big" };

            try
            {
                EditorPrefs.SetInt(keys[0], 42);
                EditorPrefs.SetFloat(keys[1], 1.5f);
                EditorPrefs.SetString(keys[2], "hello");
                EditorPrefs.SetBool(keys[3], true);
                EditorPrefs.SetInt(keys[4], 7);

                // "auto" used to fall through to GetString for every key, so an int or a float read
                // back as whatever GetString makes of it - while the tool's description promised the
                // type would be detected.
                Assert.AreEqual("int", PrefsFunctions.DetectEditorPrefType(keys[0]));
                Assert.AreEqual("float", PrefsFunctions.DetectEditorPrefType(keys[1]));
                Assert.AreEqual("string", PrefsFunctions.DetectEditorPrefType(keys[2]));

                // EditorPrefs stores a bool as an int, so 0 and 1 are reported as bool. An int that
                // happens to be 0 or 1 is indistinguishable from one; anything else is an int.
                Assert.AreEqual("bool", PrefsFunctions.DetectEditorPrefType(keys[3]));
                Assert.AreEqual("int", PrefsFunctions.DetectEditorPrefType(keys[4]));

                var read = PrefsFunctions.GetEditorPref(keys[0]);
                StringAssert.Contains("42", read.ToString());
            }
            finally
            {
                foreach (var key in keys)
                    EditorPrefs.DeleteKey(key);
            }
        }

        [Test]
        public void AutoReadsAPlayerPrefBackAsTheTypeItWasWrittenWith()
        {
            const string prefix = "KwPlayerPrefsAutoTests_";
            var keys = new[] { prefix + "i", prefix + "f", prefix + "s" };

            try
            {
                PlayerPrefs.SetInt(keys[0], 9);
                PlayerPrefs.SetFloat(keys[1], 2.25f);
                PlayerPrefs.SetString(keys[2], "hello");

                Assert.AreEqual("int", PrefsFunctions.DetectPlayerPrefType(keys[0]));
                Assert.AreEqual("float", PrefsFunctions.DetectPlayerPrefType(keys[1]));
                Assert.AreEqual("string", PrefsFunctions.DetectPlayerPrefType(keys[2]));
            }
            finally
            {
                foreach (var key in keys)
                    PlayerPrefs.DeleteKey(key);
                PlayerPrefs.Save();
            }
        }

        [Test]
        public void TryWritePref_IntRejectsNonInt()
        {
            Assert.IsFalse(PrefsFunctions.TryWritePref("k", "notint", "int", isEditor: true, out var error));
            Assert.AreEqual("not an int", error);
        }

        [Test]
        public void TryWritePref_FloatRejectsNonFloat()
        {
            Assert.IsFalse(PrefsFunctions.TryWritePref("k", "xx", "float", isEditor: true, out _));
        }

        [Test]
        public void TryWritePref_EditorRoundTrip()
        {
            const string key = "gw_test_pref_int";
            EditorPrefs.DeleteKey(key);
            Assert.IsTrue(PrefsFunctions.TryWritePref(key, "42", "int", isEditor: true, out _));
            Assert.AreEqual(42, EditorPrefs.GetInt(key));
            EditorPrefs.DeleteKey(key);
        }

        [Test]
        public void TryWritePref_AutoWritesAsString()
        {
            const string key = "gw_test_pref_auto";
            EditorPrefs.DeleteKey(key);
            Assert.IsTrue(PrefsFunctions.TryWritePref(key, "hello", "auto", isEditor: true, out _));
            Assert.AreEqual("hello", EditorPrefs.GetString(key));
            EditorPrefs.DeleteKey(key);
        }
    }
}
