// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Reflection;
using KitWright.Editor.Api.Models;
using KitWright.Editor.Tools;
using NUnit.Framework;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class FunctionInvokerTests
    {
        [Test]
        public void Invoke_RejectsMalformedTypedParameter()
        {
            var result = new FunctionInvoker().Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy",
                Parameters = new Dictionary<string, string> { ["depth"] = "not-a-number" }
            });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"INVALID_PARAM\"", result);
            StringAssert.Contains("\"param\":\"depth\"", result);
        }

        [Test]
        public void Invoke_RejectsMissingRequiredParameter()
        {
            var result = new FunctionInvoker().Invoke(new FunctionCall
            {
                FunctionName = "simulate_mouse_click",
                Parameters = new Dictionary<string, string> { ["y"] = "12" }
            });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"MISSING_PARAM\"", result);
            StringAssert.Contains("\"param\":\"x\"", result);
        }

        [Test]
        public void Invoke_RejectsUnknownParameterInsteadOfRunningOnDefaults()
        {
            var result = new FunctionInvoker().Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy",
                Parameters = new Dictionary<string, string> { ["dpeth"] = "2" }
            });

            StringAssert.Contains("\"code\":\"UNKNOWN_PARAM\"", result);
            StringAssert.Contains("\"param\":\"dpeth\"", result);
            StringAssert.Contains("depth", result, "The error must list the names the tool does accept.");
        }

        [Test]
        public void Invoke_RejectsUnknownParameterBeforeReportingAMissingOne()
        {
            var result = new FunctionInvoker().Invoke(new FunctionCall
            {
                FunctionName = "simulate_mouse_click",
                Parameters = new Dictionary<string, string> { ["ex"] = "1", ["y"] = "2" }
            });

            StringAssert.Contains("\"code\":\"UNKNOWN_PARAM\"", result);
        }

        [Test]
        public void FindGameObjects_AmbiguousComponentNameIsReportedNotReturnedAsNoMatches()
        {
            var result = new FunctionInvoker().Invoke(new FunctionCall
            {
                FunctionName = "find_game_objects",
                Parameters = new Dictionary<string, string>
                {
                    ["query"] = "TypeResolverProbeDuplicate",
                    ["find_method"] = "by_component"
                }
            });

            StringAssert.Contains("\"code\":\"AMBIGUOUS_TYPE\"", result);
            StringAssert.Contains("TypeResolverLeft.TypeResolverProbeDuplicate", result);
        }

        [Test]
        public void Invoke_WrapsLegacyStringSuccess()
        {
            var result = new FunctionInvoker().Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy"
            });

            StringAssert.Contains("\"success\":true", result);
            StringAssert.Contains("\"message\":", result);
        }

        [Test]
        public void Invoke_RejectsMalformedVectorBeforeChangingSceneObjects()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var existing = new GameObject("InvokerVectorTarget_" + suffix);
            existing.transform.position = new Vector3(4f, 5f, 6f);

            try
            {
                var invoker = new FunctionInvoker();
                var setResult = invoker.Invoke(new FunctionCall
                {
                    FunctionName = "set_transform",
                    Parameters = new Dictionary<string, string>
                    {
                        ["target"] = existing.name,
                        ["position"] = "1,2"
                    }
                });

                StringAssert.Contains("\"code\":\"INVALID_PARAM\"", setResult);
                Assert.AreEqual(new Vector3(4f, 5f, 6f), existing.transform.position);

                var createName = "InvokerInvalidPrimitive_" + suffix;
                var createResult = invoker.Invoke(new FunctionCall
                {
                    FunctionName = "create_primitive",
                    Parameters = new Dictionary<string, string>
                    {
                        ["primitive_type"] = "Cube",
                        ["name"] = createName,
                        ["position"] = "1,2"
                    }
                });

                StringAssert.Contains("\"code\":\"INVALID_PARAM\"", createResult);
                Assert.IsNull(GameObject.Find(createName));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(existing);
            }
        }

        [Test]
        public void CreatePrimitive_AppliesRotationAndParentInOneCall()
        {
            var suffix = Guid.NewGuid().ToString("N");
            var parent = new GameObject("PrimParent_" + suffix);
            var childName = "PrimChild_" + suffix;
            var orphanName = "PrimOrphan_" + suffix;

            try
            {
                var invoker = new FunctionInvoker();
                var created = invoker.Invoke(new FunctionCall
                {
                    FunctionName = "create_primitive",
                    Parameters = new Dictionary<string, string>
                    {
                        ["primitive_type"] = "Cube",
                        ["name"] = childName,
                        ["rotation"] = "0,90,0",
                        ["parent"] = parent.name
                    }
                });

                StringAssert.Contains("\"success\":true", created);
                var child = parent.transform.Find(childName);
                Assert.IsNotNull(child, "The primitive must be created under the requested parent.");
                Assert.AreEqual(90f, child.eulerAngles.y, 0.01f);

                var missingParent = invoker.Invoke(new FunctionCall
                {
                    FunctionName = "create_primitive",
                    Parameters = new Dictionary<string, string>
                    {
                        ["primitive_type"] = "Cube",
                        ["name"] = orphanName,
                        ["parent"] = "NoSuchParent_" + suffix
                    }
                });

                StringAssert.Contains("\"code\":\"PARENT_NOT_FOUND\"", missingParent);
                Assert.IsNull(GameObject.Find(orphanName), "A rejected parent must not leave a primitive behind.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void WrapLegacyStringResult_PreservesImagesAndExistingEnvelopes()
        {
            const string image = "data:image/png;base64,AA==";
            const string envelope = "{\"success\":false,\"code\":\"EXPECTED\"}";

            Assert.AreEqual(image, FunctionInvoker.WrapLegacyStringResult(image));
            Assert.AreEqual(envelope, FunctionInvoker.WrapLegacyStringResult(envelope));
        }

        [Test]
        public void Invoke_ValidatesAndWrapsManualToolResults()
        {
            var toolName = "test_manual_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                name = toolName,
                description = "Test manual tool",
                parameters = new ToolParametersDef
                {
                    required = new List<string> { "value" }
                }
            };

            ToolRegistry.Register(toolName, definition, parameters => "manual:" + parameters["value"]);
            try
            {
                var invoker = new FunctionInvoker();
                var missing = invoker.Invoke(new FunctionCall { FunctionName = toolName });
                StringAssert.Contains("\"code\":\"MISSING_PARAM\"", missing);

                var success = invoker.Invoke(new FunctionCall
                {
                    FunctionName = toolName,
                    Parameters = new Dictionary<string, string> { ["value"] = "ok" }
                });
                StringAssert.Contains("\"success\":true", success);
                StringAssert.Contains("manual:ok", success);
            }
            finally
            {
                ToolRegistry.Unregister(toolName);
            }
        }

        // A third-party attribute may derive from ToolProviderAttribute and fail to construct
        // (stale DLL, missing base assembly). Reading it must not cost us the rest of the scan.
        [AttributeUsage(AttributeTargets.Class)]
        private sealed class ExplodingToolProviderAttribute : ToolProviderAttribute
        {
            public ExplodingToolProviderAttribute() { throw new InvalidOperationException("bad metadata"); }
        }

        [ExplodingToolProvider]
        private static class BoomProvider { }

        [Test]
        public void ToolRegistry_SkipsTypesWhoseToolProviderAttributeThrows()
        {
            Assert.Catch(
                () => typeof(BoomProvider).GetCustomAttribute<ToolProviderAttribute>(),
                "Raw attribute reads on this type throw; the registry must not do a raw read.");

            // BoomProvider lives in this assembly, so a raw read anywhere in the scan throws here.
            Assert.DoesNotThrow(() => ToolRegistry.ScanAssemblies());
        }

        private sealed class ThrowingAssembly : Assembly
        {
            public override bool IsDynamic => false;
            public override Type[] GetTypes() => throw new TypeLoadException("unresolvable dependency");
        }

        [Test]
        public void ScanAssemblies_BrokenAssemblyDoesNotDropBuiltins()
        {
            try
            {
                ToolRegistry.ScanAssemblies(new Assembly[]
                {
                    new ThrowingAssembly(),
                    typeof(FunctionInvoker).Assembly
                });

                Assert.IsTrue(ToolRegistry.MethodCache.ContainsKey("create_script"),
                    "One unloadable assembly must not remove the builtin tools scanned after it.");
            }
            finally
            {
                ToolRegistry.ScanAssemblies();
            }
        }
    }
}
