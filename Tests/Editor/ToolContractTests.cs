// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// Invariants that hold for EVERY tool rather than for one named tool. A per-tool test only ever
    /// covers the tool someone thought to write it for; these fail on the next tool added with a
    /// parameter the invoker cannot build, a name a second provider already claimed, or a return
    /// type that reaches the client without a `success` field. Each test reports every violation it
    /// finds in one message, so a run answers "which tools are wrong", not "one of them is".
    /// </summary>
    public sealed class ToolContractTests
    {
        private readonly struct DeclaredTool
        {
            public DeclaredTool(string name, MethodInfo method)
            {
                Name = name;
                Method = method;
            }

            public string Name { get; }
            public MethodInfo Method { get; }
            public string Where => Describe(Method);
        }

        private static string Describe(MethodInfo method) =>
            $"{method.DeclaringType?.Name}.{method.Name}";

        // Mirrors ToolRegistry.ScanAssemblies on purpose, minus its dedupe: a name claimed twice is
        // dropped there behind a console warning, which is the thing NoTwoProviders has to see.
        private static List<DeclaredTool> DeclaredTools()
        {
            var tools = new List<DeclaredTool>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                    continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    // Per type, because the registry does the same: one unloadable type in an
                    // unrelated assembly threw "bad metadata" out of GetMethods and cost the sweep
                    // every tool after it.
                    try
                    {
                        if (type.GetCustomAttribute<ToolProviderAttribute>() == null)
                            continue;

                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            tools.Add(new DeclaredTool(ToolRegistry.ToSnakeCase(method.Name), method));
                    }
                    catch
                    {
                        // ignored, exactly as ToolRegistry.ScanAssemblies does
                    }
                }
            }

            return tools;
        }

        private static IEnumerable<KeyValuePair<string, MethodInfo>> RegisteredTools() =>
            ToolRegistry.MethodCache.OrderBy(entry => entry.Key, StringComparer.Ordinal);

        [Test]
        public void ThereIsSomethingToCheck()
        {
            // Every other test in this file passes vacuously on an empty registry, which is exactly
            // what a scan that silently caught an exception would leave behind.
            Assert.Greater(ToolRegistry.MethodCache.Count, 100,
                "The registry found almost no tools, so the invariants below are not proving anything.");
        }

        [Test]
        public void NoTwoProvidersClaimTheSameToolName()
        {
            var violations = DeclaredTools()
                .GroupBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => $"'{group.Key}': {string.Join(" and ", group.Select(tool => tool.Where))}")
                .ToList();

            AssertNone(violations,
                "duplicate tool name(s). The registry keeps whichever it scans first and drops the rest " +
                "behind a console warning, so the losing tool is simply not callable");
        }

        [Test]
        public void NoToolComesFromAPropertyAccessorOrAnOperator()
        {
            var violations = DeclaredTools()
                .Where(tool => tool.Method.IsSpecialName)
                .Select(tool => $"{tool.Where} -> '{tool.Name}'")
                .ToList();

            AssertNone(violations,
                "tool(s) generated from a property accessor or an operator. Every public static member " +
                "of a [ToolProvider] becomes a tool, so a helper property ships as one");
        }

        [Test]
        public void EveryToolCarriesADescriptionAnAgentCanActOn()
        {
            var violations = new List<string>();

            foreach (var entry in RegisteredTools())
            {
                var description = entry.Value.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (string.IsNullOrWhiteSpace(description))
                    violations.Add($"{entry.Key} ({Describe(entry.Value)})");
            }

            AssertNone(violations,
                "tool(s) with no [Description]. The schema then falls back to the method name spelled out, " +
                "which tells an agent nothing about when to call it");
        }

        [Test]
        public void EveryToolParameterCarriesADescription()
        {
            var violations = new List<string>();

            foreach (var entry in RegisteredTools())
            foreach (var parameter in entry.Value.GetParameters())
            {
                var description = parameter.GetCustomAttribute<ToolParamAttribute>()?.Description;
                if (string.IsNullOrWhiteSpace(description))
                    violations.Add($"{entry.Key}({parameter.Name})");
            }

            AssertNone(violations,
                "parameter(s) with no [ToolParam] description. The schema falls back to the parameter name " +
                "spelled out, so the agent has to guess the format and gets INVALID_PARAM instead");
        }

        // Everything FunctionInvoker.ConvertValue knows how to build. Convert.ChangeType catches the
        // numeric stragglers; an array, a List<> or a Unity object throws there instead.
        private static readonly HashSet<Type> ConvertibleParameterTypes = new HashSet<Type>
        {
            typeof(string), typeof(bool),
            typeof(int), typeof(long), typeof(short),
            typeof(float), typeof(double), typeof(decimal),
            typeof(Vector2), typeof(Vector3), typeof(Color)
        };

        [Test]
        public void EveryParameterTypeCanBeBuiltFromTheStringTheClientSends()
        {
            var violations = new List<string>();

            foreach (var entry in RegisteredTools())
            foreach (var parameter in entry.Value.GetParameters())
            {
                var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                if (type.IsEnum || ConvertibleParameterTypes.Contains(type))
                    continue;

                violations.Add($"{entry.Key}({parameter.Name}) is a {type.Name}");
            }

            AssertNone(violations,
                "parameter(s) of a type ConvertValue cannot build. Arguments arrive as strings, so such a " +
                "parameter can only ever answer INVALID_PARAM - pass a comma-separated string instead");
        }

        // Whether a schema-required argument is actually mandatory cannot be read off the declaration:
        // BuildArguments fills a compile-time default before it looks at Required, so a tool with one
        // may still refuse the call in its own body. ToolInvocationSmokeTests settles it by omitting the
        // argument and looking at the answer.

        [Test]
        public void EveryToolParameterKeepsItsOwnSlotAfterSnakeCasing()
        {
            var violations = new List<string>();

            foreach (var entry in RegisteredTools())
            {
                var duplicates = entry.Value.GetParameters()
                    .GroupBy(parameter => ToolRegistry.ToSnakeCase(parameter.Name), StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1);

                foreach (var group in duplicates)
                    violations.Add($"{entry.Key}: {string.Join(" and ", group.Select(p => p.Name))} both map to '{group.Key}'");
            }

            AssertNone(violations,
                "parameter name collision(s). The schema keys properties by the snake_case name, so one of " +
                "the two disappears from it and can never be passed");
        }

        // What FunctionInvoker.SerializeResult turns into a { success, ... } envelope: a string (wrapped),
        // null/void (wrapped), or an object that is expected to BE one. Anything else is JSON-encoded
        // as-is and reaches the client with no success field for it to branch on.
        [Test]
        public void EveryReturnTypeEndsUpAsTheStandardResponseEnvelope()
        {
            var allowed = new HashSet<Type>
            {
                typeof(void), typeof(object), typeof(string),
                typeof(Task), typeof(Task<object>), typeof(Task<string>)
            };

            var violations = RegisteredTools()
                .Where(entry => !allowed.Contains(entry.Value.ReturnType))
                .Select(entry => $"{entry.Key} returns {entry.Value.ReturnType.Name}")
                .ToList();

            AssertNone(violations,
                "tool(s) whose return value is serialized as-is. Return object (a Response) or string so " +
                "every answer carries `success`");
        }

        [Test]
        public void ALongRunningBudgetIsBiggerThanTheDefaultOrItDoesNothing()
        {
            var violations = new List<string>();

            foreach (var entry in RegisteredTools())
            {
                var budget = entry.Value.GetCustomAttribute<LongRunningToolAttribute>()?.Seconds;
                if (budget == null || budget > ToolRegistry.DefaultToolTimeoutSeconds)
                    continue;

                violations.Add($"{entry.Key} asks for {budget}s against a {ToolRegistry.DefaultToolTimeoutSeconds}s default");
            }

            AssertNone(violations,
                "long-running budget(s) at or below the default. TimeoutSecondsForRequest keeps the larger " +
                "of the two, so the attribute changes nothing and reads as if it did");
        }

        private static void AssertNone(List<string> violations, string what)
        {
            if (violations.Count == 0)
                return;

            violations.Sort(StringComparer.Ordinal);
            Assert.Fail($"{violations.Count} {what}:{Environment.NewLine}  " +
                        string.Join(Environment.NewLine + "  ", violations));
        }
    }
}
