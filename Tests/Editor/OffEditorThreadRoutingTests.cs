// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class OffEditorThreadRoutingTests
    {
        // Losing the attribute or renaming the tool would queue these behind the very modal they
        // exist to clear, and they would fail only when someone is already stuck.
        [Test]
        public void DialogToolsBypassTheEditorThread()
        {
            foreach (var toolName in new[] { "get_editor_dialog", "dismiss_editor_dialog" })
            {
                var method = ToolRegistry.GetMethod(toolName);
                Assert.IsNotNull(method, $"'{toolName}' is not registered.");
                Assert.IsTrue(method.IsDefined(typeof(OffEditorThreadAttribute), false),
                    $"'{toolName}' must be marked [OffEditorThread].");
            }
        }

        [Test]
        public void OrdinaryToolsStayOnTheEditorThread()
        {
            var method = ToolRegistry.GetMethod("get_hierarchy");
            Assert.IsNotNull(method);
            Assert.IsFalse(method.IsDefined(typeof(OffEditorThreadAttribute), false),
                "Tools that touch Unity API must not bypass the editor thread.");
        }
    }
}
