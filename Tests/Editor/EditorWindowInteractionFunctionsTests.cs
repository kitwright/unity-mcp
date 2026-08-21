// Copyright (C) KitWright. Licensed under MIT.

using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class EditorWindowInteractionFunctionsTests
    {
        [Test]
        public void SimulateEditorWindowClick_ExposesWindowAndPixelParameters()
        {
            var method = typeof(EditorWindowInteractionFunctions).GetMethod(
                "SimulateEditorWindowClick",
                BindingFlags.Public | BindingFlags.Static);

            Assert.IsNotNull(method);
            var names = method.GetParameters().Select(p => p.Name).ToArray();
            CollectionAssert.Contains(names, "window");
            CollectionAssert.Contains(names, "x");
            CollectionAssert.Contains(names, "y");
        }

        [Test]
        public void EditorWindowInteractionTools_AreNotReadOnly()
        {
            Assert.IsFalse(ToolRegistry.IsReadOnly(typeof(EditorWindowInteractionFunctions).GetMethod("SimulateEditorWindowClick")));
            Assert.IsFalse(ToolRegistry.IsReadOnly(typeof(EditorWindowInteractionFunctions).GetMethod("SimulateEditorWindowKey")));
        }
    }
}
