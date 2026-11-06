// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Threading;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools.Scripting;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class RequestCancellationTests
    {
        [Test]
        public void Cancel_MatchesTheIdWhetherItArrivesAsALongOrADouble()
        {
            using (var cts = new CancellationTokenSource())
            using (RequestCancellation.Register("session-a", 5L, cts))
            {
                Assert.IsFalse(RequestCancellation.Cancel("session-b", 5L), "another session's id 5 is another call");
                Assert.IsTrue(RequestCancellation.Cancel("session-a", 5.0));
                Assert.IsTrue(cts.IsCancellationRequested);
            }
        }

        [Test]
        public void Cancel_WithoutASession_IsNotRegistered_SoTwoBareCallersCannotHitEachOther()
        {
            using (var cts = new CancellationTokenSource())
            using (RequestCancellation.Register(null, 9L, cts))
            {
                Assert.IsFalse(RequestCancellation.Cancel(null, 9L));
                Assert.IsFalse(cts.IsCancellationRequested);
            }
        }

        [Test]
        public void Cancel_AfterTheCallFinished_FindsNothing()
        {
            using (var cts = new CancellationTokenSource())
            {
                RequestCancellation.Register("session-a", "abc", cts).Dispose();

                Assert.IsFalse(RequestCancellation.Cancel("session-a", "abc"));
                Assert.IsFalse(cts.IsCancellationRequested);
            }
        }

        [Test]
        public void LoopGuard_StopsACancelledSnippet_AndTheNextSnippetRunsFree()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                LoopBudgetGuard.Begin(TimeSpan.FromSeconds(20), cts.Token);
                try
                {
                    Assert.Throws<OperationCanceledException>(() =>
                    {
                        for (var i = 0; i < 10000; i++)
                            LoopBudgetGuard.Check(true);
                    });
                }
                finally
                {
                    LoopBudgetGuard.End();
                }
            }

            LoopBudgetGuard.Begin(TimeSpan.FromSeconds(20));
            try
            {
                Assert.DoesNotThrow(() =>
                {
                    for (var i = 0; i < 10000; i++)
                        LoopBudgetGuard.Check(true);
                });
            }
            finally
            {
                LoopBudgetGuard.End();
            }
        }
    }
}
