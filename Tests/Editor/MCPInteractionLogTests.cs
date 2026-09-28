// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitWright.Editor.MCP.Server;
using NUnit.Framework;
using UnityEditor;

namespace KitWright.Editor.Tests
{
    public sealed class MCPInteractionLogTests
    {
        [Test]
        public void AnEvictedEntry_TakesItsImageWithIt()
        {
            // The key is the live editor's activity log: it is set aside so this log starts empty and
            // evicts only entries it wrote, and put back afterwards.
            var liveLog = SessionState.GetString(MCPInteractionLog.SessionStateKey, null);
            SessionState.EraseString(MCPInteractionLog.SessionStateKey);
            var written = new List<string>();

            try
            {
                var log = new MCPInteractionLog(60);
                var image = "data:image/png;base64," + Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
                for (var i = 0; i < 63; i++)
                {
                    log.Add("capture_game_view", MCPToolCallStatus.Success, image);
                    written.Add(log.GetEntries()[0].ImageFilePath);
                }

                Assert.That(written, Has.None.Null);
                Assert.That(written, Is.Unique, "two entries sharing a file would delete each other's image");
                Assert.AreEqual(60, written.Count(File.Exists), "a full log keeps one image per entry it holds");
                Assert.IsFalse(written.Take(3).Any(File.Exists), "the entries the ring overwrote left their images behind");

                log.SetCapacity(50);
                Assert.AreEqual(50, written.Count(File.Exists), "shrinking the log drops the images of the entries it drops");
                Assert.IsTrue(written.Skip(13).All(File.Exists), "the newest entries keep theirs");
            }
            finally
            {
                foreach (var path in written)
                {
                    if (path != null && File.Exists(path))
                        File.Delete(path);
                }

                if (liveLog != null)
                    SessionState.SetString(MCPInteractionLog.SessionStateKey, liveLog);
                else
                    SessionState.EraseString(MCPInteractionLog.SessionStateKey);
            }
        }
    }
}
