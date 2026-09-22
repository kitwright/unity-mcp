// Copyright (C) KitWright. All rights reserved.

using System;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Interop;
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
    }
}
