// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Reflection;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.Services.UnityLogs;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;
using Object = UnityEngine.Object;

namespace KitWright.Editor.Tests
{
    public sealed class PlayModeToolFixesTests
    {
        private static string Unique(string stem) => stem + "_" + Guid.NewGuid().ToString("N");

        private static string CodeOf(string reply) => (string)JObject.Parse(reply)["code"];

        [Test]
        public void GetTestJobIsReplaySafeSoARedeliveredPollRunsAgainInsteadOfAnsweringTheStub()
        {
            Assert.IsTrue(ToolRegistry.IsReplaySafe("get_test_job"));

            var response = MCPServerService.TryCreateBrokerRedeliveryResponse(new MCPRequest
            {
                Id = "1",
                Method = "tools/call",
                IsBrokerRedelivery = true,
                Params = new Dictionary<string, object> { ["name"] = "get_test_job" }
            });
            Assert.IsNull(response, "a poll that crossed the reload has to be run, not answered as interrupted");
        }

        [TestCase(true, true, "Already in play mode")]
        [TestCase(false, true, "Already entering play mode")]
        [TestCase(false, false, null)]
        public void EnterPlayModeReply_ReadsTheStateOfTheTransition(bool isPlaying, bool willChange, string expected)
        {
            Assert.AreEqual(expected, SceneFunctions.EnterPlayModeReply(isPlaying, willChange, scriptCompilationFailed: false));
        }

        [Test]
        public void EnterPlayModeReply_RefusesWhileAnExitIsStillInFlight()
        {
            // isPlaying lags one tick and isPlayingOrWillChangePlaymode is already false once an exit is
            // requested: that pair is what batch_execute [exit_play_mode, enter_play_mode] sees.
            var reply = SceneFunctions.EnterPlayModeReply(isPlaying: true, isPlayingOrWillChange: false, scriptCompilationFailed: false);
            Assert.AreEqual("PLAY_MODE_TRANSITION_PENDING", CodeOf(reply));
        }

        [Test]
        public void EnterPlayModeReply_RefusesWithTheCompileErrorsInsteadOfClaimingToEnter()
        {
            var reply = SceneFunctions.EnterPlayModeReply(isPlaying: false, isPlayingOrWillChange: false, scriptCompilationFailed: true);

            Assert.AreEqual("COMPILATION_FAILED", CodeOf(reply));
            Assert.IsNotEmpty((string)JObject.Parse(reply)["data"]["issues"]);
        }

        [TestCase(true, true, null)]
        [TestCase(false, false, "Not in play mode")]
        [TestCase(true, false, "Already exiting play mode")]
        public void ExitPlayModeReply_ReadsTheStateOfTheTransition(bool isPlaying, bool willChange, string expected)
        {
            Assert.AreEqual(expected, SceneFunctions.ExitPlayModeReply(isPlaying, willChange));
        }

        [Test]
        public void ExitPlayModeReply_RefusesWhileAnEntryIsStillInFlight()
        {
            var reply = SceneFunctions.ExitPlayModeReply(isPlaying: false, isPlayingOrWillChange: true);
            Assert.AreEqual("PLAY_MODE_TRANSITION_PENDING", CodeOf(reply));
        }

        [Test]
        public void SetTimeScale_OutsidePlayModeIsRefusedInsteadOfWritingTheProjectSetting()
        {
            Assume.That(EditorApplication.isPlaying, Is.False);
            var before = Time.timeScale;
            try
            {
                Assert.AreEqual("PLAY_MODE_REQUIRED", Code("set_time_scale", "scale", "0.25"));
                Assert.AreEqual(before, Time.timeScale, 0.0001f);
            }
            finally
            {
                if (!Mathf.Approximately(Time.timeScale, before))
                    Time.timeScale = before;
            }
        }

        [TestCase("info")]
        [TestCase("warn")]
        [TestCase("everything")]
        public void GetConsoleLogs_RefusesAnUnknownLogTypeAndListsTheValidOnes(string logType)
        {
            var answer = Call("get_console_logs", "log_type", logType);

            Assert.AreEqual("INVALID_PARAM", (string)answer["code"]);
            Assert.That(answer.ToString(), Does.Contain("all, log, warning or error"));
        }

        [TestCase(null, "all")]
        [TestCase("", "all")]
        [TestCase(" Error ", "error")]
        [TestCase("WARNING", "warning")]
        [TestCase("log", "log")]
        public void TryNormalizeLogType_AcceptsTheFourValuesCaseInsensitively(string given, string expected)
        {
            Assert.IsTrue(VisualFeedbackFunctions.TryNormalizeLogType(given, out var normalized));
            Assert.AreEqual(expected, normalized);
        }

        [Test]
        public void FocusOnObject_LeavesTheSelectionAsItWasAndSaysWhenThereIsNoSceneView()
        {
            var first = new GameObject(Unique("KwFocusSelA"));
            var second = new GameObject(Unique("KwFocusSelB"));
            var target = new GameObject(Unique("KwFocusTarget"));
            var before = Selection.objects;

            try
            {
                Selection.objects = new Object[] { first, second };

                var answer = Call("focus_on_object", "name", target.name);

                var hasSceneView = SceneView.lastActiveSceneView != null ||
                                   (SceneView.sceneViews != null && SceneView.sceneViews.Count > 0);
                if (hasSceneView)
                    Assert.IsTrue((bool)answer["success"], answer.ToString());
                else
                    Assert.AreEqual("NO_SCENE_VIEW", (string)answer["code"]);

                CollectionAssert.AreEquivalent(new Object[] { first, second }, Selection.objects,
                    "focusing must not replace what the user had selected");
            }
            finally
            {
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(target);
                Selection.objects = before;
            }
        }

        [Test]
        public void AppliedLightingMessage_InPlayModeSaysTheChangeIsDiscardedAndDropsTheSaveHint()
        {
            var playing = LightingFunctions.AppliedMessage("fog=True", playing: true);
            StringAssert.Contains("discarded when it ends", playing);
            StringAssert.DoesNotContain("Save the scene to persist", playing);

            StringAssert.Contains("Save the scene to persist", LightingFunctions.AppliedMessage("fog=True", playing: false));
        }

        [Test]
        public void GetCounters_RightAfterTheRecordersWereCreatedWarnsThatTheRenderCountersCanReadZero()
        {
            var profilerWasEnabled = Profiler.enabled;
            ProfilerFunctions.ProfilerStop();
            try
            {
                ProfilerFunctions.ProfilerStart();
                StringAssert.Contains("created less than a second ago", ProfilerFunctions.GetCounters());
            }
            finally
            {
                ProfilerFunctions.ProfilerStop();
                Profiler.enabled = profilerWasEnabled;
            }
        }

        [Test]
        public void IsWarmingUp_ClearsOnceTheRecordersHaveHadTimeToSeeAFrame()
        {
            Assert.IsTrue(ProfilerFunctions.IsWarmingUp(startedAt: 10.0, now: 10.2));
            Assert.IsFalse(ProfilerFunctions.IsWarmingUp(startedAt: 10.0, now: 12.0));
        }

        [Test]
        public void ContextBlock_ShowsAnErrorLoggedAfterTheBlockWasCached()
        {
            var logs = new UnityLogsRepository();
            var builder = new EditorContextBuilder(null, logs);
            var message = Unique("kw-context-error");

            try
            {
                builder.GetContextBlock();

                LogAssert.Expect(LogType.Error, message);
                Debug.LogError(message);

                StringAssert.Contains(message, builder.GetContextBlock());
            }
            finally
            {
                builder.Dispose();
                logs.Dispose();
            }
        }

        [Test]
        public void CompileErrorContext_IsRebuiltWhenACompileStartsOrFinishes()
        {
            var builder = new EditorContextBuilder(CompilationService.Instance, null);

            try
            {
                CompilationService.IsCompilingOverride = true;
                StringAssert.Contains("Currently compiling", builder.GetCompileErrorContext());

                CompilationService.IsCompilingOverride = false;
                typeof(EditorContextBuilder)
                    .GetMethod("OnCompilationStateChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(builder, new object[] { null });

                StringAssert.DoesNotContain("Currently compiling", builder.GetCompileErrorContext());
            }
            finally
            {
                CompilationService.IsCompilingOverride = null;
                builder.Dispose();
            }
        }

        [Test]
        public void CaptureWithUI_PutsAnOverlayCanvasBackExactlyAsItFoundIt()
        {
            Assume.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(UnityEngine.Rendering.GraphicsDeviceType.Null));

            var cameraObject = new GameObject(Unique("KwCaptureCamera"));
            var otherCameraObject = new GameObject(Unique("KwCaptureOtherCamera"));
            var canvasObject = new GameObject(Unique("KwCaptureCanvas"));
            LogAssert.ignoreFailingMessages = true;

            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                var other = otherCameraObject.AddComponent<Camera>();
                var canvas = canvasObject.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.worldCamera = other;
                canvas.planeDistance = 7f;
                Assume.That(canvas.worldCamera == other && Mathf.Approximately(canvas.planeDistance, 7f),
                    "Unity does not keep these two on an overlay canvas, so there is nothing to restore");

                ScreenshotFunctions.CaptureWithUIPngBytes(camera, 64, 64);

                Assert.AreEqual(RenderMode.ScreenSpaceOverlay, canvas.renderMode);
                Assert.IsTrue(canvas.worldCamera == other, "the original worldCamera was nulled");
                Assert.AreEqual(7f, canvas.planeDistance, 0.0001f, "planeDistance was left at the capture value");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(canvasObject);
                Object.DestroyImmediate(otherCameraObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

#if KITWRIGHT_ANIMATION
        private const string AnimatorFolder = "Assets/__KitWrightPlayModeFixTests";
        private const string AnimatorControllerPath = AnimatorFolder + "/KwPlayModeCtrl.controller";

        [Test]
        public void PlayAnimatorState_InEditModeRegistersUndoAndMarksTheSceneModified()
        {
            Assume.That(EditorApplication.isPlaying, Is.False);

            var subject = new GameObject(Unique("KwAnimatorSubject"));
            LogAssert.ignoreFailingMessages = true;

            try
            {
                AnimationFunctions.CreateAnimatorController("KwPlayModeCtrl", AnimatorFolder + "/");
                AnimationFunctions.AddAnimatorState(AnimatorControllerPath, "Idle", make_default: true);
                Ok("assign_animator", "game_object_name", subject.name, "controller_path", AnimatorControllerPath);

                Undo.IncrementCurrentGroup();
                var answer = Ok("play_animator_state", "target", subject.name, "state", "Idle");

                Assert.AreEqual("Play Animator State", Undo.GetCurrentGroupName());
                Assert.IsTrue(subject.scene.isDirty);
                StringAssert.Contains("Edit Mode", (string)answer["hint"]);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
                Object.DestroyImmediate(subject);
                AssetDatabase.DeleteAsset(AnimatorFolder);
            }
        }
#endif
    }
}
