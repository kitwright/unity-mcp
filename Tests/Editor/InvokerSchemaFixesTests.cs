// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using KitWright.Editor.Api.Models;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    // Stands in for a tool a project declares: scanned only when a test passes this assembly in.
    [ToolProvider("Test")]
    public static class InvokerSchemaProbeProvider
    {
        public static object InvokerSchemaProbe(
            [ToolParam("Mandatory")] string name,
            [ToolParam("Optional by flag", Required = false)] string note,
            [ToolParam("Defaulted, no Required flag")] int count = 5,
            [ToolParam("Defaulted null, no Required flag")] string label = null,
            string bare = null) => null;

        public static object InvokerSchemaFlagsProbe(
            [ToolParam("Pick")] InvokerSchemaFixesTests.ProbeFlags flags,
            [ToolParam("Pick one")] InvokerSchemaFixesTests.ProbePlain plain) => null;

        public static string InvokerSchemaProbeProperty { get; set; }
    }

    public sealed class InvokerSchemaFixesTests
    {
        [Flags]
        public enum ProbeFlags { None = 0, Alpha = 1, Beta = 2, Gamma = 4 }

        public enum ProbePlain { One = 1, Two = 2, Three = 3 }

        private const string ProbeTool = "invoker_schema_probe";

        private static void ScanWithTheProbe() =>
            ToolRegistry.ScanAssemblies(new[] { typeof(ToolRegistry).Assembly, typeof(InvokerSchemaFixesTests).Assembly });

        private static ToolDefinition Declared(string name, params string[] properties)
        {
            var definition = new ToolDefinition
            {
                name = name,
                description = "Invoker schema probe",
                parameters = new ToolParametersDef()
            };

            foreach (var property in properties)
                definition.parameters.properties[property] = new ToolPropertyDef { type = "string", description = property };

            return definition;
        }

        private static string Invoke(string tool, params (string Key, string Value)[] arguments)
        {
            var parameters = new Dictionary<string, string>();
            foreach (var (key, value) in arguments)
                parameters[key] = value;

            return new FunctionInvoker().Invoke(new FunctionCall { FunctionName = tool, Parameters = parameters });
        }

        [Test]
        public void ADefaultedParameterIsNotListedAsRequired()
        {
            var method = typeof(InvokerSchemaProbeProvider).GetMethod(nameof(InvokerSchemaProbeProvider.InvokerSchemaProbe));

            var schema = ToolSchemaBuilder.BuildFromMethod(ProbeTool, method).parameters;

            CollectionAssert.AreEqual(new[] { "name" }, schema.required);
            CollectionAssert.AreEquivalent(new[] { "name", "note", "count", "label", "bare" }, schema.properties.Keys);
        }

        [Test]
        public void AFlagsEnumIsNotConstrainedToOneName()
        {
            var method = typeof(InvokerSchemaProbeProvider).GetMethod(nameof(InvokerSchemaProbeProvider.InvokerSchemaFlagsProbe));

            var properties = ToolSchemaBuilder.BuildFromMethod("invoker_schema_flags_probe", method).parameters.properties;

            Assert.IsNull(properties["flags"].@enum, "An enum list would forbid \"Alpha,Beta\" for a client that obeys the schema.");
            StringAssert.Contains("Gamma", properties["flags"].description);
            CollectionAssert.AreEqual(new[] { "One", "Two", "Three" }, properties["plain"].@enum);
        }

        [Test]
        public void Decimal_DoesNotReadACommaAsAGroupSeparator()
        {
            Assert.AreEqual(0.5m, FunctionInvoker.ConvertValue("0.5", typeof(decimal)));
            Assert.Catch(() => FunctionInvoker.ConvertValue("0,5", typeof(decimal)));
        }

        [TestCase("True", true)]
        [TestCase("TRUE", true)]
        [TestCase("true", true)]
        [TestCase("Yes", true)]
        [TestCase("1", true)]
        [TestCase("False", false)]
        [TestCase("FALSE", false)]
        [TestCase("No", false)]
        [TestCase("0", false)]
        public void Bool_IsReadWhateverTheCase(string text, bool expected)
        {
            Assert.AreEqual(expected, FunctionInvoker.ConvertValue(text, typeof(bool)));
        }

        [Test]
        public void Bool_RefusesWhatIsNotABoolean()
        {
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("maybe", typeof(bool)));
        }

        [Test]
        public void FlagsEnum_TakesACommaSeparatedListOfDefinedNames()
        {
            Assert.AreEqual(ProbeFlags.Alpha | ProbeFlags.Beta, FunctionInvoker.ConvertValue("Alpha,Beta", typeof(ProbeFlags)));
            Assert.AreEqual(ProbeFlags.Alpha | ProbeFlags.Gamma, FunctionInvoker.ConvertValue(" alpha , GAMMA ", typeof(ProbeFlags)));
            Assert.AreEqual(ProbeFlags.Beta, FunctionInvoker.ConvertValue("Beta", typeof(ProbeFlags)));
        }

        [Test]
        public void FlagsEnum_RefusesAnUndefinedNameOrANumberInTheList()
        {
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("Alpha,Nope", typeof(ProbeFlags)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("Alpha,4", typeof(ProbeFlags)));
        }

        // Two and One OR to Three, a defined value, so Enum.Parse alone read the pair as a single Three.
        [Test]
        public void PlainEnum_StillRefusesAList()
        {
            Assert.AreEqual(ProbePlain.Two, FunctionInvoker.ConvertValue("two", typeof(ProbePlain)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("One,Two", typeof(ProbePlain)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("999", typeof(ProbePlain)));
        }

        [Test]
        public void ArrayAndListParametersReadAJsonArray()
        {
            CollectionAssert.AreEqual(new[] { "a", "b" },
                (string[])FunctionInvoker.ConvertValue("[\"a\",\"b\"]", typeof(string[])));
            CollectionAssert.AreEqual(new List<int> { 1, 2 },
                (List<int>)FunctionInvoker.ConvertValue("[1,2]", typeof(List<int>)));
            Assert.Catch(() => FunctionInvoker.ConvertValue("a,b", typeof(string[])));
            Assert.Catch(() => FunctionInvoker.ConvertValue("[\"x\"]", typeof(List<int>)));
        }

        [Test]
        public void IntegralArrayElements_AreRefusedUnlessTheyAreJsonIntegers()
        {
            foreach (var bad in new[] { "[1.5]", "[1,2.25]", "[\"3\"]", "[\"a\"]", "[true]", "[null]", "[[1]]" })
            {
                Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue(bad, typeof(int[])), bad);
                Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue(bad, typeof(List<long>)), bad);
            }

            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("[1.5]", typeof(byte[])));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("[1.5]", typeof(ulong[])));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("[0.5]", typeof(short[])));
        }

        [Test]
        public void IntegralArrayRefusal_NamesTheElementAndItsValue()
        {
            var message = Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("[1,2.5]", typeof(int[]))).Message;

            StringAssert.Contains("element 1", message);
            StringAssert.Contains("2.5", message);
        }

        [Test]
        public void IntegralArrayElements_TakeIntegersAndAFloatWithoutAFractionalPart()
        {
            CollectionAssert.AreEqual(new[] { 3, 4, -5 }, (int[])FunctionInvoker.ConvertValue("[3.0, 4, -5]", typeof(int[])));
            CollectionAssert.AreEqual(new long[] { 9007199254740993, 2 },
                (long[])FunctionInvoker.ConvertValue("[9007199254740993, 2.0]", typeof(long[])));
            Assert.IsEmpty((int[])FunctionInvoker.ConvertValue("[]", typeof(int[])));
        }

        [Test]
        public void NonIntegralElementTypesKeepTheirReading()
        {
            CollectionAssert.AreEqual(new[] { 1.5, 2.0 }, (double[])FunctionInvoker.ConvertValue("[1.5, 2]", typeof(double[])));
            CollectionAssert.AreEqual(new List<float> { 0.5f }, (List<float>)FunctionInvoker.ConvertValue("[0.5]", typeof(List<float>)));
            CollectionAssert.AreEqual(new[] { "3" }, (string[])FunctionInvoker.ConvertValue("[\"3\"]", typeof(string[])));
        }

        [Test]
        public void Vector4AndQuaternionReadFourNumbers()
        {
            Assert.AreEqual(new Vector4(1f, 2f, 3f, 4f), FunctionInvoker.ConvertValue("1,2,3,4", typeof(Vector4)));
            Assert.AreEqual(new Vector4(1f, 2f, 3f, 4f), FunctionInvoker.ConvertValue("(1, 2, 3, 4)", typeof(Vector4)));
            Assert.AreEqual(new Quaternion(0f, 0f, 0f, 1f), FunctionInvoker.ConvertValue("0,0,0,1", typeof(Quaternion)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("1,2,3", typeof(Vector4)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("1,2,3,NaN", typeof(Quaternion)));
        }

        [Test]
        public void Quaternion_RefusesTheZeroQuaternionButVector4TakesIt()
        {
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("0,0,0,0", typeof(Quaternion)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("(0, 0, 0, -0)", typeof(Quaternion)));
            Assert.AreEqual(new Quaternion(0f, 0f, 0f, 1f), FunctionInvoker.ConvertValue("0,0,0,1", typeof(Quaternion)));
            Assert.AreEqual(new Quaternion(0f, 0f, 0f, 0.5f), FunctionInvoker.ConvertValue("0,0,0,0.5", typeof(Quaternion)));
            Assert.AreEqual(Vector4.zero, FunctionInvoker.ConvertValue("0,0,0,0", typeof(Vector4)));
        }

        [Test]
        public void Color_RefusesNonFiniteComponentsAndBlankText()
        {
            Assert.AreEqual(new Color(1f, 0f, 0f, 1f), FunctionInvoker.ConvertValue("1,0,0", typeof(Color)));
            Assert.AreEqual(new Color(1f, 0f, 0f, 1f), FunctionInvoker.ConvertValue("#ff0000", typeof(Color)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("NaN,0,0", typeof(Color)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("1,Infinity,0", typeof(Color)));
            Assert.Throws<FormatException>(() => FunctionInvoker.ConvertValue("  ", typeof(Color)));
        }

        [Test]
        public void ManualTool_RefusesAParameterItNeverDeclared()
        {
            var name = "manual_unknown_" + Guid.NewGuid().ToString("N");
            var ran = false;
            ToolRegistry.Register(name, Declared(name, "value"), _ => { ran = true; return "ran"; });

            try
            {
                var refused = Invoke(name, ("valeu", "x"));

                StringAssert.Contains("\"code\":\"UNKNOWN_PARAM\"", refused);
                StringAssert.Contains("\"param\":\"valeu\"", refused);
                StringAssert.Contains("'valeu' -> 'value'", refused);
                Assert.IsFalse(ran, "A refused call must not reach the handler.");

                StringAssert.Contains("\"success\":true", Invoke(name, ("value", "x")));
                Assert.IsTrue(ran);
            }
            finally { ToolRegistry.Unregister(name); }
        }

        [Test]
        public void ManualTool_NamesTheMisspellingBeforeTheMissingParameter()
        {
            var name = "manual_order_" + Guid.NewGuid().ToString("N");
            var definition = Declared(name, "value");
            definition.parameters.required.Add("value");
            ToolRegistry.Register(name, definition, _ => "ok");

            try
            {
                StringAssert.Contains("\"code\":\"UNKNOWN_PARAM\"", Invoke(name, ("valeu", "x")));
                StringAssert.Contains("\"code\":\"MISSING_PARAM\"", Invoke(name));
            }
            finally { ToolRegistry.Unregister(name); }
        }

        [Test]
        public void ManualTool_ThatDeclaresNothingStillTakesAnyParameter()
        {
            var name = "manual_open_" + Guid.NewGuid().ToString("N");
            ToolRegistry.Register(name, new ToolDefinition { name = name }, p => "got:" + p["anything"]);

            try
            {
                var result = Invoke(name, ("anything", "1"));

                StringAssert.Contains("\"success\":true", result);
                StringAssert.Contains("got:1", result);
            }
            finally { ToolRegistry.Unregister(name); }
        }

        [Test]
        public void ManualTool_WithNoParametersDefinitionExportsAndIsExposedUnderEveryProfile()
        {
            var name = "manual_export_" + Guid.NewGuid().ToString("N");
            ToolRegistry.Register(name, new ToolDefinition { description = "No parameters, no name" }, _ => "ok");

            try
            {
                var tool = new MCPToolExporter(null).ExportTools().FirstOrDefault(t => (string)t["name"] == name);

                Assert.IsNotNull(tool, "A manual tool without a parameters definition took the whole tools/list down or vanished from it.");
                Assert.AreEqual("object", ((Dictionary<string, object>)tool["inputSchema"])["type"]);

                foreach (MCPToolExportProfile profile in Enum.GetValues(typeof(MCPToolExportProfile)))
                {
                    Assert.IsTrue(
                        MCPToolExportPolicy.IsToolAllowed(name, profile, profileConfigured: false, profileTools: null),
                        $"A tool the project registered by hand must not be hidden by the {profile} profile.");
                }

                Assert.IsFalse(MCPToolExportPolicy.IsToolAllowed(
                    name, MCPToolExportProfile.Core, profileConfigured: true, profileTools: new[] { "execute_code" }),
                    "A hand-picked list still decides.");
            }
            finally { ToolRegistry.Unregister(name); }

            Assert.IsFalse(MCPToolExportPolicy.IsToolAllowed(
                name, MCPToolExportProfile.Core, profileConfigured: false, profileTools: null));
        }

        [Test]
        public void ScanAssemblies_DoesNotTurnPropertyAccessorsIntoTools()
        {
            var property = typeof(InvokerSchemaProbeProvider).GetProperty(nameof(InvokerSchemaProbeProvider.InvokerSchemaProbeProperty));

            try
            {
                ScanWithTheProbe();

                Assert.IsNotNull(ToolRegistry.GetMethod(ProbeTool));
                Assert.IsNull(ToolRegistry.GetMethod(ToolRegistry.ToSnakeCase(property.GetMethod.Name)));
                Assert.IsNull(ToolRegistry.GetMethod(ToolRegistry.ToSnakeCase(property.SetMethod.Name)));
            }
            finally { ToolRegistry.ScanAssemblies(); }
        }

        private sealed class PartiallyLoadableAssembly : Assembly
        {
            public override bool IsDynamic => false;
            public override AssemblyName GetName(bool copiedName) => new AssemblyName("PartiallyLoadableFake");
            public override AssemblyName[] GetReferencedAssemblies() => new[] { typeof(ToolRegistry).Assembly.GetName() };

            public override Type[] GetTypes() => throw new ReflectionTypeLoadException(
                new[] { typeof(InvokerSchemaProbeProvider), null },
                new Exception[] { new TypeLoadException("missing dependency") });
        }

        [Test]
        public void ScanAssemblies_KeepsTheTypesThatLoadedAndSaysWhichAssemblyLostSome()
        {
            try
            {
                LogAssert.Expect(LogType.Warning, new Regex("PartiallyLoadableFake"));

                ToolRegistry.ScanAssemblies(new Assembly[] { new PartiallyLoadableAssembly(), typeof(FunctionInvoker).Assembly });

                Assert.IsNotNull(ToolRegistry.GetMethod(ProbeTool), "The loadable half of the assembly was dropped with the rest.");
                Assert.IsNotNull(ToolRegistry.GetMethod("create_script"));
            }
            finally { ToolRegistry.ScanAssemblies(); }
        }

        [Test]
        public void DuplicateToolMessage_NamesBothAssemblies()
        {
            var kept = typeof(ToolRegistry).GetMethod(nameof(ToolRegistry.ToSnakeCase));
            var ignored = typeof(InvokerSchemaProbeProvider).GetMethod(nameof(InvokerSchemaProbeProvider.InvokerSchemaProbe));

            var message = ToolRegistry.DuplicateToolMessage(ProbeTool, kept, ignored);

            StringAssert.Contains(ProbeTool, message);
            StringAssert.Contains(typeof(ToolRegistry).Assembly.GetName().Name, message);
            StringAssert.Contains(typeof(InvokerSchemaProbeProvider).Assembly.GetName().Name, message);
            StringAssert.Contains(typeof(ToolRegistry).FullName, message);
            StringAssert.Contains(typeof(InvokerSchemaProbeProvider).FullName, message);
        }
    }
}
