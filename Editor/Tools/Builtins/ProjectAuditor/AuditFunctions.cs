// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using Unity.ProjectAuditor.Editor;
using UnityEditor;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Project Auditor scans, in their own assembly so the package builds without com.unity.project-auditor.
    /// A scan builds the player's code and can run for minutes, so audit returns at once and audit_status reports.
    /// </summary>
    [ToolProvider("Profiling")]
    internal static class AuditFunctions
    {
        private const string StateKey = "KitWright.Audit.State";
        private const string DefaultOutputFolder = "Temp/KitWrightAudit";
        private const string CsvHeader = "Category,Severity,Id,Description,Path,Line";

        [Description("Start a Project Auditor static-analysis scan of the project (needs com.unity.project-auditor). Returns at once; " +
                     "poll audit_status until it is completed, then read the CSV. A scan builds the player's code, so it can take " +
                     "minutes and cannot be cancelled. output must stay inside the project.")]
        public static object Audit(
            [ToolParam("Comma-separated issue categories to scan (e.g. Code,ProjectSetting,AssetIssue). Empty = all.", Required = false)] string categories = "",
            [ToolParam("CSV path, relative to the project root or absolute inside it. Default Temp/KitWrightAudit/<scan id>.csv.", Required = false)] string output = "")
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                return Response.Error("EDIT_MODE_REQUIRED", hint: "Call exit_play_mode first, then retry audit.");

            var state = Read();
            if (state.Value<string>("status") == "scanning" && Running)
                return Response.Error("AUDIT_ALREADY_RUNNING", state);

            if (!TryParseCategories(categories, out var parsed, out var badName))
                return Response.Error("UNKNOWN_CATEGORY", new { category = badName, valid = Enum.GetNames(typeof(IssueCategory)) });

            var scanId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var outputPath = PathSafety.ResolveProjectPath(string.IsNullOrWhiteSpace(output) ? $"{DefaultOutputFolder}/{scanId}.csv" : output);
            if (!outputPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PATH", new { output }, "output must be a .csv file.");

            Running = true;
            Write(new JObject
            {
                ["status"] = "scanning",
                ["scanId"] = scanId,
                ["output"] = outputPath,
                ["categories"] = parsed == null ? "all" : string.Join(",", parsed),
                ["startedUtc"] = DateTime.UtcNow.ToString("o")
            });

            try
            {
                new ProjectAuditor().AuditAsync(new AnalysisParams
                {
                    Categories = parsed,
                    OnCompleted = report => Finish(scanId, outputPath, report)
                });
            }
            catch (Exception ex)
            {
                Running = false;
                Write(new JObject { ["status"] = "failed", ["scanId"] = scanId, ["error"] = ex.Message });
                return Response.Error("AUDIT_START_FAILED", new { message = ex.Message });
            }

            return Response.Success("Started the Project Auditor scan.", new { scanId, output = outputPath });
        }

        [Description("Report the last Project Auditor scan: idle, scanning, completed, failed or interrupted (a domain reload " +
                     "ended it). A completed scan lists the CSV path and the number of issues found.")]
        [ReadOnlyTool]
        public static object AuditStatus()
        {
            var state = Read();
            if (state.Value<string>("status") == "scanning" && !Running)
            {
                state["status"] = "interrupted";
                Write(state);
            }

            return Response.Success($"The audit is {state.Value<string>("status")}.", state);
        }

        private static bool Running;

        private static void Finish(string scanId, string outputPath, Report report)
        {
            Running = false;
            try
            {
                var issues = report.GetAllIssues();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath));

                var csv = new StringBuilder(CsvHeader).AppendLine();
                foreach (var issue in issues.Where(i => i.IsIssue()))
                    csv.AppendLine(string.Join(",", Quote(issue.Category.ToString()), Quote(issue.Severity.ToString()),
                        Quote(issue.Id.ToString()), Quote(issue.Description), Quote(issue.RelativePath), issue.Line));

                AtomicFile.WriteAllText(outputPath, csv.ToString());

                Write(new JObject
                {
                    ["status"] = "completed",
                    ["scanId"] = scanId,
                    ["output"] = outputPath,
                    ["totalItems"] = report.NumTotalIssues,
                    ["issues"] = issues.Count(i => i.IsIssue()),
                    ["endedUtc"] = DateTime.UtcNow.ToString("o")
                });
            }
            catch (Exception ex)
            {
                Write(new JObject { ["status"] = "failed", ["scanId"] = scanId, ["error"] = ex.Message });
            }
        }

        private static bool TryParseCategories(string text, out IssueCategory[] categories, out string badName)
        {
            categories = null;
            badName = null;
            if (string.IsNullOrWhiteSpace(text))
                return true;

            var parsed = new System.Collections.Generic.List<IssueCategory>();
            foreach (var name in text.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0))
            {
                if (!Enum.TryParse(name, true, out IssueCategory category) || !Enum.IsDefined(typeof(IssueCategory), category))
                {
                    badName = name;
                    return false;
                }

                parsed.Add(category);
            }

            categories = parsed.Distinct().ToArray();
            return true;
        }

        private static string Quote(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

        private static JObject Read()
        {
            var json = SessionState.GetString(StateKey, string.Empty);
            return json.Length == 0 ? new JObject { ["status"] = "idle" } : JObject.Parse(json);
        }

        private static void Write(JObject state) => SessionState.SetString(StateKey, state.ToString(Newtonsoft.Json.Formatting.None));
    }
}
