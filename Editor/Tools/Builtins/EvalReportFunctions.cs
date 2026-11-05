// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("ExecuteCode")]
    internal static class EvalReportFunctions
    {
        private const int RepeatedPatternThreshold = 2;
        private const int MaxTop = 500;

        private static readonly Regex ApiCall = new Regex(@"\b([A-Z][A-Za-z0-9_]*)\.([A-Z][A-Za-z0-9_]*)\b", RegexOptions.Compiled);

        [Description("Aggregate the execute_code history of this Editor session (the latest 50 calls) into a ranked report: how many ran, " +
                     "the error rate, the share of one-liners, which Type.Member APIs the snippets use most, and the ones used " +
                     "repeatedly, which are candidates for a dedicated tool. A domain reload keeps the history; closing the Editor clears it.")]
        [ReadOnlyTool]
        public static object ReportEvals(
            [ToolParam("Most entries in each ranked list, 0 to 500. Default 50.", Required = false)] int top = 50)
        {
            if (top < 0 || top > MaxTop)
                top = 50;

            var entries = ScriptExecutionFunctions.LoadHistory().entries;
            if (entries.Count == 0)
                return Response.Success("No execute_code calls in this session yet.", new { total = 0 });

            var counts = new Dictionary<string, int>();
            foreach (var entry in entries.Where(e => !string.IsNullOrEmpty(e.code)))
                foreach (var fingerprint in ApiCall.Matches(entry.code).Cast<Match>().Select(m => m.Value).Distinct())
                    counts[fingerprint] = counts.TryGetValue(fingerprint, out var seen) ? seen + 1 : 1;

            var ranked = counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
            var failures = entries.Count(e => !e.success);
            var oneLiners = entries.Count(e => !string.IsNullOrWhiteSpace(e.code) && e.code.Trim().IndexOf('\n') < 0);

            return Response.Success($"{entries.Count} execute_code call(s) in this session.", new
            {
                total = entries.Count,
                errors = failures,
                errorRatePercent = Math.Round(100.0 * failures / entries.Count, 1),
                oneLinerPercent = Math.Round(100.0 * oneLiners / entries.Count, 1),
                apiFingerprints = ranked.Take(top).Select(p => new { api = p.Key, calls = p.Value }).ToArray(),
                dedicatedToolCandidates = ranked.Where(p => p.Value >= RepeatedPatternThreshold).Take(top)
                    .Select(p => new { api = p.Key, calls = p.Value }).ToArray()
            });
        }
    }
}
