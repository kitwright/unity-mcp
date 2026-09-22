// Copyright (C) KitWright. All rights reserved.

using System.Collections;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    // IvanMurzak/Unity-MCP #826.
    public sealed class DontDestroyOnLoadSearchTests
    {
        private const string ProbeName = "KitWrightDontDestroyOnLoadProbeTarget";

        [Test]
        public void DontDestroyOnLoadScene_IsInvalidOutsidePlayMode()
        {
            Assert.IsFalse(ObjectsHelper.DontDestroyOnLoadScene().IsValid(),
                "There is no DontDestroyOnLoad scene in edit mode, so the probe must not run.");
        }

        [UnityTest]
        public IEnumerator SearchFindsAnObjectParkedInDontDestroyOnLoad()
        {
            yield return new EnterPlayMode();

            var probe = new GameObject(ProbeName);
            Object.DontDestroyOnLoad(probe);

            // Without these the test passes vacuously: outside play mode DontDestroyOnLoad is a
            // no-op and the probe stays in an ordinary scene the old search already saw.
            Assert.IsTrue(Application.isPlaying, "EnterPlayMode did not take effect.");
            Assert.AreEqual("DontDestroyOnLoad", probe.scene.name);

            var byName = ObjectsHelper.FindObjects(ProbeName, ObjectsHelper.MethodByName,
                findAll: true, searchInactive: false);

            Assert.IsNotEmpty(byName, "by_name went blind to the DontDestroyOnLoad scene.");
            Assert.Contains(probe, byName);

            // ExitPlayMode tears down the DontDestroyOnLoad scene, probe included.
            yield return new ExitPlayMode();
        }
    }
}
