// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_ANIMATION
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The three tools that drive an Animator rather than author one: binding a controller, forcing a
    /// state, and writing a parameter. The controller itself is built with the authoring tools, which
    /// AnimationAuthoringFunctionsTests already covers.
    /// </summary>
    public sealed class AnimatorRuntimeToolsTests
    {
        private const string FolderName = "__KitWrightAnimatorToolTests";
        private const string Folder = "Assets/" + FolderName;
        private const string ControllerPath = Folder + "/KwCtrl.controller";
        private const string Subject = "KwAnimatorSubject";

        private GameObject subject;

        [SetUp]
        public void CreateControllerAndSubject()
        {
            // An Animator that is not playing says so on the console for some of these writes; the
            // assertions read the animator and the answers, not the log.
            LogAssert.ignoreFailingMessages = true;

            AnimationFunctions.CreateAnimatorController("KwCtrl", Folder + "/");
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "0");
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Grounded", "bool", "false");
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Jump", "trigger");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Idle", make_default: true);
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run");

            subject = new GameObject(Subject);
        }

        [TearDown]
        public void DestroySubjectAndController()
        {
            LogAssert.ignoreFailingMessages = false;

            if (subject != null)
                Object.DestroyImmediate(subject);
            subject = null;

            AssetDatabase.DeleteAsset(Folder);
        }

        private Animator Assigned()
        {
            Ok("assign_animator", "game_object_name", Subject, "controller_path", ControllerPath);
            return subject.GetComponent<Animator>();
        }

        [Test]
        public void AssignAnimatorAddsTheComponentItNeedsAndBindsTheController()
        {
            Assert.IsNull(subject.GetComponent<Animator>(), "The subject starts without an Animator.");

            var animator = Assigned();

            Assert.IsNotNull(animator, "assign_animator should have added the Animator itself.");
            Assert.AreEqual(ControllerPath, AssetDatabase.GetAssetPath(animator.runtimeAnimatorController));

            Assert.AreEqual("ANIMATOR_CONTROLLER_NOT_FOUND",
                Code("assign_animator", "game_object_name", Subject, "controller_path", Folder + "/NoSuch.controller"));
            Assert.AreEqual("GAME_OBJECT_NOT_FOUND",
                Code("assign_animator", "game_object_name", "KwNothingCalledThis", "controller_path", ControllerPath));
        }

        [Test]
        public void PlayAnimatorStateFindsTheLayerHoldingTheStateAndRefusesTheRest()
        {
            Assigned();

            var played = Ok("play_animator_state", "target", Subject, "state", "Run");
            Assert.AreEqual(0, (int)played["data"]["layer"], "The only layer holding 'Run' is layer 0.");
            Assert.AreEqual(-1, (int)played["data"]["requestedLayer"]);

            Ok("play_animator_state", "target", Subject, "state", "Idle", "layer", "0", "normalized_time", "0.5");

            Assert.AreEqual("STATE_NOT_FOUND", Code("play_animator_state", "target", Subject, "state", "Ghost"));
            Assert.AreEqual("INVALID_LAYER", Code("play_animator_state", "target", Subject, "state", "Idle", "layer", "9"));
            Assert.AreEqual("INVALID_LAYER", Code("play_animator_state", "target", Subject, "state", "Idle", "layer", "-2"));

            var bare = new GameObject("KwAnimatorless");
            try
            {
                Assert.IsFalse((bool)Call("play_animator_state", "target", bare.name, "state", "Idle")["success"],
                    "An object with no Animator cannot play a state.");
            }
            finally
            {
                Object.DestroyImmediate(bare);
            }
        }

        [Test]
        public void SetAnimatorParameterWritesEachTypeAndRefusesAValueOfTheWrongOne()
        {
            var animator = Assigned();
            Ok("play_animator_state", "target", Subject, "state", "Idle");

            Ok("set_animator_parameter", "target", Subject, "parameter", "Speed", "value", "1.25");
            Assert.AreEqual(1.25f, animator.GetFloat("Speed"), 0.001f);

            Ok("set_animator_parameter", "target", Subject, "parameter", "Grounded", "value", "true");
            Assert.IsTrue(animator.GetBool("Grounded"));

            Ok("set_animator_parameter", "target", Subject, "parameter", "Jump", "value", "set");
            Ok("set_animator_parameter", "target", Subject, "parameter", "Jump", "value", "reset");

            Assert.AreEqual("PARAMETER_NOT_FOUND",
                Code("set_animator_parameter", "target", Subject, "parameter", "Ghost", "value", "1"));
            Assert.AreEqual("INVALID_FLOAT",
                Code("set_animator_parameter", "target", Subject, "parameter", "Speed", "value", "fast"));
            Assert.AreEqual(1.25f, animator.GetFloat("Speed"), 0.001f, "A refused write must leave the old value.");
            Assert.AreEqual("INVALID_BOOL",
                Code("set_animator_parameter", "target", Subject, "parameter", "Grounded", "value", "yes"));
            Assert.AreEqual("PARAMETER_REQUIRED",
                Code("set_animator_parameter", "target", Subject, "parameter", "", "value", "1"));
        }
    }
}
#endif
