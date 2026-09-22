// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using KitWright.Editor.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// Calls a tool the way a client does: every argument a string, through FunctionInvoker, answer
    /// parsed as the standard envelope. Twelve test fixtures had their own byte-identical copy of this.
    /// Pull it in with <c>using static KitWright.Editor.Tests.ToolCall;</c> and the call sites read the
    /// same as they did locally.
    /// </summary>
    internal static class ToolCall
    {
        public static JObject Call(string tool, params string[] pairs)
        {
            var parameters = new Dictionary<string, string>();
            for (var i = 0; i + 1 < pairs.Length; i += 2)
                parameters[pairs[i]] = pairs[i + 1];

            return JObject.Parse(new FunctionInvoker().Invoke(
                new FunctionCall { FunctionName = tool, Parameters = parameters }));
        }

        public static JObject Ok(string tool, params string[] pairs)
        {
            var answer = Call(tool, pairs);
            Assert.IsTrue((bool)answer["success"], $"{tool}: {answer}");
            return answer;
        }

        public static JObject Refused(string tool, params string[] pairs)
        {
            var answer = Call(tool, pairs);
            Assert.IsFalse((bool)answer["success"], $"{tool} should have refused: {answer}");
            return answer;
        }

        public static string Code(string tool, params string[] pairs) => (string)Call(tool, pairs)["code"];
    }
}
