// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Editor")]
    internal static class WaitFunctions
    {
        private const int MaxJobs = 32;
        private static readonly string[] Ops = { "equals", "notEquals", "greaterThan", "lessThan", "contains", "changed" };
        private static readonly Dictionary<string, WaitJob> Jobs = new Dictionary<string, WaitJob>();

        private sealed class WaitSpec
        {
            public string Member;
            public string Op;
            public string Value;
            public string Target;
            public string FindType;
            public double TimeoutSeconds;
            public int IntervalMs;
            public bool TolerateMissing;
            public bool PauseOnMet;
        }

        private sealed class WaitJob
        {
            public string Id;
            public string State = "waiting";
            public string Detail;
            public object LastValue;
            public int Polls;
            public DateTime StartedUtc = DateTime.UtcNow;
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
        }

        [Description("Wait until a member's value satisfies a condition, polling in the editor. member is a dotted path: " +
                     "without target or find_type it is a static path ('UnityEditor.EditorApplication.isPlaying'); with " +
                     "target (a GameObject: instance id, name or path) or find_type (a UnityEngine.Object type, first instance) " +
                     "it walks instance members, trying a component of that name when the GameObject has no such member " +
                     "('Health.Current'). Only fields and properties are read. op is equals, notEquals, greaterThan, lessThan, " +
                     "contains (text) or changed (met on the first change from the value first seen; needs no value). " +
                     "value is coerced to the member's type; the text 'null' means null. A met wait answers success; a wait that " +
                     "runs out answers WAIT_TIMEOUT. background=true returns a wait_id at once for wait_status / wait_cancel " +
                     "(background waits are lost on a domain reload). To screenshot when met, follow with capture_game_view.")]
        [LongRunningTool(660)]
        public static async Task<object> WaitFor(
            [ToolParam("Dotted member path to read")] string member,
            [ToolParam("equals (default), notEquals, greaterThan, lessThan, contains or changed", Required = false)] string op = "equals",
            [ToolParam("Operand, required except for 'changed'", Required = false)] string value = null,
            [ToolParam("GameObject (instance id, name or path) to read instance members from", Required = false)] string target = null,
            [ToolParam("Fully qualified UnityEngine.Object type; its first loaded instance is read", Required = false)] string find_type = null,
            [ToolParam("Longest wait in seconds, 0 to 600. Default 30.", Required = false)] double timeout_seconds = 30,
            [ToolParam("Polling interval in milliseconds, 16 to 5000. Default 100.", Required = false)] int poll_interval_ms = 100,
            [ToolParam("Return a wait_id immediately and keep waiting in the background", Required = false)] bool background = false,
            [ToolParam("Treat a type, member or instance that is not found as 'not met yet' instead of an error", Required = false)] bool tolerate_missing = false,
            [ToolParam("Pause Play Mode on the frame the condition holds", Required = false)] bool pause_on_met = false)
        {
            if (string.IsNullOrWhiteSpace(member))
                return Response.Error("INVALID_PARAM", new { param = "member", provided = member });

            op = string.IsNullOrWhiteSpace(op) ? "equals" : op.Trim();
            var canonicalOp = Ops.FirstOrDefault(o => string.Equals(o, op, StringComparison.OrdinalIgnoreCase));
            if (canonicalOp == null)
                return Response.Error("INVALID_OP", new { op, valid = Ops });

            if (canonicalOp != "changed" && value == null)
                return Response.Error("INVALID_PARAM", new { param = "value" }, "value is required for every op except 'changed'.");

            if (!(timeout_seconds >= 0 && timeout_seconds <= 600))
                return Response.Error("INVALID_PARAM", new { param = "timeout_seconds", provided = timeout_seconds }, "timeout_seconds must be from 0 to 600.");

            if (poll_interval_ms < 16 || poll_interval_ms > 5000)
                return Response.Error("INVALID_PARAM", new { param = "poll_interval_ms", provided = poll_interval_ms }, "poll_interval_ms must be from 16 to 5000.");

            if (!string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(find_type))
                return Response.Error("INVALID_PARAM", new { target, find_type }, "Pass target or find_type, not both.");

            var spec = new WaitSpec
            {
                Member = member.Trim(),
                Op = canonicalOp,
                Value = value,
                Target = string.IsNullOrEmpty(target) ? null : target,
                FindType = string.IsNullOrEmpty(find_type) ? null : find_type,
                TimeoutSeconds = timeout_seconds,
                IntervalMs = poll_interval_ms,
                TolerateMissing = tolerate_missing,
                PauseOnMet = pause_on_met
            };

            var job = new WaitJob { Id = Guid.NewGuid().ToString("N") };

            if (background)
            {
                Register(job);
                _ = Run(spec, job);
                return Response.Success("Waiting in the background.", new { wait_id = job.Id, state = job.State });
            }

            await Run(spec, job);
            return Describe(job, spec.Member);
        }

        [Description("Get the state of a background wait started with wait_for background=true: waiting, met, timeout, " +
                     "cancelled or error, with the last value read.")]
        [ReadOnlyTool]
        public static object WaitStatus(
            [ToolParam("The wait_id wait_for returned")] string wait_id)
        {
            if (!Jobs.TryGetValue(wait_id ?? string.Empty, out var job))
                return Response.Error("WAIT_NOT_FOUND", new { wait_id },
                    "Unknown wait id. A domain reload drops every background wait, and only the 32 most recent are kept.");

            return Response.Success($"The wait is {job.State}.", Snapshot(job));
        }

        [Description("Cancel a background wait started with wait_for background=true.")]
        public static object WaitCancel(
            [ToolParam("The wait_id wait_for returned")] string wait_id)
        {
            if (!Jobs.TryGetValue(wait_id ?? string.Empty, out var job))
                return Response.Error("WAIT_NOT_FOUND", new { wait_id });

            if (job.State != "waiting")
                return Response.Error("WAIT_ALREADY_FINISHED", Snapshot(job));

            job.Cancel.Cancel();
            return Response.Success("Cancelling the wait.", new { wait_id, state = job.State });
        }

        private static async Task Run(WaitSpec spec, WaitJob job)
        {
            var deadline = DateTime.UtcNow.AddSeconds(spec.TimeoutSeconds);
            object initial = null;
            var hasInitial = false;

            try
            {
                while (true)
                {
                    job.Cancel.Token.ThrowIfCancellationRequested();
                    job.Polls++;

                    if (TryRead(spec, out var current, out var error, out var missing))
                    {
                        job.LastValue = Printable(current);

                        bool met;
                        if (spec.Op == "changed")
                        {
                            if (!hasInitial) { initial = current; hasInitial = true; }
                            met = hasInitial && job.Polls > 1 && !Equals(initial, current);
                        }
                        else
                        {
                            met = Matches(current, spec.Op, spec.Value);
                        }

                        if (met)
                        {
                            job.State = "met";
                            if (spec.PauseOnMet && EditorApplication.isPlaying)
                                EditorApplication.isPaused = true;
                            return;
                        }
                    }
                    else if (!(missing && spec.TolerateMissing))
                    {
                        job.State = "error";
                        job.Detail = error;
                        return;
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        job.State = "timeout";
                        return;
                    }

                    await Task.Delay(spec.IntervalMs, job.Cancel.Token);
                }
            }
            catch (OperationCanceledException)
            {
                job.State = "cancelled";
            }
            catch (Exception ex)
            {
                job.State = "error";
                job.Detail = ex.Message;
            }
        }

        private static bool TryRead(WaitSpec spec, out object value, out string error, out bool missing)
        {
            value = null;
            error = null;
            missing = false;

            var segments = spec.Member.Split('.');
            object current;
            Type staticType = null;

            if (spec.Target != null)
            {
                current = ObjectsHelper.FindTarget(spec.Target);
                if (current == null)
                {
                    missing = true;
                    error = $"target '{spec.Target}' not found";
                    return false;
                }
            }
            else if (spec.FindType != null)
            {
                var type = ReflectionFunctions.Resolve(spec.FindType, out _, out _);
                if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type))
                {
                    error = $"'{spec.FindType}' is not a loaded UnityEngine.Object type";
                    return false;
                }

                var found = UnityEngine.Object.FindObjectsByType(type, FindObjectsInactive.Include, FindObjectsSortMode.None);
                if (found.Length == 0)
                {
                    missing = true;
                    error = $"no instance of '{spec.FindType}' is loaded";
                    return false;
                }

                current = found[0];
            }
            else
            {
                var split = segments.Length - 1;
                for (; split >= 1 && staticType == null; split--)
                    staticType = ReflectionFunctions.Resolve(string.Join(".", segments, 0, split), out _, out _);

                if (staticType == null)
                {
                    missing = true;
                    error = $"no type resolves from the start of '{spec.Member}'";
                    return false;
                }

                segments = segments.Skip(split + 1).ToArray();
                current = null;
            }

            foreach (var segment in segments)
            {
                if (current is UnityEngine.Object unityObject && unityObject == null)
                {
                    missing = true;
                    error = $"the object is destroyed before '{segment}'";
                    return false;
                }

                if (staticType == null && current == null)
                {
                    missing = true;
                    error = $"null before '{segment}'";
                    return false;
                }

                var owner = staticType ?? current.GetType();
                var flags = BindingFlags.Public | BindingFlags.FlattenHierarchy |
                            (staticType != null ? BindingFlags.Static : BindingFlags.Instance);

                var property = owner.GetProperty(segment, flags);
                var field = property == null ? owner.GetField(segment, flags) : null;

                if (property != null && property.CanRead && property.GetIndexParameters().Length == 0)
                    current = property.GetValue(staticType != null ? null : current);
                else if (field != null)
                    current = field.GetValue(staticType != null ? null : current);
                else if (current is GameObject go && FindComponent(go, segment) is Component component)
                    current = component;
                else
                {
                    missing = true;
                    error = $"'{segment}' is not a readable field or property of {owner.Name}";
                    return false;
                }

                staticType = null;
            }

            value = current is UnityEngine.Object destroyed && destroyed == null ? null : current;
            return true;
        }

        private static Component FindComponent(GameObject go, string name) =>
            go.GetComponents<Component>().FirstOrDefault(c => c != null &&
                (c.GetType().Name == name || c.GetType().FullName == name));

        private static bool Matches(object actual, string op, string operand)
        {
            if (op == "contains")
                return actual != null && Convert.ToString(actual, CultureInfo.InvariantCulture).Contains(operand ?? string.Empty);

            object expected = null;
            if (operand != "null")
                expected = actual == null ? operand : Coerce(operand, actual.GetType());

            switch (op)
            {
                case "equals": return Equals(actual, expected);
                case "notEquals": return !Equals(actual, expected);
                case "greaterThan": return actual is IComparable greater && expected != null && greater.CompareTo(expected) > 0;
                case "lessThan": return actual is IComparable less && expected != null && less.CompareTo(expected) < 0;
                default: return false;
            }
        }

        private static object Coerce(string text, Type type)
        {
            if (type.IsEnum)
                return Enum.Parse(type, text, true);

            if (type == typeof(string))
                return text;

            return Convert.ChangeType(text, type, CultureInfo.InvariantCulture);
        }

        private static object Printable(object value)
        {
            if (value == null) return null;
            if (value is UnityEngine.Object o) return o.name;
            return value.GetType().IsPrimitive || value is string || value is Enum ? value : value.ToString();
        }

        private static void Register(WaitJob job)
        {
            if (Jobs.Count >= MaxJobs)
            {
                var oldest = Jobs.Values.Where(j => j.State != "waiting").OrderBy(j => j.StartedUtc).FirstOrDefault()
                             ?? Jobs.Values.OrderBy(j => j.StartedUtc).First();
                oldest.Cancel.Cancel();
                Jobs.Remove(oldest.Id);
            }

            Jobs[job.Id] = job;
        }

        private static object Snapshot(WaitJob job) => new
        {
            wait_id = job.Id,
            state = job.State,
            value = job.LastValue,
            polls = job.Polls,
            elapsed_seconds = Math.Round((DateTime.UtcNow - job.StartedUtc).TotalSeconds, 2),
            detail = job.Detail
        };

        private static object Describe(WaitJob job, string member)
        {
            var data = Snapshot(job);
            switch (job.State)
            {
                case "met": return Response.Success($"'{member}' met the condition.", data);
                case "timeout": return Response.Error("WAIT_TIMEOUT", data, "The condition did not hold in time; value is the last one read.");
                case "cancelled": return Response.Error("WAIT_CANCELLED", data);
                default: return Response.Error("WAIT_FAILED", data);
            }
        }
    }
}
