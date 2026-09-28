// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.MCP.Server;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class MCPToolListChangeNotifierTests
    {
        [Test]
        public void TryConsumePending_ConsumesOnceUntilRestored()
        {
            MCPToolListChangeNotifier.RestorePending();

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending());
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending());

            MCPToolListChangeNotifier.RestorePending();

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending());
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending());
        }

        // One flag used to be consumed by whichever client asked first; every other client on the
        // editor kept a stale list until it reconnected.
        [Test]
        public void EverySessionIsToldOnceAboutAChange()
        {
            var first = "kw-first-" + System.Guid.NewGuid().ToString("N");
            var second = "kw-second-" + System.Guid.NewGuid().ToString("N");
            var neverSeen = "kw-unknown-" + System.Guid.NewGuid().ToString("N");
            var late = "kw-late-" + System.Guid.NewGuid().ToString("N");

            MCPToolListChangeNotifier.Observe(first);
            MCPToolListChangeNotifier.Observe(second);
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending(first), "nothing changed since it initialized");

            MCPToolListChangeNotifier.MarkChanged();
            MCPToolListChangeNotifier.Observe(late);

            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending(first));
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending(first));
            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending(second), "the first client must not use up the second's notification");
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending(second));
            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending(neverSeen),
                "a session with no mark has held its list since before the change");
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending(late), "it initialized after the change, so its list is current");

            MCPToolListChangeNotifier.RestorePending(first);
            Assert.IsTrue(MCPToolListChangeNotifier.TryConsumePending(first), "a failed send re-arms only that session");
            Assert.IsFalse(MCPToolListChangeNotifier.TryConsumePending(second));

            while (MCPToolListChangeNotifier.TryConsumePending())
            {
            }
        }

        [Test]
        public void BuildSseBody_EmitsNotificationBeforeResponse()
        {
            var body = MCPToolListChangeNotifier.BuildSseBody("{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}");

            Assert.That(body, Does.StartWith("data: " + MCPToolListChangeNotifier.NotificationJson + "\n\n"));
            Assert.That(body, Does.Contain("data: {\"jsonrpc\":\"2.0\",\"id\":\"1\",\"result\":{}}\n\n"));
        }
    }
}
