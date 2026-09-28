// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections;
using System.Reflection;
using KitWright.Editor.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace KitWright.Editor.Tests
{
    public sealed class EditorContextBuilderTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        [UnityTest]
        public IEnumerator AHierarchyChange_IsNotRebuiltUntilSomethingReadsIt()
        {
            var builder = new EditorContextBuilder(null, null);
            GameObject spawned = null;

            try
            {
                builder.GetActiveSceneSummary();
                spawned = new GameObject("kw-lazy-context-" + Guid.NewGuid().ToString("N"));

                // The hierarchyChanged handler, called here rather than waited on so the test does
                // not depend on which tick the editor raises the event.
                typeof(EditorContextBuilder).GetMethod("OnHierarchyChanged", Private).Invoke(builder, null);

                // An eagerly scheduled rebuild (delayCall) would have run on one of these ticks.
                for (var i = 0; i < 3; i++)
                    yield return null;

                var cached = (string)typeof(EditorContextBuilder).GetField("_cachedSceneSummary", Private).GetValue(builder);
                Assert.That(cached, Does.Not.Contain(spawned.name),
                    "a hierarchy change must only mark the snapshot stale, not rebuild it");
                Assert.That(builder.GetActiveSceneSummary(), Does.Contain(spawned.name),
                    "the next read rebuilds what went stale");
            }
            finally
            {
                builder.Dispose();
                if (spawned != null)
                    Object.DestroyImmediate(spawned);
            }
        }
    }
}
