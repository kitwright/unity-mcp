// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_INPUTSYSTEM
using System;
using System.ComponentModel;
using System.Threading.Tasks;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("InputSimulation")]
    internal static class InputSimulationFunctions
    {
        [Description("Simulate keyboard input in Play Mode. Queues key events that the Unity Input System will process. Supports key names like W, A, S, D, Space, LeftShift, E, Q, and number keys.")]
        [ReadOnlyTool]
        public static string SimulateKeyPress(
            [ToolParam("Key name, for example W, Space, LeftShift, E, or 1")] string key,
            [ToolParam("Duration in seconds to hold the key. Use 0 for a tap.", Required = false)] float duration = 0f,
            [ToolParam("Action type: press, release, or tap", Required = false)] string action = "tap")
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateKeyPress only works in Play Mode.");

            try
            {
                var keyboard = EnsureKeyboard();
                if (keyboard == null)
                    return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No keyboard device found in Input System");

                var keyControl = FindKey(keyboard, key);
                if (keyControl == null)
                    return ToolResultFormatter.ErrorMessage("KEY_NOT_RECOGNIZED", $"Key '{key}' not recognized. Examples: W, A, S, D, Space, LeftShift, E, Escape, 1, 2");

                switch ((action ?? "tap").Trim().ToLowerInvariant())
                {
                    case "press":
                        QueueKeyState(keyboard, keyControl, true);
                        return $"Key '{key}' pressed (held down)";
                    case "release":
                        QueueKeyState(keyboard, keyControl, false);
                        return $"Key '{key}' released";
                    default:
                        return TapKey(keyboard, keyControl, key, duration);
                }
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Simulate multiple keys held simultaneously in Play Mode, for example W plus LeftShift.")]
        [ReadOnlyTool]
        public static string SimulateKeyCombo(
            [ToolParam("Comma-separated key names, for example 'W,LeftShift' or 'A,Space'")] string keys,
            [ToolParam("Duration in seconds to hold the combo", Required = false)] float duration = 0.5f)
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateKeyCombo only works in Play Mode.");

            try
            {
                var keyboard = EnsureKeyboard();
                if (keyboard == null)
                    return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No keyboard device found");

                duration = Mathf.Clamp(duration, 0.05f, 5f);
                var keyNames = (keys ?? string.Empty).Split(',');

                for (int i = 0; i < keyNames.Length; i++)
                {
                    var keyControl = FindKey(keyboard, keyNames[i].Trim());
                    if (keyControl != null)
                        QueueKeyState(keyboard, keyControl, true);
                }

                double releaseTime = EditorApplication.timeSinceStartup + duration;
                EditorApplication.CallbackFunction releaseAll = null;
                releaseAll = () =>
                {
                    if (EditorApplication.timeSinceStartup < releaseTime)
                        return;

                    EditorApplication.update -= releaseAll;
                    var kb = Keyboard.current;
                    if (kb == null)
                        return;

                    for (int i = 0; i < keyNames.Length; i++)
                    {
                        var keyControl = FindKey(kb, keyNames[i].Trim());
                        if (keyControl != null)
                            QueueKeyState(kb, keyControl, false);
                    }
                };
                EditorApplication.update += releaseAll;

                return $"Keys [{keys}] held for {duration:F2}s";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Simulate a mouse drag from one screen position to another in Play Mode using the Unity Input System. Takes 'duration' seconds to run, one step per frame, and returns once the button is back up.")]
        [ReadOnlyTool]
        public static async Task<string> SimulateMouseDrag(
            [ToolParam("Start X coordinate in pixels")] int start_x,
            [ToolParam("Start Y coordinate in pixels")] int start_y,
            [ToolParam("End X coordinate in pixels")] int end_x,
            [ToolParam("End Y coordinate in pixels")] int end_y,
            [ToolParam("Duration of the drag in seconds. The steps are spread over it, one per frame.", Required = false)] float duration = 0.5f,
            [ToolParam("Mouse button: left, right, or middle", Required = false)] string button = "left")
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateMouseDrag only works in Play Mode.");

            try
            {
                var mouse = EnsureMouse();
                if (mouse == null)
                    return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No mouse device found in Input System");

                duration = Mathf.Clamp(duration, 0.1f, 3f);
                int steps = Mathf.Max(5, Mathf.RoundToInt(duration * 30));
                var gap = duration / steps;

                InputState.Change(mouse.position, new Vector2(start_x, start_y));
                QueueStateEvent(mouse, pressEvent =>
                {
                    mouse.position.WriteValueIntoEvent(new Vector2(start_x, start_y), pressEvent);
                    GetMouseButton(mouse, button).WriteValueIntoEvent(1f, pressEvent);
                });

                for (int i = 1; i <= steps; i++)
                {
                    await NextFrameAfter(gap);

                    // Re-read the device: the drag now spans frames, and a domain reload in the
                    // middle of one replaces every Input System device.
                    var device = Mouse.current;
                    if (device == null)
                        return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_LOST", "The mouse device went away mid-drag.");

                    var t = (float)i / steps;
                    var at = new Vector2(Mathf.Lerp(start_x, end_x, t), Mathf.Lerp(start_y, end_y, t));
                    // The last step is the release. StateEvent.From seeds the event with the device's
                    // current state, so a release that writes only the position leaves the button at 1
                    // and every later click and drag runs with it still held.
                    var held = i < steps ? 1f : 0f;

                    QueueStateEvent(device, moveEvent =>
                    {
                        device.position.WriteValueIntoEvent(at, moveEvent);
                        GetMouseButton(device, button).WriteValueIntoEvent(held, moveEvent);
                    });
                }

                return $"Mouse drag from ({start_x},{start_y}) to ({end_x},{end_y}) over {duration:F2}s ({steps} steps)";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Simulate a touch on the screen in Play Mode: tap, press and hold, or release. Drives the Input System's Touchscreen device, which is what a mobile build reads — simulate_mouse_click drives uGUI directly and produces no touch input, so code reading Touch.activeTouches or a touch-bound InputAction never sees it.")]
        [ReadOnlyTool]
        public static string SimulateTouch(
            [ToolParam("Screen X coordinate in pixels from the left edge")] int x,
            [ToolParam("Screen Y coordinate in pixels from the bottom edge")] int y,
            [ToolParam("Action type: tap, press, or release", Required = false)] string action = "tap",
            [ToolParam("Seconds to hold a tap down. 0 uses a 50 ms tap, short but long enough to be observed.", Required = false)] float duration = 0f,
            [ToolParam("Touch id. Pass a second id to hold two fingers at once, for a pinch.", Required = false)] int touch_id = 1)
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateTouch only works in Play Mode.");

            try
            {
                var touchscreen = EnsureDevice<Touchscreen>();
                if (touchscreen == null)
                    return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No touchscreen device found in Input System");

                var position = new Vector2(x, y);

                switch ((action ?? "tap").Trim().ToLowerInvariant())
                {
                    case "press":
                        QueueTouch(touchscreen, touch_id, UnityEngine.InputSystem.TouchPhase.Began, position);
                        return $"Touch {touch_id} began at ({x},{y}) and is still down";

                    case "release":
                        QueueTouch(touchscreen, touch_id, UnityEngine.InputSystem.TouchPhase.Ended, position);
                        return $"Touch {touch_id} ended at ({x},{y})";

                    default:
                        var hold = Mathf.Clamp(duration <= 0f ? 0.05f : duration, 0.05f, 5f);
                        QueueTouch(touchscreen, touch_id, UnityEngine.InputSystem.TouchPhase.Began, position);
                        ReleaseAfter(hold, () =>
                        {
                            var device = Touchscreen.current;
                            if (device != null)
                                QueueTouch(device, touch_id, UnityEngine.InputSystem.TouchPhase.Ended, position);
                        });
                        return $"Touch {touch_id} tapped at ({x},{y}) for {hold:F2}s";
                }
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Simulate a swipe or drag on the screen in Play Mode, as one finger moving from a start point to an end point. Drives the Input System's Touchscreen device — use this rather than simulate_mouse_drag when the code under test reads touch. Takes 'duration' seconds to run, one step per frame.")]
        [ReadOnlyTool]
        public static async Task<string> SimulateTouchDrag(
            [ToolParam("Start X coordinate in pixels")] int start_x,
            [ToolParam("Start Y coordinate in pixels")] int start_y,
            [ToolParam("End X coordinate in pixels")] int end_x,
            [ToolParam("End Y coordinate in pixels")] int end_y,
            [ToolParam("Duration of the swipe in seconds. The steps are spread over it, one per frame.", Required = false)] float duration = 0.5f,
            [ToolParam("Touch id", Required = false)] int touch_id = 1)
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateTouchDrag only works in Play Mode.");

            try
            {
                var touchscreen = EnsureDevice<Touchscreen>();
                if (touchscreen == null)
                    return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No touchscreen device found in Input System");

                duration = Mathf.Clamp(duration, 0.1f, 3f);
                var steps = Mathf.Max(5, Mathf.RoundToInt(duration * 30));
                var gap = duration / steps;

                QueueTouch(touchscreen, touch_id, UnityEngine.InputSystem.TouchPhase.Began, new Vector2(start_x, start_y));

                for (var i = 1; i <= steps; i++)
                {
                    await NextFrameAfter(gap);

                    var device = Touchscreen.current;
                    if (device == null)
                        return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_LOST", "The touchscreen device went away mid-swipe.");

                    var t = (float)i / steps;
                    QueueTouch(device, touch_id,
                        i < steps ? UnityEngine.InputSystem.TouchPhase.Moved : UnityEngine.InputSystem.TouchPhase.Ended,
                        new Vector2(Mathf.Lerp(start_x, end_x, t), Mathf.Lerp(start_y, end_y, t)));
                }

                return $"Touch {touch_id} swiped from ({start_x},{start_y}) to ({end_x},{end_y}) over {duration:F2}s ({steps} steps)";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        [Description("Simulate a gamepad in Play Mode: press or hold a button, and move the sticks and triggers. Anything left unspecified keeps the value the virtual gamepad already holds, so moving a stick does not release a held button.")]
        [ReadOnlyTool]
        public static string SimulateGamepad(
            [ToolParam("Button name: south/a, east/b, west/x, north/y, start, select, left_shoulder, right_shoulder, left_trigger, right_trigger, dpad_up, dpad_down, dpad_left, dpad_right, left_stick, right_stick. Omit to only move the sticks and triggers.", Required = false)] string button = null,
            [ToolParam("Action for the button: tap, press, or release", Required = false)] string action = "tap",
            [ToolParam("Seconds to hold a tapped button. 0 uses a 50 ms tap.", Required = false)] float duration = 0f,
            [ToolParam("Left stick X, -1 to 1. Omit to leave it where it is.", Required = false)] float left_stick_x = float.NaN,
            [ToolParam("Left stick Y, -1 to 1. Omit to leave it where it is.", Required = false)] float left_stick_y = float.NaN,
            [ToolParam("Right stick X, -1 to 1. Omit to leave it where it is.", Required = false)] float right_stick_x = float.NaN,
            [ToolParam("Right stick Y, -1 to 1. Omit to leave it where it is.", Required = false)] float right_stick_y = float.NaN,
            [ToolParam("Left trigger, 0 to 1. Omit to leave it where it is.", Required = false)] float left_trigger = float.NaN,
            [ToolParam("Right trigger, 0 to 1. Omit to leave it where it is.", Required = false)] float right_trigger = float.NaN)
        {
            if (!EditorApplication.isPlaying)
                return ToolResultFormatter.ErrorMessage("PLAY_MODE_REQUIRED", "SimulateGamepad only works in Play Mode.");

            try
            {
                var gamepad = EnsureDevice<Gamepad>();
                if (gamepad == null)
                    return ToolResultFormatter.ErrorMessage("INPUT_DEVICE_NOT_FOUND", "No gamepad device found in Input System");

                GamepadButton? target = null;
                if (!string.IsNullOrWhiteSpace(button))
                {
                    target = FindGamepadButton(button);
                    if (target == null)
                        return ToolResultFormatter.ErrorMessage("BUTTON_NOT_RECOGNIZED",
                            $"Gamepad button '{button}' not recognized. Examples: south, east, start, dpad_up, left_shoulder.");
                }

                var verb = (action ?? "tap").Trim().ToLowerInvariant();
                var pressed = verb != "release";

                WriteGamepad(gamepad, target, pressed, left_stick_x, left_stick_y, right_stick_x, right_stick_y, left_trigger, right_trigger);

                if (target == null)
                    return $"Gamepad updated (left stick {gamepad.leftStick.ReadValue()}, right stick {gamepad.rightStick.ReadValue()}, " +
                           $"triggers {gamepad.leftTrigger.ReadValue():F2}/{gamepad.rightTrigger.ReadValue():F2})";

                if (verb == "press")
                    return $"Gamepad button '{button}' pressed (held down)";

                if (verb == "release")
                    return $"Gamepad button '{button}' released";

                var holdFor = Mathf.Clamp(duration <= 0f ? 0.05f : duration, 0.05f, 5f);
                var buttonToRelease = target.Value;
                ReleaseAfter(holdFor, () =>
                {
                    var device = Gamepad.current;
                    if (device != null)
                        WriteGamepad(device, buttonToRelease, false, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
                });

                return $"Gamepad button '{button}' tapped for {holdFor:F2}s";
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        private static void QueueTouch(Touchscreen touchscreen, int touchId, UnityEngine.InputSystem.TouchPhase phase, Vector2 position)
        {
            // A touch id of 0 is "no touch" to the Input System, so a caller passing 0 would queue
            // events the Touchscreen device drops on the floor.
            InputSystem.QueueStateEvent(touchscreen, new TouchState
            {
                touchId = touchId <= 0 ? 1 : touchId,
                phase = phase,
                position = position
            });

            InputSystem.Update();
        }

        /// <summary>
        /// Writes only what the caller named. <c>StateEvent.From</c> seeds the event with the device's
        /// CURRENT state, so a partial write leaves every other control where it was: moving a stick does
        /// not release a held button, and no whole-device state has to be read back and reassembled.
        /// Writing through the controls also keeps <c>GamepadState</c>'s 32-bit button bitmask out of it,
        /// which matters because LeftTrigger and RightTrigger sit outside that mask.
        /// </summary>
        private static void WriteGamepad(Gamepad gamepad, GamepadButton? button, bool pressed,
            float leftX, float leftY, float rightX, float rightY, float leftTrigger, float rightTrigger)
        {
            var left = gamepad.leftStick.ReadValue();
            var right = gamepad.rightStick.ReadValue();

            QueueStateEvent(gamepad, eventPtr =>
            {
                if (button != null)
                    gamepad[button.Value].WriteValueIntoEvent(pressed ? 1f : 0f, eventPtr);

                if (!float.IsNaN(leftX) || !float.IsNaN(leftY))
                {
                    gamepad.leftStick.WriteValueIntoEvent(new Vector2(
                        float.IsNaN(leftX) ? left.x : Mathf.Clamp(leftX, -1f, 1f),
                        float.IsNaN(leftY) ? left.y : Mathf.Clamp(leftY, -1f, 1f)), eventPtr);
                }

                if (!float.IsNaN(rightX) || !float.IsNaN(rightY))
                {
                    gamepad.rightStick.WriteValueIntoEvent(new Vector2(
                        float.IsNaN(rightX) ? right.x : Mathf.Clamp(rightX, -1f, 1f),
                        float.IsNaN(rightY) ? right.y : Mathf.Clamp(rightY, -1f, 1f)), eventPtr);
                }

                if (!float.IsNaN(leftTrigger))
                    gamepad.leftTrigger.WriteValueIntoEvent(Mathf.Clamp01(leftTrigger), eventPtr);

                if (!float.IsNaN(rightTrigger))
                    gamepad.rightTrigger.WriteValueIntoEvent(Mathf.Clamp01(rightTrigger), eventPtr);
            });
        }

        // GamepadButton already aliases the vendor names (A/B/X/Y, Cross/Circle/Square/Triangle) onto
        // South/East/West/North, so parsing the enum covers them without a mapping table of our own
        // that could contradict Unity's.
        internal static GamepadButton? FindGamepadButton(string name)
        {
            var wanted = (name ?? string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).Trim();

            // Enum.TryParse accepts an ordinal, so "3" would resolve to whichever button happens to
            // sit at 3 and the caller would never learn its name was not understood.
            if (wanted.Length == 0 || char.IsDigit(wanted[0]) || wanted[0] == '-')
                return null;

            return Enum.TryParse<GamepadButton>(wanted, true, out var parsed) ? parsed : (GamepadButton?)null;
        }

        // EditorApplication.update is the only tick a tool can schedule against while Play Mode runs,
        // so a hold-then-release is a deadline on that loop rather than a coroutine.
        /// <summary>
        /// Resumes on a later editor frame. InputSystem.Update() advances the device but does not run
        /// EventSystem.Update(), so a drag queued inside one editor tick reaches uGUI as a single
        /// pointer position: the press, the whole path and the release all arrive at the end point,
        /// and nothing between the two ends is ever seen. A frame per step is what makes 'duration'
        /// mean what its name says.
        /// </summary>
        private static Task NextFrameAfter(float seconds)
        {
            var tcs = new TaskCompletionSource<bool>();
            ReleaseAfter(seconds, () => tcs.TrySetResult(true));
            return tcs.Task;
        }

        private static void ReleaseAfter(float seconds, Action release)
        {
            var deadline = EditorApplication.timeSinceStartup + seconds;
            EditorApplication.CallbackFunction callback = null;
            callback = () =>
            {
                if (EditorApplication.timeSinceStartup < deadline)
                    return;

                EditorApplication.update -= callback;
                release();
            };

            EditorApplication.update += callback;
        }

        private static TDevice EnsureDevice<TDevice>() where TDevice : InputDevice
        {
            try
            {
                var existing = InputSystem.GetDevice<TDevice>();
                if (existing != null)
                    return existing;

                return InputSystem.AddDevice<TDevice>();
            }
            catch
            {
                return null;
            }
        }

        private static string TapKey(Keyboard keyboard, KeyControl keyControl, string key, float duration)
        {
            if (duration <= 0f)
            {
                QueueKeyState(keyboard, keyControl, true);
                EditorApplication.CallbackFunction releaseCallback = null;
                int frameToRelease = Time.frameCount + 2;
                releaseCallback = () =>
                {
                    if (Time.frameCount < frameToRelease)
                        return;

                    EditorApplication.update -= releaseCallback;
                    if (Keyboard.current != null)
                        QueueKeyState(Keyboard.current, FindKey(Keyboard.current, key), false);
                };
                EditorApplication.update += releaseCallback;
                return $"Key '{key}' tapped (1 frame)";
            }

            duration = Mathf.Clamp(duration, 0.01f, 5f);
            QueueKeyState(keyboard, keyControl, true);

            double releaseTime = EditorApplication.timeSinceStartup + duration;
            EditorApplication.CallbackFunction releaseAfterDuration = null;
            releaseAfterDuration = () =>
            {
                if (EditorApplication.timeSinceStartup < releaseTime)
                    return;

                EditorApplication.update -= releaseAfterDuration;
                if (Keyboard.current != null)
                    QueueKeyState(Keyboard.current, FindKey(Keyboard.current, key), false);
            };
            EditorApplication.update += releaseAfterDuration;
            return $"Key '{key}' held for {duration:F2}s";
        }

        private static void QueueKeyState(Keyboard keyboard, KeyControl keyControl, bool pressed)
        {
            if (keyboard == null || keyControl == null)
                return;

            QueueStateEvent(keyboard, eventPtr =>
            {
                keyControl.WriteValueIntoEvent(pressed ? 1f : 0f, eventPtr);
            });
        }

        private static void QueueStateEvent(InputDevice device, Action<InputEventPtr> writeState)
        {
            if (device == null || writeState == null)
                return;

            using (StateEvent.From(device, out var eventPtr))
            {
                writeState(eventPtr);
                InputSystem.QueueEvent(eventPtr);
            }

            // Editor-driven simulation needs an explicit update so queued events
            // are applied immediately and InputActions observe the state change.
            InputSystem.Update();
        }

        private static Keyboard EnsureKeyboard()
        {
            var keyboard = Keyboard.current;
            if (keyboard != null)
                return keyboard;

            try
            {
                keyboard = InputSystem.GetDevice<Keyboard>();
                if (keyboard != null)
                    return keyboard;

                return InputSystem.AddDevice<Keyboard>();
            }
            catch
            {
                return null;
            }
        }

        private static Mouse EnsureMouse()
        {
            var mouse = Mouse.current;
            if (mouse != null)
                return mouse;

            try
            {
                mouse = InputSystem.GetDevice<Mouse>();
                if (mouse != null)
                    return mouse;

                return InputSystem.AddDevice<Mouse>();
            }
            catch
            {
                return null;
            }
        }

        private static KeyControl FindKey(Keyboard keyboard, string keyName)
        {
            if (keyboard == null || string.IsNullOrWhiteSpace(keyName))
                return null;

            try
            {
                var control = keyboard[keyName.ToLowerInvariant()] as KeyControl;
                if (control != null)
                    return control;
            }
            catch
            {
            }

            switch (keyName.Trim().ToLowerInvariant())
            {
                case "w": return keyboard.wKey;
                case "a": return keyboard.aKey;
                case "s": return keyboard.sKey;
                case "d": return keyboard.dKey;
                case "e": return keyboard.eKey;
                case "q": return keyboard.qKey;
                case "r": return keyboard.rKey;
                case "f": return keyboard.fKey;
                case "space": return keyboard.spaceKey;
                case "leftshift":
                case "lshift":
                case "shift":
                    return keyboard.leftShiftKey;
                case "rightshift":
                case "rshift":
                    return keyboard.rightShiftKey;
                case "leftctrl":
                case "lctrl":
                case "ctrl":
                    return keyboard.leftCtrlKey;
                case "leftalt":
                case "lalt":
                case "alt":
                    return keyboard.leftAltKey;
                case "tab": return keyboard.tabKey;
                case "escape":
                case "esc":
                    return keyboard.escapeKey;
                case "enter":
                case "return":
                    return keyboard.enterKey;
                case "backspace":
                    return keyboard.backspaceKey;
                case "1": return keyboard.digit1Key;
                case "2": return keyboard.digit2Key;
                case "3": return keyboard.digit3Key;
                case "4": return keyboard.digit4Key;
                case "5": return keyboard.digit5Key;
                case "6": return keyboard.digit6Key;
                case "7": return keyboard.digit7Key;
                case "8": return keyboard.digit8Key;
                case "9": return keyboard.digit9Key;
                case "0": return keyboard.digit0Key;
                default:
                    return null;
            }
        }

        private static ButtonControl GetMouseButton(Mouse mouse, string button)
        {
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "right":
                    return mouse.rightButton;
                case "middle":
                    return mouse.middleButton;
                default:
                    return mouse.leftButton;
            }
        }
    }
}
#endif
