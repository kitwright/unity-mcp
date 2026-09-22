// Copyright (C) KitWright. All rights reserved.

using System;
using System.Diagnostics;

namespace KitWright.Editor.Tools.Scripting
{
    /// <summary>
    /// Wall-clock budget for a snippet's loops. <see cref="LoopGuardInjector"/> wraps every loop
    /// condition in <see cref="Check"/> before the snippet is compiled, so a loop that never exits
    /// raises here instead of wedging the editor's main thread.
    /// </summary>
    /// <remarks>
    /// A snippet runs on the main thread through reflection, and .NET Core has no Thread.Abort:
    /// once a loop stops yielding, nothing outside it can regain control and the editor has to be
    /// killed, losing every unsaved scene. Throwing from inside the loop is the only exit that
    /// unwinds normally. Recursion (StackOverflowException) and blocking waits have no condition to
    /// wrap and are still unreachable from here.
    /// </remarks>
    public static class LoopBudgetGuard
    {
        /// <summary>
        /// Budget one snippet's loops get before <see cref="Check"/> throws. Kept well under the MCP
        /// client's request timeout so a runaway loop comes back as a readable error rather than a
        /// timed-out call that says nothing about what went wrong.
        /// </summary>
        public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(20);

        // Reading the clock on every iteration would dominate a tight loop, so most iterations cost
        // one increment and one compare instead.
        private const int CheckStride = 256;

        private static long _deadlineTimestamp;
        private static int _sinceLastCheck;
        private static TimeSpan _budget;

        internal static void Begin(TimeSpan budget)
        {
            _budget = budget;
            _deadlineTimestamp = Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency);
            _sinceLastCheck = 0;
        }

        internal static void End()
        {
            _deadlineTimestamp = 0;
        }

        /// <summary>
        /// Returns <paramref name="condition"/> unchanged, or throws once the snippet's loops have
        /// run past the budget. Called by injected code, not by hand.
        /// </summary>
        public static bool Check(bool condition)
        {
            if (_deadlineTimestamp == 0) return condition;
            if (++_sinceLastCheck < CheckStride) return condition;

            _sinceLastCheck = 0;
            if (Stopwatch.GetTimestamp() <= _deadlineTimestamp) return condition;

            _deadlineTimestamp = 0;
            throw new TimeoutException(
                $"Snippet loop exceeded its {_budget.TotalSeconds:0}s budget and was stopped. " +
                "A loop is not making progress -- check its exit condition (a counter reassigned " +
                "inside the body, a collection mutated while iterating it).");
        }
    }
}
