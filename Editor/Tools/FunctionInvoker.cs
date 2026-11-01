// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace KitWright.Editor.Tools
{
    /// <summary>
    /// Invokes tool functions via reflection.
    /// Handles snake_case to PascalCase parameter mapping and type conversion.
    /// </summary>
    internal class FunctionInvoker
    {
        /// <summary>For tools that answer without awaiting. A tool whose body awaits resumes on
        /// EditorApplication.update, which blocking here stops, so it refuses instead of hanging the
        /// editor for good. Everything else calls <see cref="InvokeAsync"/>.</summary>
        public string Invoke(FunctionCall functionCall)
        {
            var pending = InvokeAsync(functionCall);
            if (!pending.IsCompleted)
                throw new InvalidOperationException(
                    $"'{functionCall?.FunctionName}' did not answer synchronously. Await InvokeAsync instead: " +
                    "blocking here stops the editor update loop the tool resumes on.");

            return pending.GetAwaiter().GetResult();
        }

        public async Task<string> InvokeAsync(FunctionCall functionCall)
        {
            if (functionCall == null)
                return ToolResultFormatter.Error("NULL_FUNCTION_CALL");
            if (string.IsNullOrWhiteSpace(functionCall.FunctionName))
                return ToolResultFormatter.Error("FUNCTION_NAME_REQUIRED");

            // Check manually registered tools first
            if (ToolRegistry.ManualTools.TryGetValue(functionCall.FunctionName, out var manualTool))
            {
                try
                {
                    RejectUnknownManualParameters(manualTool, functionCall.Parameters);

                    var missingParameter = FindMissingManualParameter(manualTool, functionCall.Parameters);
                    if (missingParameter != null)
                    {
                        return ToolResultFormatter.Error("MISSING_PARAM",
                            new { param = missingParameter, expected = "a required value" });
                    }

                    return WrapLegacyStringResult(manualTool.Handler(functionCall.Parameters));
                }
                catch (ToolArgumentException pex)
                {
                    return ToolResultFormatter.Error(pex.Code,
                        new { param = pex.ParamName, provided = pex.Provided, expected = pex.Expected });
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[KitWright] Manual tool '{functionCall.FunctionName}' failed: {ex.Message}");
                    return ToolResultFormatter.Error("MANUAL_TOOL_FAILED",
                        new { tool = functionCall.FunctionName, message = ex.Message });
                }
            }

            var method = ToolRegistry.GetMethod(functionCall.FunctionName);
            if (method == null)
                return ToolResultFormatter.Error("UNKNOWN_FUNCTION",
                    new { function = functionCall.FunctionName });

            try
            {
                var args = BuildArguments(method, functionCall.Parameters);
                var result = method.Invoke(null, args);
                return await NormalizeResultAsync(result, method.ReturnType);
            }
            catch (ToolArgumentException pex)
            {
                return ToolResultFormatter.Error(pex.Code,
                    new { param = pex.ParamName, provided = pex.Provided, expected = pex.Expected });
            }
            // An async tool throws out of the await rather than through reflection, so both shapes
            // land here and the policy errors below stay reachable either way.
            catch (Exception ex) when (ex is TargetInvocationException || ex is PathOutsideProjectException
                                       || ex is AmbiguousTargetException || ex is AmbiguousTypeException)
            {
                var inner = (ex as TargetInvocationException)?.InnerException ?? ex;
                if (inner is ToolArgumentException argument)
                    return ToolResultFormatter.Error(argument.Code,
                        new { param = argument.ParamName, provided = argument.Provided, expected = argument.Expected });
                if (inner is PathOutsideProjectException)
                    return ToolResultFormatter.Error("PATH_OUTSIDE_PROJECT",
                        new { function = functionCall.FunctionName, message = inner.Message });
                if (inner is AmbiguousTypeException ambiguousType)
                    return ToolResultFormatter.Error("AMBIGUOUS_TYPE",
                        new
                        {
                            function = functionCall.FunctionName,
                            value = ambiguousType.TypeName,
                            candidates = ambiguousType.Candidates
                        },
                        "Pass one of the candidates as the fully qualified name.");
                if (inner is AmbiguousTargetException ambiguous)
                    return ToolResultFormatter.Error("AMBIGUOUS_TARGET",
                        new
                        {
                            function = functionCall.FunctionName,
                            target = ambiguous.Target,
                            match_count = ambiguous.MatchCount,
                            candidates = ambiguous.Candidates,
                            message = inner.Message
                        },
                        "Pass one of the candidate ids as the target (find_method=by_id where the tool has that parameter).");

                Debug.LogError($"[KitWright] Function '{functionCall.FunctionName}' failed: {inner.Message}\n{inner.StackTrace}");
                return ToolResultFormatter.Error("FUNCTION_FAILED",
                    new { function = functionCall.FunctionName, message = inner.Message });
            }
            catch (Exception ex)
            {
                Debug.LogError($"[KitWright] Function invoke error for '{functionCall.FunctionName}': {ex.Message}");
                return ToolResultFormatter.Error("FUNCTION_INVOKE_ERROR",
                    new { function = functionCall.FunctionName, message = ex.Message });
            }
        }

        internal static async Task<string> NormalizeResultAsync(object result, Type declaredReturnType)
        {
            if (result is Task task)
            {
                await task;

                // The declared type, not the runtime one: an `async Task` method hands back a
                // Task<VoidTaskResult>, whose Result serialized to "{}" - no envelope, so a batch
                // read a tool that finished cleanly as a failure.
                if (declaredReturnType == typeof(Task))
                    return SerializeResult(null);

                var taskResult = task.GetType().GetProperty("Result")?.GetValue(task);
                return SerializeResult(taskResult);
            }

            return SerializeResult(result);
        }

        // Tools may return either a plain string (backward-compatible behavior) or a structured
        // object (e.g. via Response.Success/Error) which we JSON-encode for the client. An object
        // that is not already an envelope is wrapped as its data, as a JSON string already is, so
        // every caller - the client, a batch, a parent batch - reads success the same way.
        internal static string SerializeResult(object value)
        {
            if (value == null)
                return JsonConvert.SerializeObject(Response.Success("OK"));
            if (value is string s)
                return WrapLegacyStringResult(s);
            try
            {
                return JsonConvert.SerializeObject(IsEnvelopeObject(value) ? value : Response.Success("OK", value));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright] Failed to serialize tool result: {ex.Message}");
                return JsonConvert.SerializeObject(Response.Success(value.ToString() ?? "OK"));
            }
        }

        // Response builds its envelopes as anonymous objects, so this reads the shape, not a type.
        private static bool IsEnvelopeObject(object value)
        {
            switch (value)
            {
                case JObject obj:
                    return obj["success"]?.Type == JTokenType.Boolean;
                case IDictionary<string, object> dict:
                    return dict.TryGetValue("success", out var flag) && flag is bool;
                default:
                    return value.GetType().GetProperty("success")?.PropertyType == typeof(bool);
            }
        }

        // Legacy tools return a bare human-readable string on success and only switch to a
        // structured {success:false,...} JSON on failure, so callers can't uniformly branch on a
        // `success` field. Wrap a bare success string in the standard envelope so EVERY tool
        // response is parseable as {success, message}. Two things pass through untouched:
        //   (a) image data URIs ("data:...") -- MCPRequestHandler renders these as image content;
        //   (b) strings that are ALREADY a {success:...} envelope (ToolResultFormatter.Error, or a
        //       tool that hand-built one via JsonConvert) -- so we never double-wrap.
        internal static string WrapLegacyStringResult(string s)
        {
            if (s == null)
                return JsonConvert.SerializeObject(Response.Success("OK"));
            if (s.StartsWith("data:", StringComparison.Ordinal))
                return s;
            if (IsEnvelopeJson(s))
                return s;
            if (TryParseJsonPayload(s, out var payload))
                return JsonConvert.SerializeObject(Response.Success("OK", payload));
            return JsonConvert.SerializeObject(Response.Success(s));
        }

        // A legacy tool that hand-builds JSON without a `success` field would otherwise have its
        // payload escaped into `message`, forcing clients to parse the response twice. Keep it
        // structured under `data` instead.
        private static bool TryParseJsonPayload(string s, out JToken payload)
        {
            payload = null;

            var trimmed = s.TrimStart();
            if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
                return false;

            try
            {
                payload = JsonParse.Token(s);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsEnvelopeJson(string s)
        {
            var trimmed = s.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return false;
            try
            {
                var obj = JObject.Parse(s);
                return obj.TryGetValue("success", out var token) && token.Type == JTokenType.Boolean;
            }
            catch
            {
                return false;
            }
        }

        private static string FindMissingManualParameter(
            ToolRegistry.ManualToolEntry manualTool,
            Dictionary<string, string> parameters)
        {
            var required = manualTool.Definition?.parameters?.required;
            if (required == null)
                return null;

            foreach (var name in required)
            {
                if (parameters == null || !parameters.TryGetValue(name, out var value) || value == null)
                    return name;
            }

            return null;
        }

        // A method's parameter list, snake_case names, [ToolParam] flags and expected-format hints are
        // all fixed for the domain, but BuildArguments runs on every tool call — reflecting them per
        // request is pure overhead. Resolve once per MethodInfo and cache the flattened plan.
        private sealed class ParamPlan
        {
            public string OriginalName;
            public string SnakeName;
            public Type ParameterType;
            public bool HasDefaultValue;
            public object DefaultValue;
            public bool IsRequired;
            public string ExpectedFormat;
            public object ValueTypeDefault;
        }

        private static readonly ConcurrentDictionary<MethodInfo, ParamPlan[]> _paramPlanCache = new();

        private static ParamPlan[] GetParamPlans(MethodInfo method)
        {
            return _paramPlanCache.GetOrAdd(method, m =>
            {
                var methodParams = m.GetParameters();
                var plans = new ParamPlan[methodParams.Length];
                for (int i = 0; i < methodParams.Length; i++)
                {
                    var param = methodParams[i];
                    plans[i] = new ParamPlan
                    {
                        OriginalName = param.Name,
                        SnakeName = ToolRegistry.ToSnakeCase(param.Name),
                        ParameterType = param.ParameterType,
                        HasDefaultValue = param.HasDefaultValue,
                        DefaultValue = param.HasDefaultValue ? param.DefaultValue : null,
                        IsRequired = param.GetCustomAttribute<ToolParamAttribute>()?.Required ?? true,
                        ExpectedFormat = DescribeExpectedFormat(param.ParameterType),
                        ValueTypeDefault = param.ParameterType.IsValueType
                            ? Activator.CreateInstance(param.ParameterType)
                            : null,
                    };
                }
                return plans;
            });
        }

        // A name with no slot was dropped in silence, so the tool ran on its defaults and reported
        // success: test_filter for test_names read back as "ran the whole suite, all green". Checked
        // before MISSING_PARAM so a misspelling is named as one, not as the slot it failed to fill.
        private static void RejectUnknownParameters(ParamPlan[] plans, Dictionary<string, string> parameters)
        {
            if (parameters == null || parameters.Count == 0)
                return;

            List<string> unknown = null;
            foreach (var key in parameters.Keys)
            {
                var known = false;
                foreach (var plan in plans)
                {
                    if (key == plan.SnakeName || key == plan.OriginalName)
                    {
                        known = true;
                        break;
                    }
                }

                if (!known)
                    (unknown ??= new List<string>()).Add(key);
            }

            if (unknown == null)
                return;

            var accepted = new string[plans.Length];
            for (int i = 0; i < plans.Length; i++)
                accepted[i] = plans[i].SnakeName;

            ThrowUnknownParameters(unknown, accepted);
        }

        // A manual tool that declares nothing has no list to check against, so it still takes anything.
        private static void RejectUnknownManualParameters(
            ToolRegistry.ManualToolEntry manualTool, Dictionary<string, string> parameters)
        {
            var schema = manualTool.Definition?.parameters;
            if (schema == null || parameters == null || parameters.Count == 0)
                return;

            var accepted = new List<string>();
            if (schema.properties != null)
                accepted.AddRange(schema.properties.Keys);
            if (schema.required != null)
            {
                foreach (var name in schema.required)
                {
                    if (!accepted.Contains(name))
                        accepted.Add(name);
                }
            }

            if (accepted.Count == 0)
                return;

            List<string> unknown = null;
            foreach (var key in parameters.Keys)
            {
                if (!accepted.Contains(key))
                    (unknown ??= new List<string>()).Add(key);
            }

            if (unknown != null)
                ThrowUnknownParameters(unknown, accepted.ToArray());
        }

        private static void ThrowUnknownParameters(List<string> unknown, string[] accepted)
        {
            var suggestions = new List<string>();
            foreach (var key in unknown)
            {
                var suggestion = SuggestParameter(key, accepted);
                if (suggestion != null)
                    suggestions.Add($"'{key}' -> '{suggestion}'");
            }

            throw new ToolArgumentException(
                "UNKNOWN_PARAM",
                string.Join(", ", unknown),
                null,
                (accepted.Length == 0
                    ? "no parameters"
                    : $"one of [{string.Join(", ", accepted)}]") +
                (suggestions.Count == 0 ? string.Empty : $". Did you mean {string.Join(", ", suggestions)}?"));
        }

        private static readonly string[] CountSynonyms = { "limit", "count", "max", "top", "take", "n", "size", "page_size", "max_results", "max_entries", "max_count" };
        private static readonly string[] CountNames = { "count", "max_entries", "max_results", "limit" };

        internal static string SuggestParameter(string key, string[] accepted)
        {
            if (Array.IndexOf(CountSynonyms, key) >= 0)
            {
                foreach (var name in CountNames)
                {
                    if (Array.IndexOf(accepted, name) >= 0)
                        return name;
                }
            }

            string best = null;
            var bestDistance = 3;
            foreach (var name in accepted)
            {
                var distance = EditDistance(key, name);
                if (distance < bestDistance && distance < key.Length)
                {
                    best = name;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private static int EditDistance(string a, string b)
        {
            var row = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++)
                row[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                var diagonal = row[0];
                row[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    var above = row[j];
                    row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                    diagonal = above;
                }
            }

            return row[b.Length];
        }

        private object[] BuildArguments(MethodInfo method, Dictionary<string, string> parameters)
        {
            var plans = GetParamPlans(method);
            RejectUnknownParameters(plans, parameters);
            var args = new object[plans.Length];

            for (int i = 0; i < plans.Length; i++)
            {
                var plan = plans[i];

                string value = null;
                // Try snake_case name first, then original name
                if (parameters != null)
                {
                    if (!parameters.TryGetValue(plan.SnakeName, out value))
                        parameters.TryGetValue(plan.OriginalName, out value);
                }

                if (value != null)
                {
                    try
                    {
                        args[i] = ConvertValue(value, plan.ParameterType);
                    }
                    catch (Exception ex)
                    {
                        throw new ToolArgumentException(plan.SnakeName, value, plan.ExpectedFormat, ex);
                    }
                }
                else if (plan.HasDefaultValue)
                {
                    args[i] = plan.DefaultValue;
                }
                else if (plan.IsRequired)
                {
                    throw new ToolArgumentException(
                        "MISSING_PARAM",
                        plan.SnakeName,
                        null,
                        plan.ExpectedFormat);
                }
                else
                {
                    args[i] = plan.ValueTypeDefault;
                }
            }

            return args;
        }

        internal static object ConvertValue(string value, Type targetType)
        {
            if (targetType == typeof(string))
                return value;

            // Handle nullable: empty or "null" -> null; otherwise unwrap and parse the underlying.
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
            {
                if (string.IsNullOrEmpty(value) || value == "null")
                    return null;
                targetType = underlying;
            }

            // NOTE: parse failures -- INCLUDING an explicit empty string for a non-string type --
            // are intentionally NOT swallowed here. They propagate to BuildArguments, which
            // rethrows them as a ToolArgumentException so the caller gets an INVALID_PARAM error
            // instead of a silent default(T) / zero-vector / false / zeroth-enum wrong write.
            if (targetType == typeof(int))
                return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return long.Parse(value, CultureInfo.InvariantCulture);
            // NumberStyles.Float, not the default: that one also allows group separators, so "0,5" read as 5.
            if (targetType == typeof(float))
            {
                var f = float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (float.IsNaN(f) || float.IsInfinity(f))
                    throw new FormatException($"'{value}' is not a finite number");
                return f;
            }
            if (targetType == typeof(double))
            {
                var d = double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d))
                    throw new FormatException($"'{value}' is not a finite number");
                return d;
            }
            // Convert.ChangeType reads decimals with group separators allowed, so "0,5" came back as 5.
            if (targetType == typeof(decimal))
                return decimal.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (targetType == typeof(bool))
            {
                if (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("yes", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("no", StringComparison.OrdinalIgnoreCase))
                    return false;
                throw new FormatException($"'{value}' is not a boolean (expected true/false/1/0/yes/no)");
            }
            if (targetType.IsEnum)
                return ParseEnum(value, targetType);

            // Vector3: "x,y,z" or "(x,y,z)"
            if (targetType == typeof(Vector3))
                return ParseVector3(value);
            // Vector2
            if (targetType == typeof(Vector2))
                return ParseVector2(value);
            if (targetType == typeof(Vector4))
            {
                var c = ParseComponents(value, 4);
                return new Vector4(c[0], c[1], c[2], c[3]);
            }
            if (targetType == typeof(Quaternion))
            {
                var c = ParseComponents(value, 4);
                if (c[0] == 0f && c[1] == 0f && c[2] == 0f && c[3] == 0f)
                    throw new FormatException($"'{value}' is the zero quaternion, which is not a rotation (0,0,0,1 is no rotation)");
                return new Quaternion(c[0], c[1], c[2], c[3]);
            }
            // Color: "r,g,b,a" or "r,g,b" or "#hex"
            if (targetType == typeof(Color))
                return ParseColor(value);
            if (IsListType(targetType))
            {
                var array = JsonParse.Array(value);
                var element = targetType.IsArray ? targetType.GetElementType() : targetType.GetGenericArguments()[0];
                if (IsIntegralType(element))
                    RequireIntegerElements(array);
                return array.ToObject(targetType);
            }

            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }

        private static bool IsListType(Type type) =>
            type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>));

        private static bool IsIntegralType(Type type) =>
            type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong);

        // ToObject rounds 1.5 to 2 and reads "3" as 3. A float token with no fractional part (3.0) is still an integer.
        private static void RequireIntegerElements(JArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];
                if (item.Type == JTokenType.Integer)
                    continue;

                if (item.Type == JTokenType.Float)
                {
                    var number = (double)item;
                    if (!double.IsInfinity(number) && number == Math.Floor(number))
                        continue;
                }

                throw new FormatException($"element {i} ({item.ToString(Formatting.None)}) is not an integer");
            }
        }

        private static bool IsFlagsEnum(Type type) => type.IsDefined(typeof(FlagsAttribute), false);

        // Enum.Parse reads "A,B" on any enum as the OR of both, so a plain enum with C = A|B took it as C.
        // Only a [Flags] enum takes a list, and only of names it defines.
        private static object ParseEnum(string value, Type enumType)
        {
            var names = Enum.GetNames(enumType);
            if (value.IndexOf(',') >= 0)
            {
                if (!IsFlagsEnum(enumType))
                    throw new FormatException($"'{value}' is not one {enumType.Name} value (one of [{string.Join(", ", names)}])");

                var picked = new List<string>();
                foreach (var part in value.Split(','))
                {
                    var name = Array.Find(names, n => string.Equals(n, part.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (name == null)
                        throw new FormatException($"'{part.Trim()}' is not a defined {enumType.Name} value (one of [{string.Join(", ", names)}])");
                    picked.Add(name);
                }

                return Enum.Parse(enumType, string.Join(",", picked));
            }

            // Enum.Parse accepts unmapped NUMERIC strings (e.g. "999" -> (T)999) without
            // throwing, so validate the result is a defined member.
            var parsed = Enum.Parse(enumType, value, ignoreCase: true);
            if (!Enum.IsDefined(enumType, parsed))
                throw new FormatException($"'{value}' is not a defined {enumType.Name} value (one of [{string.Join(", ", names)}])");
            return parsed;
        }

        private static Vector3 ParseVector3(string value) =>
            ValueConverter.TryParseVector3(value, out var v, out var error) ? v : throw new FormatException(error);

        private static Vector2 ParseVector2(string value) =>
            ValueConverter.TryParseVector2(value, out var v, out var error) ? v : throw new FormatException(error);

        private static float[] ParseComponents(string value, int count)
        {
            var parts = value.Trim('(', ')', ' ').Split(',');
            if (parts.Length != count)
                throw new FormatException($"expected {count} comma-separated numbers, got {parts.Length}");

            var result = new float[count];
            for (var i = 0; i < count; i++)
            {
                if (!ValueConverter.TryFiniteFloat(parts[i], out result[i]))
                    throw new FormatException($"'{value}' has a non-numeric or non-finite component");
            }

            return result;
        }

        private static Color ParseColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new FormatException("a color is required");

            return ValueConverter.TryParseColor(value, default, out var color, out var error)
                ? color
                : throw new FormatException(error);
        }

        // Human-readable expected-format hint for an INVALID_PARAM error, so the caller knows
        // what shape the value should have taken.
        private static string DescribeExpectedFormat(Type type)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            if (t == typeof(int) || t == typeof(long)) return "an integer";
            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "a number";
            if (t == typeof(bool)) return "a boolean ('true'/'false'/'1'/'0'/'yes')";
            if (t == typeof(Vector3)) return "a Vector3 'x,y,z'";
            if (t == typeof(Vector2)) return "a Vector2 'x,y'";
            if (t == typeof(Vector4)) return "a Vector4 'x,y,z,w'";
            if (t == typeof(Quaternion)) return "a Quaternion 'x,y,z,w'";
            if (t == typeof(Color)) return "a Color 'r,g,b[,a]' or '#hex'";
            if (IsListType(t)) return "a JSON array such as [\"a\",\"b\"]";
            if (t.IsEnum)
                return IsFlagsEnum(t)
                    ? $"one or a comma-separated list of [{string.Join(", ", Enum.GetNames(t))}]"
                    : $"one of [{string.Join(", ", Enum.GetNames(t))}]";
            return t.Name;
        }
    }

    // Thrown when a tool argument string cannot be converted to the parameter's type. Carries
    // enough context for the invoker to return a structured INVALID_PARAM error.
    internal sealed class ToolArgumentException : Exception
    {
        public string Code { get; }
        public string ParamName { get; }
        public string Provided { get; }
        public string Expected { get; }

        public ToolArgumentException(string paramName, string provided, string expected, Exception inner)
            : this("INVALID_PARAM", paramName, provided, expected, inner)
        {
        }

        public ToolArgumentException(string code, string paramName, string provided, string expected, Exception inner = null)
            : base($"Parameter '{paramName}' could not be parsed as {expected}.", inner)
        {
            Code = code;
            ParamName = paramName;
            Provided = provided;
            Expected = expected;
        }
    }
}
