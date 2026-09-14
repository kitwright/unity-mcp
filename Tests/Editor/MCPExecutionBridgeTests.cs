// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using KitWright.Editor.Api.Models;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// Integration tests that exercise <see cref="FunctionInvoker"/> end-to-end
    /// with manual tool registration, unknown function handling, parameter validation,
    /// and result serialization (structured object vs legacy string).
    /// </summary>
    public sealed class MCPExecutionBridgeTests
    {
        // ------------------------------------------------------------------
        //  1. Unknown function → UNKNOWN_FUNCTION error
        // ------------------------------------------------------------------

        [Test]
        public void Invoke_UnknownFunction_ReturnsUnknownFunctionError()
        {
            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(new FunctionCall
            {
                FunctionName = "totally_nonexistent_tool_" + Guid.NewGuid().ToString("N")
            });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"UNKNOWN_FUNCTION\"", result);
        }

        [Test]
        public void Invoke_NullFunctionCall_ReturnsNullFunctionCallError()
        {
            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(null);

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"NULL_FUNCTION_CALL\"", result);
        }

        [Test]
        public void Invoke_EmptyFunctionName_ReturnsFunctionNameRequiredError()
        {
            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(new FunctionCall { FunctionName = "" });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"FUNCTION_NAME_REQUIRED\"", result);
        }

        [Test]
        public void Invoke_WhitespaceFunctionName_ReturnsFunctionNameRequiredError()
        {
            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(new FunctionCall { FunctionName = "   " });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"FUNCTION_NAME_REQUIRED\"", result);
        }

        // ------------------------------------------------------------------
        //  2. Invalid typed parameter → INVALID_PARAM error
        // ------------------------------------------------------------------

        [TestCase("not-a-number", "depth")]
        [TestCase("abc", "depth")]
        [TestCase("1.5.3", "depth")]
        public void Invoke_GetHierarchy_InvalidDepth_ReturnsInvalidParamError(string depthValue, string expectedParam)
        {
            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy",
                Parameters = new Dictionary<string, string> { ["depth"] = depthValue }
            });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"INVALID_PARAM\"", result);
            StringAssert.Contains($"\"param\":\"{expectedParam}\"", result);
        }

        [TestCase("not-a-bool", "include_components")]
        [TestCase("maybe", "include_inactive")]
        public void Invoke_GetHierarchy_InvalidBoolParam_ReturnsInvalidParamError(string boolValue, string paramName)
        {
            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy",
                Parameters = new Dictionary<string, string> { [paramName] = boolValue }
            });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"INVALID_PARAM\"", result);
            StringAssert.Contains($"\"param\":\"{paramName}\"", result);
        }

        // ------------------------------------------------------------------
        //  3. Manual tool registration → invoke → structured response
        // ------------------------------------------------------------------

        [Test]
        public void ManualTool_RegisterAndInvoke_ReturnsStructuredSuccessResponse()
        {
            var toolName = "test_integration_manual_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                name = toolName,
                description = "Integration test manual tool",
                parameters = new ToolParametersDef
                {
                    required = new List<string> { "input" }
                }
            };

            ToolRegistry.Register(toolName, definition, parameters =>
                "echo:" + parameters["input"]);

            try
            {
                var invoker = new FunctionInvoker();
                var result = invoker.Invoke(new FunctionCall
                {
                    FunctionName = toolName,
                    Parameters = new Dictionary<string, string> { ["input"] = "hello_world" }
                });

                StringAssert.Contains("\"success\":true", result);
                StringAssert.Contains("echo:hello_world", result);
            }
            finally
            {
                ToolRegistry.Unregister(toolName);
            }
        }

        [Test]
        public void ManualTool_MissingRequiredParam_ReturnsMissingParamError()
        {
            var toolName = "test_missing_param_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                name = toolName,
                description = "Tool with required params",
                parameters = new ToolParametersDef
                {
                    required = new List<string> { "alpha", "beta" }
                }
            };

            ToolRegistry.Register(toolName, definition, parameters => "ok");

            try
            {
                var invoker = new FunctionInvoker();

                // Send only alpha, missing beta
                var result = invoker.Invoke(new FunctionCall
                {
                    FunctionName = toolName,
                    Parameters = new Dictionary<string, string> { ["alpha"] = "1" }
                });

                StringAssert.Contains("\"success\":false", result);
                StringAssert.Contains("\"code\":\"MISSING_PARAM\"", result);
                StringAssert.Contains("beta", result);
            }
            finally
            {
                ToolRegistry.Unregister(toolName);
            }
        }

        [Test]
        public void ManualTool_NoParams_WhenNoneRequired_ReturnsSuccess()
        {
            var toolName = "test_no_params_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                name = toolName,
                description = "Tool with no required params",
                parameters = new ToolParametersDef()
            };

            ToolRegistry.Register(toolName, definition, parameters => "no-params-ok");

            try
            {
                var invoker = new FunctionInvoker();
                var result = invoker.Invoke(new FunctionCall
                {
                    FunctionName = toolName,
                    Parameters = new Dictionary<string, string>()
                });

                StringAssert.Contains("\"success\":true", result);
                StringAssert.Contains("no-params-ok", result);
            }
            finally
            {
                ToolRegistry.Unregister(toolName);
            }
        }

        [Test]
        public void ManualTool_HandlerThrowsException_ReturnsManualToolFailedError()
        {
            var toolName = "test_throws_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                name = toolName,
                description = "Tool that throws",
                parameters = new ToolParametersDef()
            };

            ToolRegistry.Register(toolName, definition,
                parameters => throw new InvalidOperationException("boom"));

            try
            {
                LogAssert.Expect(LogType.Error, $"[KitWright] Manual tool '{toolName}' failed: boom");

                var invoker = new FunctionInvoker();
                var result = invoker.Invoke(new FunctionCall
                {
                    FunctionName = toolName,
                    Parameters = new Dictionary<string, string>()
                });

                StringAssert.Contains("\"success\":false", result);
                StringAssert.Contains("\"code\":\"MANUAL_TOOL_FAILED\"", result);
                StringAssert.Contains("boom", result);
            }
            finally
            {
                ToolRegistry.Unregister(toolName);
            }
        }

        // ------------------------------------------------------------------
        //  4. Tool returning object (via Response.Success) → serialized JSON
        // ------------------------------------------------------------------

        [Test]
        public void Invoke_GetHierarchy_DefaultParams_ReturnsSuccessEnvelope()
        {
            // get_hierarchy returns a string that gets wrapped via WrapLegacyStringResult
            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(new FunctionCall
            {
                FunctionName = "get_hierarchy"
            });

            StringAssert.Contains("\"success\":true", result);
            StringAssert.Contains("\"message\":", result);
        }

        // ------------------------------------------------------------------
        //  5. WrapLegacyStringResult edge cases
        // ------------------------------------------------------------------

        [Test]
        public void WrapLegacyStringResult_NullInput_ReturnsSuccessOK()
        {
            var result = FunctionInvoker.WrapLegacyStringResult(null);

            StringAssert.Contains("\"success\":true", result);
            StringAssert.Contains("OK", result);
        }

        [Test]
        public void WrapLegacyStringResult_PlainString_WrapsInSuccessEnvelope()
        {
            var result = FunctionInvoker.WrapLegacyStringResult("Hello World");

            StringAssert.Contains("\"success\":true", result);
            StringAssert.Contains("Hello World", result);
        }

        [Test]
        public void WrapLegacyStringResult_DataUri_PassesThroughUnchanged()
        {
            const string dataUri = "data:image/png;base64,iVBOR==";
            var result = FunctionInvoker.WrapLegacyStringResult(dataUri);

            Assert.AreEqual(dataUri, result);
        }

        [Test]
        public void WrapLegacyStringResult_ExistingSuccessEnvelope_PassesThroughUnchanged()
        {
            const string envelope = "{\"success\":true,\"message\":\"already wrapped\"}";
            var result = FunctionInvoker.WrapLegacyStringResult(envelope);

            Assert.AreEqual(envelope, result);
        }

        [Test]
        public void WrapLegacyStringResult_ExistingErrorEnvelope_PassesThroughUnchanged()
        {
            const string errorEnvelope = "{\"success\":false,\"code\":\"SOME_ERROR\",\"error\":\"SOME_ERROR\"}";
            var result = FunctionInvoker.WrapLegacyStringResult(errorEnvelope);

            Assert.AreEqual(errorEnvelope, result);
        }

        [Test]
        public void WrapLegacyStringResult_JsonWithoutSuccessField_WrapsInEnvelope()
        {
            const string json = "{\"count\":42,\"items\":[]}";
            var result = FunctionInvoker.WrapLegacyStringResult(json);

            StringAssert.Contains("\"success\":true", result);
            StringAssert.Contains(json, result);
        }

        [Test]
        public void WrapLegacyStringResult_EmptyString_WrapsInSuccessEnvelope()
        {
            var result = FunctionInvoker.WrapLegacyStringResult("");

            StringAssert.Contains("\"success\":true", result);
        }

        // ------------------------------------------------------------------
        //  6. ToolResultFormatter integration
        // ------------------------------------------------------------------

        [Test]
        public void ToolResultFormatter_ErrorWithData_ContainsCodeAndData()
        {
            var result = ToolResultFormatter.Error("TEST_CODE", new { detail = "value" });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"TEST_CODE\"", result);
            StringAssert.Contains("\"detail\":\"value\"", result);
        }

        [Test]
        public void ToolResultFormatter_ErrorMessage_ContainsCodeAndMessage()
        {
            var result = ToolResultFormatter.ErrorMessage("MY_CODE", "my message");

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"MY_CODE\"", result);
            StringAssert.Contains("my message", result);
        }

        [Test]
        public void ToolResultFormatter_IsError_ReturnsFalseForNullOrEmpty()
        {
            Assert.IsFalse(ToolResultFormatter.IsError(null));
            Assert.IsFalse(ToolResultFormatter.IsError(""));
        }

        [Test]
        public void ToolResultFormatter_IsError_ReturnsFalseForMalformedJson()
        {
            Assert.IsFalse(ToolResultFormatter.IsError("not json at all"));
            Assert.IsFalse(ToolResultFormatter.IsError("{broken json"));
        }

        // ------------------------------------------------------------------
        //  7. Manual tool registration edge cases
        // ------------------------------------------------------------------

        [Test]
        public void ManualTool_RegisterWithNullParams_ReturnsSuccess()
        {
            var toolName = "test_null_params_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                name = toolName,
                description = "Tool with null parameters def"
                // parameters intentionally null
            };

            ToolRegistry.Register(toolName, definition, parameters => "null-params-ok");

            try
            {
                var invoker = new FunctionInvoker();
                var result = invoker.Invoke(new FunctionCall
                {
                    FunctionName = toolName,
                    Parameters = new Dictionary<string, string>()
                });

                StringAssert.Contains("\"success\":true", result);
                StringAssert.Contains("null-params-ok", result);
            }
            finally
            {
                ToolRegistry.Unregister(toolName);
            }
        }

        [Test]
        public void ManualTool_UnregisterThenInvoke_ReturnsUnknownFunction()
        {
            var toolName = "test_unregister_" + Guid.NewGuid().ToString("N");
            var definition = new ToolDefinition
            {
                name = toolName,
                description = "Tool to unregister"
            };

            ToolRegistry.Register(toolName, definition, _ => "ok");
            ToolRegistry.Unregister(toolName);

            var invoker = new FunctionInvoker();
            var result = invoker.Invoke(new FunctionCall { FunctionName = toolName });

            StringAssert.Contains("\"success\":false", result);
            StringAssert.Contains("\"code\":\"UNKNOWN_FUNCTION\"", result);
        }

        [Test]
        public void ToolRegistry_RegisterNullName_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                ToolRegistry.Register(null, new ToolDefinition(), _ => "ok"));
        }

        [Test]
        public void ToolRegistry_RegisterNullDefinition_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                ToolRegistry.Register("some_tool", null, _ => "ok"));
        }

        [Test]
        public void ToolRegistry_RegisterNullHandler_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                ToolRegistry.Register("some_tool", new ToolDefinition(), null));
        }

        // A comma-decimal locale formatting 1.5 as "1,5" survives the round trip only because both
        // ends speak invariant: the parser reads that comma as a group separator, so 1.5 lands as 15.
        [Test]
        public void ConvertArgumentToString_CommaDecimalLocale_RoundTripsThroughInvoker()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                var formatted = MCP.Server.MCPExecutionBridge.ConvertArgumentToString(1.5d);

                Assert.AreEqual("1.5", formatted);
                Assert.AreEqual(1.5f,
                    float.Parse(formatted, System.Globalization.CultureInfo.InvariantCulture),
                    0.0001f);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }
    }
}
