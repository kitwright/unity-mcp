// Copyright (C) KitWright. Licensed under MIT.

#if KITWRIGHT_INPUTSYSTEM
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class InputSimulationFunctionsTests
    {
        [Test]
        public void FindGamepadButton_AcceptsTheSnakeCaseNamesTheToolAdvertises()
        {
            Assert.AreEqual(GamepadButton.DpadUp, InputSimulationFunctions.FindGamepadButton("dpad_up"));
            Assert.AreEqual(GamepadButton.LeftShoulder, InputSimulationFunctions.FindGamepadButton("left_shoulder"));
            Assert.AreEqual(GamepadButton.Start, InputSimulationFunctions.FindGamepadButton("START"));
        }

        [Test]
        public void FindGamepadButton_ResolvesTheVendorAliasesToTheSameFace()
        {
            Assert.AreEqual(InputSimulationFunctions.FindGamepadButton("south"), InputSimulationFunctions.FindGamepadButton("a"));
            Assert.AreEqual(InputSimulationFunctions.FindGamepadButton("south"), InputSimulationFunctions.FindGamepadButton("cross"));
            Assert.AreEqual(InputSimulationFunctions.FindGamepadButton("east"), InputSimulationFunctions.FindGamepadButton("b"));
        }

        [Test]
        public void FindGamepadButton_ReturnsNullRatherThanAGuess()
        {
            Assert.IsNull(InputSimulationFunctions.FindGamepadButton("turbo"));
            Assert.IsNull(InputSimulationFunctions.FindGamepadButton(""));
            Assert.IsNull(InputSimulationFunctions.FindGamepadButton(null));
        }

        // An enum member's ordinal parses as that enum, so "3" would silently mean the fourth button.
        [Test]
        public void FindGamepadButton_RejectsANumber()
        {
            Assert.IsNull(InputSimulationFunctions.FindGamepadButton("3"));
        }

        // Every member Gamepad's own indexer maps has to resolve, the two triggers included: they sit
        // outside GamepadState's 32-bit button bitmask, which is one reason this writes through the
        // controls rather than through that bitmask.
        [Test]
        public void FindGamepadButton_KeepsEveryButtonTheDeviceIndexerMaps()
        {
            foreach (GamepadButton value in Enum.GetValues(typeof(GamepadButton)))
            {
                Assert.IsNotNull(InputSimulationFunctions.FindGamepadButton(value.ToString()),
                    $"'{value}' is a real GamepadButton and must stay resolvable.");
            }

            Assert.AreEqual(GamepadButton.LeftTrigger, InputSimulationFunctions.FindGamepadButton("left_trigger"));
        }

        [Test]
        public void TouchToolsRefuseOutsidePlayMode()
        {
            StringAssert.Contains("PLAY_MODE_REQUIRED", InputSimulationFunctions.SimulateTouch(10, 10));
            // The drag tools refuse before their first await, so the task is already finished here.
            StringAssert.Contains("PLAY_MODE_REQUIRED", InputSimulationFunctions.SimulateTouchDrag(0, 0, 10, 10).Result);
            StringAssert.Contains("PLAY_MODE_REQUIRED", InputSimulationFunctions.SimulateMouseDrag(0, 0, 10, 10).Result);
            StringAssert.Contains("PLAY_MODE_REQUIRED", InputSimulationFunctions.SimulateGamepad("south"));
        }

        /// <summary>
        /// Samples the devices the way a game does: once per player frame, from inside the player
        /// loop. Reading the same controls from the test body instead reads whatever buffer the last
        /// editor update left behind, which reports an injected press as gone on the next frame even
        /// while the running game still sees it held. Anything asserting about input across frames
        /// has to be measured from here.
        /// </summary>
        private sealed class InputSampler : MonoBehaviour
        {
            public readonly List<bool> MouseDown = new List<bool>();
            public readonly List<float> MouseX = new List<float>();
            public readonly List<bool> TouchDown = new List<bool>();
            public readonly List<float> TouchX = new List<float>();

            private void Update()
            {
                var mouse = UnityEngine.InputSystem.Mouse.current;
                if (mouse != null)
                {
                    MouseDown.Add(mouse.leftButton.isPressed);
                    MouseX.Add(mouse.position.ReadValue().x);
                }

                var touch = Touchscreen.current;
                if (touch != null)
                {
                    TouchDown.Add(touch.primaryTouch.press.isPressed);
                    TouchX.Add(touch.primaryTouch.position.ReadValue().x);
                }
            }
        }

        private static IEnumerable<float> WhileDown(IReadOnlyList<bool> down, IReadOnlyList<float> x)
        {
            for (var i = 0; i < down.Count && i < x.Count; i++)
                if (down[i])
                    yield return x[i];
        }

        private InputSettings.EditorInputBehaviorInPlayMode? previousInputBehavior;

        [TearDown]
        public void RestoreInputBehavior()
        {
            if (previousInputBehavior != null)
                InputSystem.settings.editorInputBehaviorInPlayMode = previousInputBehavior.Value;

            previousInputBehavior = null;
        }

        /// <summary>
        /// Probes the raw house pattern with no tool code in the way, so a gamepad assertion can tell
        /// "the tool is wrong" from "the Input System will not route this here". In the editor it can
        /// drop input for devices that respect Game View focus, and a batchmode run has no Game View at
        /// all, so the focus rule is lifted first and restored in TearDown.
        /// </summary>
        /// <summary>
        /// Under the default PointersAndKeyboardsRespectGameViewFocus, Unity resets pointer devices
        /// every frame when no Game View has focus - which is the normal case for a run driven from
        /// outside the editor. An injected press survives the tick it was written in and nothing
        /// after it, so anything that spans frames has to lift the rule first.
        /// </summary>
        private void LiftGameViewFocusRule()
        {
            previousInputBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode =
                InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        }

        private void RequireWorkingGamepadInjection()
        {
            LiftGameViewFocusRule();

            var gamepad = InputSystem.GetDevice<Gamepad>() ?? InputSystem.AddDevice<Gamepad>();
            WriteSouth(gamepad, 1f);
            var landed = gamepad.buttonSouth.isPressed;
            WriteSouth(gamepad, 0f);

            if (!landed)
            {
                Assert.Ignore("Raw Input System state events do not reach a synthetic Gamepad here: batchmode " +
                              $"has no Game View to route device input to (wrote to id={gamepad.deviceId}, " +
                              $"added={gamepad.added}, enabled={gamepad.enabled}). simulate_touch and " +
                              "simulate_key_press do land, and their own tests cover the injection path.");
            }
        }

        /// <summary>
        /// The pointer counterpart of <see cref="RequireWorkingGamepadInjection"/>, and the same
        /// question: is the device unreachable here, or is the tool wrong? Under the default
        /// PointersAndKeyboardsRespectGameViewFocus, Unity resets pointer devices every frame when no
        /// Game View has focus, and a batchmode run has no Game View at all - so an injected pointer
        /// is gone before the player loop samples it and nothing spanning frames can be asserted.
        /// Lifting the rule instead is not an option here: it routes the real mouse of whoever runs
        /// the editor into the same device these tests read.
        /// </summary>
        private static void RequirePointersRoutedToAGameView()
        {
            if (Application.isBatchMode)
            {
                Assert.Ignore("A batchmode run has no Game View to route pointer input to, so an injected " +
                              "mouse or touch is reset before the player loop reads it. The single-tick " +
                              "assertions above still cover the injection path itself.");
            }
        }

        private static void WriteSouth(Gamepad gamepad, float value)
        {
            using (StateEvent.From(gamepad, out var eventPtr))
            {
                gamepad.buttonSouth.WriteValueIntoEvent(value, eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }

            InputSystem.Update();
        }

        private static string Diagnose(string answer)
        {
            var gamepad = Gamepad.current;
            return $"tool said [{answer}]; devices=[{string.Join(", ", InputSystem.devices.Select(d => d.name))}]; " +
                   $"south={gamepad?.buttonSouth.ReadValue()}; leftTrigger={gamepad?.leftTrigger.ReadValue()}; " +
                   $"leftStick={gamepad?.leftStick.ReadValue()}; updateMode={InputSystem.settings.updateMode}";
        }

        // Everything below enters Play Mode, because the injection itself is the part that cannot be
        // checked any other way: outside Play Mode every one of these tools returns PLAY_MODE_REQUIRED
        // before touching a device, so an Edit Mode assertion proves only that the guard works.
        [UnityTest]
        public IEnumerator SimulateTouch_DrivesARealTouchscreenDeviceUpAndDown()
        {
            yield return new EnterPlayMode();

            InputSimulationFunctions.SimulateTouch(120, 140, "press");
            Assert.IsNotNull(Touchscreen.current, "the tool is expected to add a Touchscreen when none exists");
            Assert.IsTrue(Touchscreen.current.primaryTouch.press.isPressed, "a press should leave the touch down");
            Assert.AreEqual(120f, Touchscreen.current.primaryTouch.position.ReadValue().x, 1f);

            InputSimulationFunctions.SimulateTouch(120, 140, "release");
            Assert.IsFalse(Touchscreen.current.primaryTouch.press.isPressed, "a release should lift it");

            RequirePointersRoutedToAGameView();

            var probe = new GameObject("KwTouchSwipeProbe");
            var sampler = probe.AddComponent<InputSampler>();

            var swipe = InputSimulationFunctions.SimulateTouchDrag(20, 20, 200, 240);
            while (!swipe.IsCompleted)
                yield return null;
            yield return null;

            var down = WhileDown(sampler.TouchDown, sampler.TouchX).ToList();
            var diag = $"downSamples=[{string.Join(",", down)}] tool=[{swipe.Result}]";
            UnityEngine.Object.DestroyImmediate(probe);

            StringAssert.Contains("swiped", swipe.Result);

            // Sampled from the player loop, because that is the only place the answer is real: read
            // from the test body instead, the device reports the finger up on the very next frame
            // while the running game still sees it held.
            //
            // Every phase used to be queued inside one editor tick, so a game got the whole swipe in
            // a single frame - one pointer position, never the press at the source. Spreading the
            // steps over frames is what these samples prove.
            Assert.Greater(down.Distinct().Count(), 2,
                "the finger has to be seen in motion across frames, not as one jump to the end. " + diag);
            Assert.Less(down.First(), 60f, "the first frame of the swipe is near the source. " + diag);
            Assert.Greater(down.Max(), 150f, "and it reaches the far end while still down. " + diag);

            yield return new ExitPlayMode();
        }

        /// <summary>
        /// Two defects in one drag. The release event wrote only the position, and StateEvent.From
        /// seeds an event with the device's current state, so the button stayed down: every later
        /// click and drag in the session ran with it held. And every step was queued inside one
        /// editor tick, so 'duration' only sized the path - uGUI, which reads the pointer in
        /// EventSystem.Update(), saw one position for the whole drag and never the press at the
        /// source.
        /// </summary>
        [UnityTest]
        public IEnumerator SimulateMouseDrag_LetsTheButtonGoAndSpendsTheDurationItWasGiven()
        {
            yield return new EnterPlayMode();

            RequirePointersRoutedToAGameView();

            var probe = new GameObject("KwMouseDragProbe");
            var sampler = probe.AddComponent<InputSampler>();

            var startedAt = EditorApplication.timeSinceStartup;
            var drag = InputSimulationFunctions.SimulateMouseDrag(10, 20, 200, 240, 0.4f);
            while (!drag.IsCompleted)
                yield return null;
            // One more frame, so the release itself reaches the player loop before it is read.
            yield return null;

            var elapsed = EditorApplication.timeSinceStartup - startedAt;
            var down = WhileDown(sampler.MouseDown, sampler.MouseX).ToList();
            var diag = $"downSamples=[{string.Join(",", down)}] elapsed={elapsed:F3} " +
                       $"lastDown={sampler.MouseDown.LastOrDefault()} lastX={sampler.MouseX.LastOrDefault()} " +
                       $"tool=[{drag.Result}]";
            UnityEngine.Object.DestroyImmediate(probe);

            // Sampled from the player loop: read from the test body instead, the device reports the
            // button up on the very next frame while the running game still sees it held.
            Assert.IsFalse(sampler.MouseDown.Last(), "a finished drag must not leave the button held. " + diag);
            Assert.AreEqual(200f, sampler.MouseX.Last(), 1f, "the pointer ends where it was sent. " + diag);

            // uGUI reads the pointer in EventSystem.Update(), which does not run between two
            // InputSystem.Update() calls inside one editor tick. Queued that way the press, the whole
            // path and the release all arrive at the end point, so a drag handler sees one position
            // and never the press at the source.
            Assert.Greater(down.Distinct().Count(), 2,
                "the pointer has to move across frames while the button is down, not jump once. " + diag);
            Assert.Less(down.First(), 60f, "the drag is seen starting near the source. " + diag);

            // Generous floor: the point is that the call spans frames at all, not that it hits 0.4s.
            Assert.Greater(elapsed, 0.2d, "and it spends the duration it was given. " + diag);

            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SimulateGamepad_KeepsAHeldButtonWhileTheStickMoves()
        {
            yield return new EnterPlayMode();

            RequireWorkingGamepadInjection();

            var pressAnswer = InputSimulationFunctions.SimulateGamepad("south", "press");
            Assert.IsNotNull(Gamepad.current, $"the tool is expected to add a Gamepad when none exists; it said [{pressAnswer}]");
            Assert.IsTrue(Gamepad.current.buttonSouth.isPressed, Diagnose(pressAnswer));

            // The regression this merge exists for: a gamepad state event carries the whole device, so
            // a partial update that did not read the device first would release the held button here.
            InputSimulationFunctions.SimulateGamepad(button: null, left_stick_x: 1f);
            Assert.IsTrue(Gamepad.current.buttonSouth.isPressed, "moving a stick must not release a held button");
            Assert.Greater(Gamepad.current.leftStick.ReadValue().x, 0.5f);

            InputSimulationFunctions.SimulateGamepad("south", "release");
            Assert.IsFalse(Gamepad.current.buttonSouth.isPressed);
            Assert.Greater(Gamepad.current.leftStick.ReadValue().x, 0.5f, "releasing a button must not centre the stick");

            yield return new ExitPlayMode();
        }

        [UnityTest]
        public IEnumerator SimulateGamepad_MovesTheAnalogTriggerAndNotTheDpad()
        {
            yield return new EnterPlayMode();

            RequireWorkingGamepadInjection();

            var answer = InputSimulationFunctions.SimulateGamepad(button: null, left_trigger: 1f, right_trigger: 0.5f);

            Assert.Greater(Gamepad.current.leftTrigger.ReadValue(), 0.9f, Diagnose(answer));
            Assert.AreEqual(0.5f, Gamepad.current.rightTrigger.ReadValue(), 0.05f, Diagnose(answer));

            // GamepadButton.LeftTrigger is 32, outside GamepadState's 32-bit mask, so writing it through
            // that bitmask would have folded it onto bit 0 - D-pad Up.
            Assert.IsFalse(Gamepad.current.dpad.up.isPressed, "the trigger must not fold onto the D-pad");
            Assert.IsFalse(Gamepad.current.dpad.down.isPressed);

            // Named as a button it is a full press, and still not the D-pad.
            InputSimulationFunctions.SimulateGamepad("left_trigger", "press");
            Assert.Greater(Gamepad.current.leftTrigger.ReadValue(), 0.9f);
            Assert.IsFalse(Gamepad.current.dpad.up.isPressed, "the trigger must not fold onto the D-pad");

            yield return new ExitPlayMode();
        }
    }
}
#endif
