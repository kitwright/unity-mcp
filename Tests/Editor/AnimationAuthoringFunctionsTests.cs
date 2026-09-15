// Copyright (C) KitWright. Licensed under MIT.

#if KITWRIGHT_ANIMATION
using System.Globalization;
using System.Linq;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KitWright.Editor.Tests
{
    public sealed class AnimationAuthoringFunctionsTests
    {
        private const string Folder = "Assets/__KwAnimAuthoringTests";
        private const string ControllerPath = Folder + "/Ctrl.controller";
        private const string ClipPath = Folder + "/Clip.anim";

        [SetUp]
        public void SetUp()
        {
            AnimationFunctions.CreateAnimatorController("Ctrl", Folder + "/");
            AnimationFunctions.CreateAnimationClip("Clip", Folder + "/");
        }

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh();
        }

        private static AnimatorController Controller() =>
            AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);

        private static AnimatorStateMachine StateMachine() => Controller().layers[0].stateMachine;

        [Test]
        public void AddAnimatorParameter_LandsWithItsDefaultValue()
        {
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "0.5");

            var parameter = Controller().parameters.Single(p => p.name == "Speed");
            Assert.AreEqual(AnimatorControllerParameterType.Float, parameter.type);
            Assert.AreEqual(0.5f, parameter.defaultFloat, 0.0001f);
        }

        // The editor runs under the OS culture, and float.Parse(string) allows group separators, so on a
        // German machine "0.5" used to land as 5 with no exception to catch. The test above only passes
        // because the machine running it happens to use a dot.
        [Test]
        public void AddAnimatorParameter_ReadsItsDefaultValueTheSameUnderAnyCulture()
        {
            var previous = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            try
            {
                AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "0.5");

                Assert.AreEqual(0.5f, Controller().parameters.Single(p => p.name == "Speed").defaultFloat, 0.0001f);
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void AddAnimatorParameter_RejectsADefaultValueThatIsNotANumber()
        {
            StringAssert.Contains("INVALID_DEFAULT_VALUE",
                AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float", "fast"));
        }

        [Test]
        public void AddAnimatorParameter_RejectsADuplicateInsteadOfAddingASecond()
        {
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float");

            StringAssert.Contains("PARAMETER_EXISTS",
                AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float"));
            Assert.AreEqual(1, Controller().parameters.Count(p => p.name == "Speed"));
        }

        // create_animator_controller leaves an empty state machine, so "the asset exists" says nothing
        // about whether a state, its clip and the entry point actually got set.
        [Test]
        public void AddAnimatorState_BindsTheClipAndCanClaimTheDefaultState()
        {
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run", ClipPath, 0, make_default: true);

            var stateMachine = StateMachine();
            var run = stateMachine.states.Single(s => s.state.name == "Run").state;
            Assert.AreEqual(ClipPath, AssetDatabase.GetAssetPath(run.motion));
            Assert.AreEqual("Run", stateMachine.defaultState.name);
        }

        [Test]
        public void AddAnimatorState_MissingClipIsAnErrorNotAStateWithNoMotion()
        {
            StringAssert.Contains("ANIMATION_CLIP_NOT_FOUND",
                AnimationFunctions.AddAnimatorState(ControllerPath, "Run", Folder + "/NoSuch.anim"));
        }

        [Test]
        public void AddAnimatorTransition_CarriesItsConditionAndTiming()
        {
            AnimationFunctions.AddAnimatorParameter(ControllerPath, "Speed", "float");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Idle");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run");

            AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run",
                "[{\"parameter\":\"Speed\",\"mode\":\"Greater\",\"threshold\":0.1}]", 0, false, 0.25f);

            var idle = StateMachine().states.Single(s => s.state.name == "Idle").state;
            var transition = idle.transitions.Single();
            Assert.AreEqual("Run", transition.destinationState.name);
            Assert.AreEqual(0.25f, transition.duration, 0.0001f);

            var condition = transition.conditions.Single();
            Assert.AreEqual("Speed", condition.parameter);
            Assert.AreEqual(AnimatorConditionMode.Greater, condition.mode);
            Assert.AreEqual(0.1f, condition.threshold, 0.0001f);
        }

        // Unity drops a condition naming an unknown parameter without a word, leaving a transition that
        // never fires. Refusing the call is the whole reason the guard exists.
        [Test]
        public void AddAnimatorTransition_UnknownParameterIsRejectedNotSilentlyDropped()
        {
            AnimationFunctions.AddAnimatorState(ControllerPath, "Idle");
            AnimationFunctions.AddAnimatorState(ControllerPath, "Run");

            var message = AnimationFunctions.AddAnimatorTransition(ControllerPath, "Idle", "Run",
                "[{\"parameter\":\"Ghost\",\"mode\":\"Greater\",\"threshold\":0.1}]");

            StringAssert.Contains("PARAMETER_NOT_FOUND", message);
        }

        [Test]
        public void AddAnimatorTransition_AnyStateTransitionAttachesToTheMachineNotAState()
        {
            AnimationFunctions.AddAnimatorState(ControllerPath, "Hit");

            AnimationFunctions.AddAnimatorTransition(ControllerPath, "any", "Hit");

            Assert.AreEqual("Hit", StateMachine().anyStateTransitions.Single().destinationState.name);
        }

        // create_animation_clip returns a clip of length 0; a curve is what makes it an animation.
        [Test]
        public void SetClipCurve_GivesTheClipRealLengthAndABinding()
        {
            AnimationFunctions.SetClipCurve(ClipPath, "m_LocalPosition.x",
                "[{\"time\":0,\"value\":0},{\"time\":0.5,\"value\":3},{\"time\":1,\"value\":0}]");

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            Assert.AreEqual(1f, clip.length, 0.0001f);

            var binding = AnimationUtility.GetCurveBindings(clip).Single();
            Assert.AreEqual("m_LocalPosition.x", binding.propertyName);
            Assert.AreEqual(3, AnimationUtility.GetEditorCurve(clip, binding).length);
        }

        [Test]
        public void SetClipCurve_MalformedKeysAreRejectedInsteadOfWritingAnEmptyCurve()
        {
            StringAssert.Contains("INVALID_KEYFRAME",
                AnimationFunctions.SetClipCurve(ClipPath, "m_LocalPosition.x", "[{\"time\":0}]"));

            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipPath);
            Assert.IsEmpty(AnimationUtility.GetCurveBindings(clip));
        }
    }
}
#endif
