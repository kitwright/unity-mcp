// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.animation is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_ANIMATION
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Animation")]
    internal static class AnimationFunctions
    {
        [Description("Create an Animator Controller asset")]
        public static string CreateAnimatorController(
            [ToolParam("Name of the controller")] string name,
            [ToolParam("Save path", Required = false)] string save_path = "Assets/Animations/")
        {
            try { PathSafety.ResolveAssetPath(save_path.TrimEnd('/', '\\') + "/x"); }
            catch (PathOutsideProjectException ex)
            { return ToolResultFormatter.Error("INVALID_PATH", new { save_path, message = ex.Message }); }

            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var fullPath = $"{save_path}{name}.controller";
            var controller = AnimatorController.CreateAnimatorControllerAtPath(fullPath);
            return $"Created Animator Controller at {fullPath}";
        }

        [Description("Create an Animation Clip asset")]
        public static string CreateAnimationClip(
            [ToolParam("Name of the animation clip")] string name,
            [ToolParam("Save path", Required = false)] string save_path = "Assets/Animations/")
        {
            try { PathSafety.ResolveAssetPath(save_path.TrimEnd('/', '\\') + "/x"); }
            catch (PathOutsideProjectException ex)
            { return ToolResultFormatter.Error("INVALID_PATH", new { save_path, message = ex.Message }); }

            if (!Directory.Exists(save_path))
                Directory.CreateDirectory(save_path);

            var clip = new AnimationClip();
            clip.name = name;

            var fullPath = $"{save_path}{name}.anim";
            AssetDatabase.CreateAsset(clip, fullPath);
            AssetDatabase.Refresh();
            return $"Created Animation Clip at {fullPath}";
        }

        [Description("Assign an Animator Controller to a GameObject")]
        public static string AssignAnimator(
            [ToolParam("GameObject name, hierarchy path, or instance ID. Finds inactive objects too.")] string game_object_name,
            [ToolParam("Path to the Animator Controller asset")] string controller_path)
        {
            var go = ObjectsHelper.FindTarget(game_object_name);
            if (go == null)
                return ObjectsHelper.NotFoundText("game_object_name", game_object_name);

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(controller_path);
            if (controller == null)
                return ToolResultFormatter.Error("ANIMATOR_CONTROLLER_NOT_FOUND", new { controller_path });

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                animator = Undo.AddComponent<Animator>(go);

            Undo.RecordObject(animator, $"Assign animator to {go.name}");
            animator.runtimeAnimatorController = controller;
            return $"Assigned Animator Controller to '{go.name}'";
        }

        [Description("Get an Animator's current runtime state: active state name (resolved from the controller when possible), " +
                     "normalized time, transition status, and all parameters with their current values. " +
                     "Most useful in Play Mode; in Edit Mode a non-playing Animator reports default state info.")]
        [ReadOnlyTool]
        public static object GetAnimatorState(
            [ToolParam("GameObject identifier (instance id, name, path, tag…)")] string target,
            [ToolParam("Layer index to report the current state for", Required = false)] int layer = 0,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var resolved = ResolveAnimator(target, find_method);
            if (resolved.Error != null) return resolved.Error;
            var animator = resolved.Animator;

            if (layer < 0 || layer >= animator.layerCount)
                return Response.Error("INVALID_LAYER", new { layer, layerCount = animator.layerCount });

            var stateInfo = animator.GetCurrentAnimatorStateInfo(layer);
            var parameters = new System.Collections.Generic.List<object>();
            foreach (var p in animator.parameters)
            {
                object value;
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float: value = animator.GetFloat(p.nameHash); break;
                    case AnimatorControllerParameterType.Int: value = animator.GetInteger(p.nameHash); break;
                    case AnimatorControllerParameterType.Bool: value = animator.GetBool(p.nameHash); break;
                    case AnimatorControllerParameterType.Trigger: value = animator.GetBool(p.nameHash) ? "pending" : "idle"; break;
                    default: value = null; break;
                }
                parameters.Add(new { name = p.name, type = p.type.ToString(), value });
            }

            return Response.Success($"Animator state on '{animator.gameObject.name}' (layer {layer}).", new
            {
                gameObject = new { instanceId = ObjectIdCodec.GetSerializableId(animator.gameObject), name = animator.gameObject.name },
                controller = animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : null,
                isActiveAndEnabled = animator.isActiveAndEnabled,
                layer,
                layerCount = animator.layerCount,
                currentState = new
                {
                    name = ResolveStateName(animator, layer, stateInfo.shortNameHash),
                    shortNameHash = stateInfo.shortNameHash,
                    normalizedTime = stateInfo.normalizedTime,
                    length = stateInfo.length,
                    speed = stateInfo.speed,
                    loop = stateInfo.loop
                },
                isInTransition = animator.IsInTransition(layer),
                parameters
            });
        }

        [Description("Set an Animator parameter by name. The parameter type (Float/Int/Bool/Trigger) is detected automatically " +
                     "from the controller. For Trigger parameters pass true (or 'set') to fire, false (or 'reset') to clear. " +
                     "Runtime-only state: not undoable, resets when Play Mode exits.")]
        public static object SetAnimatorParameter(
            [ToolParam("GameObject identifier (instance id, name, path, tag…)")] string target,
            [ToolParam("Parameter name as defined in the Animator Controller")] string parameter,
            [ToolParam("Value: number for Float/Int, true/false for Bool, true/'set'/false/'reset' for Trigger")] string value,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            if (string.IsNullOrEmpty(parameter))
                return Response.Error("PARAMETER_REQUIRED");
            if (string.IsNullOrEmpty(value))
                return Response.Error("VALUE_REQUIRED");

            var resolved = ResolveAnimator(target, find_method);
            if (resolved.Error != null) return resolved.Error;
            var animator = resolved.Animator;

            AnimatorControllerParameter match = null;
            foreach (var p in animator.parameters)
            {
                if (string.Equals(p.name, parameter, System.StringComparison.Ordinal)) { match = p; break; }
            }
            if (match == null)
            {
                var available = new System.Collections.Generic.List<string>();
                foreach (var p in animator.parameters)
                    available.Add($"{p.name} ({p.type})");
                return Response.Error("PARAMETER_NOT_FOUND", new { parameter, available });
            }

            value = value.Trim();
            switch (match.type)
            {
                case AnimatorControllerParameterType.Float:
                    if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var f))
                        return Response.Error("INVALID_FLOAT", new { value });
                    animator.SetFloat(match.nameHash, f);
                    return Response.Success($"Set float '{parameter}' = {f}.");
                case AnimatorControllerParameterType.Int:
                    if (!int.TryParse(value, out var i))
                        return Response.Error("INVALID_INT", new { value });
                    animator.SetInteger(match.nameHash, i);
                    return Response.Success($"Set int '{parameter}' = {i}.");
                case AnimatorControllerParameterType.Bool:
                    if (!bool.TryParse(value, out var b))
                        return Response.Error("INVALID_BOOL", new { value });
                    animator.SetBool(match.nameHash, b);
                    return Response.Success($"Set bool '{parameter}' = {b}.");
                case AnimatorControllerParameterType.Trigger:
                    var lowered = value.ToLowerInvariant();
                    if (lowered == "true" || lowered == "set")
                    {
                        animator.SetTrigger(match.nameHash);
                        return Response.Success($"Fired trigger '{parameter}'.");
                    }
                    if (lowered == "false" || lowered == "reset")
                    {
                        animator.ResetTrigger(match.nameHash);
                        return Response.Success($"Reset trigger '{parameter}'.");
                    }
                    return Response.Error("INVALID_TRIGGER_VALUE", new { value, accepted = new[] { "true", "set", "false", "reset" } });
                default:
                    return Response.Error("UNSUPPORTED_PARAMETER_TYPE", new { type = match.type.ToString() });
            }
        }

        [Description("Play a specific Animator state by name, e.g. to drive UI or a character to a known visual state " +
                     "before taking a screenshot. In Edit Mode the animator is force-evaluated once so the pose applies " +
                     "without entering Play Mode. Runtime-only state: not undoable.")]
        public static object PlayAnimatorState(
            [ToolParam("GameObject identifier (instance id, name, path, tag…)")] string target,
            [ToolParam("State name as defined in the Animator Controller (e.g. 'Idle' or 'Base Layer.Idle')")] string state,
            [ToolParam("Layer index; -1 lets Unity pick the first layer containing the state", Required = false)] int layer = -1,
            [ToolParam("Normalized start time 0-1; omit to keep Unity's default", Required = false)] float normalized_time = float.NegativeInfinity,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            if (string.IsNullOrEmpty(state))
                return Response.Error("STATE_REQUIRED");

            var resolved = ResolveAnimator(target, find_method);
            if (resolved.Error != null) return resolved.Error;
            var animator = resolved.Animator;

            var hash = Animator.StringToHash(state);
            bool found = false;
            var resolvedLayer = layer;
            if (layer >= 0)
            {
                if (layer >= animator.layerCount)
                    return Response.Error("INVALID_LAYER", new { layer, layerCount = animator.layerCount });
                found = animator.HasState(layer, hash);
            }
            else if (layer < -1)
            {
                return Response.Error("INVALID_LAYER", new { layer, layerCount = animator.layerCount, accepted = "-1 or a valid layer index" });
            }
            else
            {
                for (int l = 0; l < animator.layerCount; l++)
                {
                    if (animator.HasState(l, hash))
                    {
                        found = true;
                        resolvedLayer = l;
                        break;
                    }
                }
            }
            if (!found)
                return Response.Error("STATE_NOT_FOUND", new { state, layer, hint = "Check the state name in the Animator Controller (short name or 'LayerName.StateName')." });

            animator.Play(hash, resolvedLayer, normalized_time);

            // In Edit Mode the animator doesn't tick on its own -- evaluate once so the pose is visible immediately.
            if (!EditorApplication.isPlaying)
                animator.Update(0f);

            return Response.Success($"Playing state '{state}' on '{animator.gameObject.name}' (layer {layer}).", new
            {
                gameObject = new { instanceId = ObjectIdCodec.GetSerializableId(animator.gameObject), name = animator.gameObject.name },
                state,
                layer = resolvedLayer,
                requestedLayer = layer
            });
        }

        [Description("Add a parameter to an Animator Controller asset. Parameters are what transitions test, so add " +
                     "them before add_animator_transition references them.")]
        public static string AddAnimatorParameter(
            [ToolParam("Path to the .controller asset")] string controller_path,
            [ToolParam("Parameter name")] string name,
            [ToolParam("Parameter type: 'float', 'int', 'bool' or 'trigger'")] string type,
            [ToolParam("Default value: a number for float/int, 'true'/'false' for bool. Ignored for trigger.", Required = false)] string default_value = null)
        {
            var controller = LoadController(controller_path, out var error);
            if (controller == null) return error;

            if (controller.parameters.Any(p => p.name == name))
                return ToolResultFormatter.Error("PARAMETER_EXISTS", new { controller_path, name });

            if (!Enum.TryParse<AnimatorControllerParameterType>(type, true, out var parameterType))
                return ToolResultFormatter.Error("UNKNOWN_PARAMETER_TYPE", new
                {
                    type,
                    valid = Enum.GetNames(typeof(AnimatorControllerParameterType))
                });

            // Parsed before the parameter is added, not after. AddParameter writes to the controller
            // asset, so returning INVALID_DEFAULT_VALUE afterwards left the parameter behind - and
            // the retry with a corrected value then answered PARAMETER_EXISTS, leaving no way
            // forward through the tool at all.
            var hasDefault = !string.IsNullOrWhiteSpace(default_value);
            float floatDefault = 0f;
            int intDefault = 0;
            bool boolDefault = false;
            if (hasDefault)
            {
                var parsed = true;
                switch (parameterType)
                {
                    case AnimatorControllerParameterType.Float:
                        parsed = ValueConverter.TryParseFloat(default_value, out floatDefault);
                        break;
                    case AnimatorControllerParameterType.Int:
                        parsed = ValueConverter.TryParseInt(default_value, out intDefault);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        parsed = bool.TryParse(default_value, out boolDefault);
                        break;
                }

                if (!parsed)
                    return ToolResultFormatter.Error("INVALID_DEFAULT_VALUE", new { default_value, type });
            }

            controller.AddParameter(name, parameterType);

            if (hasDefault)
            {
                // The property hands back a copy of the array, so the edit only lands on assignment back.
                var parameters = controller.parameters;
                var added = parameters[parameters.Length - 1];
                switch (parameterType)
                {
                    case AnimatorControllerParameterType.Float: added.defaultFloat = floatDefault; break;
                    case AnimatorControllerParameterType.Int: added.defaultInt = intDefault; break;
                    case AnimatorControllerParameterType.Bool: added.defaultBool = boolDefault; break;
                }
                controller.parameters = parameters;
            }

            SaveController(controller);
            return $"Added {parameterType} parameter '{name}' to '{controller_path}'.";
        }

        [Description("Add a state to an Animator Controller layer, optionally binding an Animation Clip to it. " +
                     "create_animator_controller leaves a layer with nothing but an empty state machine; this is what " +
                     "fills it in.")]
        public static string AddAnimatorState(
            [ToolParam("Path to the .controller asset")] string controller_path,
            [ToolParam("Name for the new state")] string state_name,
            [ToolParam("Path to an .anim clip to play in this state", Required = false)] string clip_path = null,
            [ToolParam("Layer index. Default 0.", Required = false)] int layer = 0,
            [ToolParam("Make this the layer's default (entry) state", Required = false)] bool make_default = false)
        {
            var stateMachine = ResolveStateMachine(controller_path, layer, out var controller, out var error);
            if (stateMachine == null) return error;

            if (stateMachine.states.Any(s => s.state.name == state_name))
                return ToolResultFormatter.Error("STATE_EXISTS", new { controller_path, state_name, layer });

            // Loaded before the state exists. AddState writes to the controller, so returning
            // ANIMATION_CLIP_NOT_FOUND afterwards left the state behind, and the retry with a
            // corrected clip_path then answered STATE_EXISTS - no way forward through the tool.
            AnimationClip clip = null;
            if (!string.IsNullOrWhiteSpace(clip_path))
            {
                clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clip_path);
                if (clip == null)
                    return ToolResultFormatter.Error("ANIMATION_CLIP_NOT_FOUND", new { clip_path });
            }

            var state = stateMachine.AddState(state_name);
            if (clip != null)
                state.motion = clip;

            if (make_default)
                stateMachine.defaultState = state;

            SaveController(controller);
            return $"Added state '{state_name}' to layer {layer} of '{controller_path}'" +
                   (make_default ? " (now the default state)." : ".");
        }

        [Description("Add a transition between two Animator states. from_state also accepts 'any' for an Any State " +
                     "transition and 'entry' for an entry transition; to_state also accepts 'exit'.\n" +
                     "conditions is a JSON array, e.g. [{\"parameter\":\"Speed\",\"mode\":\"Greater\",\"threshold\":0.1}]. " +
                     "mode is one of If, IfNot, Greater, Less, Equals, NotEqual — If/IfNot are the bool and trigger " +
                     "forms and ignore threshold. A transition with no conditions and has_exit_time=false fires " +
                     "immediately, which is almost never what you want.")]
        public static string AddAnimatorTransition(
            [ToolParam("Path to the .controller asset")] string controller_path,
            [ToolParam("Source state name, or 'any' / 'entry'")] string from_state,
            [ToolParam("Destination state name, or 'exit'")] string to_state,
            [ToolParam("JSON array of conditions (see description)", Required = false)] string conditions = null,
            [ToolParam("Layer index. Default 0.", Required = false)] int layer = 0,
            [ToolParam("Wait for the source clip to finish before transitioning", Required = false)] bool has_exit_time = false,
            [ToolParam("Blend duration in seconds. Default 0.25.", Required = false)] float duration = 0.25f)
        {
            var stateMachine = ResolveStateMachine(controller_path, layer, out var controller, out var error);
            if (stateMachine == null) return error;

            var from = (from_state ?? string.Empty).Trim().ToLowerInvariant();
            var toExit = string.Equals((to_state ?? string.Empty).Trim(), "exit", StringComparison.OrdinalIgnoreCase);

            AnimatorState destination = null;
            if (!toExit)
            {
                destination = FindState(stateMachine, to_state);
                if (destination == null)
                    return ToolResultFormatter.Error("STATE_NOT_FOUND", new { state = to_state, layer, controller_path });
            }

            // Parsed before anything is created. Applying conditions to a transition that already
            // existed meant a bad entry halfway down the list left the transition behind carrying
            // the entries before it - a rule nobody asked for, on a call that reported failure.
            List<ParsedCondition> parsedConditions = null;
            if (!string.IsNullOrWhiteSpace(conditions) &&
                !TryParseConditions(controller, conditions, out parsedConditions, out var conditionError))
                return conditionError;

            AnimatorStateTransition transition;
            if (from == "any" || from == "anystate")
            {
                if (toExit)
                    return ToolResultFormatter.Error("INVALID_TRANSITION", new { hint = "An Any State transition cannot target Exit." });
                transition = stateMachine.AddAnyStateTransition(destination);
            }
            else if (from == "entry")
            {
                return ToolResultFormatter.Error("ENTRY_TRANSITION_UNSUPPORTED", new
                {
                    hint = "Entry transitions carry no exit time or duration. Use add_animator_state with " +
                           "make_default=true to pick the state the layer enters."
                });
            }
            else
            {
                var source = FindState(stateMachine, from_state);
                if (source == null)
                    return ToolResultFormatter.Error("STATE_NOT_FOUND", new { state = from_state, layer, controller_path });
                transition = toExit ? source.AddExitTransition() : source.AddTransition(destination);
            }

            transition.hasExitTime = has_exit_time;
            transition.duration = duration;

            if (parsedConditions != null)
                foreach (var condition in parsedConditions)
                    transition.AddCondition(condition.Mode, condition.Threshold, condition.Parameter);

            var conditionCount = transition.conditions.Length;

            SaveController(controller);
            return $"Added transition {from_state} -> {to_state} on layer {layer} of '{controller_path}' " +
                   $"({conditionCount} condition(s), exitTime={has_exit_time}, duration={duration}).";
        }

        [Description("Write a float curve into an Animation Clip. create_animation_clip makes an empty clip; this is " +
                     "what puts animation in it.\n" +
                     "keys is a JSON array of {\"time\":<seconds>,\"value\":<float>} — at least two to see movement. " +
                     "property is the serialized field name, which for a Transform is a single axis: " +
                     "'m_LocalPosition.x', 'm_LocalScale.y', 'm_LocalRotation.z'. relative_path is the child path " +
                     "under the animated root ('' for the root itself, 'Body/Head' for a descendant). " +
                     "Curves are linear-interpolated between keys; re-running for the same binding replaces it.")]
        public static string SetClipCurve(
            [ToolParam("Path to the .anim clip asset")] string clip_path,
            [ToolParam("Serialized property name, e.g. 'm_LocalPosition.x'")] string property,
            [ToolParam("JSON array of {time, value} keyframes")] string keys,
            [ToolParam("Child path under the animated root. Empty for the root object.", Required = false)] string relative_path = "",
            [ToolParam("Component type the property lives on. Default 'Transform'.", Required = false)] string type = "Transform")
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clip_path);
            if (clip == null)
                return ToolResultFormatter.Error("ANIMATION_CLIP_NOT_FOUND", new { clip_path });

            var componentType = TypeResolver.Resolve(type);
            if (componentType == null)
                return ToolResultFormatter.Error("TYPE_NOT_FOUND", new { type });

            JArray parsedKeys;
            try { parsedKeys = JArray.Parse(keys); }
            catch (Exception ex) { return ToolResultFormatter.Error("INVALID_KEYS_JSON", new { message = ex.Message }); }

            if (parsedKeys.Count == 0)
                return ToolResultFormatter.Error("NO_KEYFRAMES", new { hint = "Pass at least one {time, value} pair." });

            var keyframes = new Keyframe[parsedKeys.Count];
            for (var i = 0; i < parsedKeys.Count; i++)
            {
                var time = parsedKeys[i]["time"];
                var value = parsedKeys[i]["value"];
                if (time == null || value == null)
                    return ToolResultFormatter.Error("INVALID_KEYFRAME", new { index = i, hint = "Each key needs both 'time' and 'value'." });
                keyframes[i] = new Keyframe(time.Value<float>(), value.Value<float>());
            }

            var curve = new AnimationCurve(keyframes);
            for (var i = 0; i < curve.length; i++)
            {
                AnimationUtility.SetKeyLeftTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(curve, i, AnimationUtility.TangentMode.Linear);
            }

            var binding = EditorCurveBinding.FloatCurve(relative_path ?? string.Empty, componentType, property);
            Undo.RecordObject(clip, $"Set curve {property} on {clip.name}");
            AnimationUtility.SetEditorCurve(clip, binding, curve);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssets();

            return $"Set {curve.length}-key curve on '{property}' ({componentType.Name}, path '{relative_path}') in '{clip_path}'. " +
                   $"Clip length is now {clip.length:0.###}s.";
        }

        // -------- Helpers --------

        private static AnimatorController LoadController(string controllerPath, out string error)
        {
            error = null;
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
                error = ToolResultFormatter.Error("ANIMATOR_CONTROLLER_NOT_FOUND", new { controller_path = controllerPath });
            return controller;
        }

        private static AnimatorStateMachine ResolveStateMachine(string controllerPath, int layer, out AnimatorController controller, out string error)
        {
            controller = LoadController(controllerPath, out error);
            if (controller == null) return null;

            if (layer < 0 || layer >= controller.layers.Length)
            {
                error = ToolResultFormatter.Error("LAYER_OUT_OF_RANGE", new
                {
                    layer,
                    layerCount = controller.layers.Length,
                    controller_path = controllerPath
                });
                return null;
            }

            return controller.layers[layer].stateMachine;
        }

        private static AnimatorState FindState(AnimatorStateMachine stateMachine, string name)
        {
            foreach (var child in stateMachine.states)
            {
                if (string.Equals(child.state.name, name, StringComparison.Ordinal))
                    return child.state;
            }
            return null;
        }

        private struct ParsedCondition
        {
            public AnimatorConditionMode Mode;
            public float Threshold;
            public string Parameter;
        }

        /// <summary>
        /// Every condition validated before any of them is written. Applying them straight onto the
        /// transition meant a list whose second entry named a missing parameter left the first one
        /// applied and the transition in place - a rule the caller never asked for, on a transition
        /// the error said had failed.
        /// </summary>
        private static bool TryParseConditions(
            AnimatorController controller, string conditions, out List<ParsedCondition> result, out string error)
        {
            result = new List<ParsedCondition>();
            error = null;

            JArray parsed;
            try { parsed = JArray.Parse(conditions); }
            catch (Exception ex)
            {
                error = ToolResultFormatter.Error("INVALID_CONDITIONS_JSON", new { message = ex.Message });
                return false;
            }

            foreach (var entry in parsed)
            {
                var parameter = entry["parameter"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(parameter))
                {
                    error = ToolResultFormatter.Error("INVALID_CONDITION", new { hint = "Each condition needs a 'parameter'." });
                    return false;
                }

                // A condition on a parameter that does not exist is silently dropped by Unity, which
                // leaves a transition that never fires and no sign of why.
                if (!controller.parameters.Any(p => p.name == parameter))
                {
                    error = ToolResultFormatter.Error("PARAMETER_NOT_FOUND", new
                    {
                        parameter,
                        available = controller.parameters.Select(p => p.name).ToArray(),
                        hint = "Add it with add_animator_parameter first."
                    });
                    return false;
                }

                var modeName = entry["mode"]?.Value<string>() ?? "If";
                if (!Enum.TryParse<AnimatorConditionMode>(modeName, true, out var mode))
                {
                    error = ToolResultFormatter.Error("UNKNOWN_CONDITION_MODE", new
                    {
                        mode = modeName,
                        valid = Enum.GetNames(typeof(AnimatorConditionMode))
                    });
                    return false;
                }

                result.Add(new ParsedCondition
                {
                    Mode = mode,
                    Threshold = entry["threshold"]?.Value<float>() ?? 0f,
                    Parameter = parameter
                });
            }

            return true;
        }

        private static void SaveController(AnimatorController controller)
        {
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        private struct ResolvedAnimator
        {
            public Animator Animator;
            public object Error;
        }

        private static ResolvedAnimator ResolveAnimator(string target, string findMethod)
        {
            var go = ObjectsHelper.FindObject(target, findMethod, searchInactive: true);
            if (go == null)
                return new ResolvedAnimator { Error = ObjectsHelper.NotFound("target", target, findMethod) };

            var animator = go.GetComponent<Animator>();
            if (animator == null)
                return new ResolvedAnimator { Error = Response.Error("NO_ANIMATOR_ON_TARGET", new { target = go.name }) };

            if (animator.runtimeAnimatorController == null)
                return new ResolvedAnimator { Error = Response.Error("NO_CONTROLLER_ASSIGNED", new { target = go.name }) };

            return new ResolvedAnimator { Animator = animator };
        }

        // Best-effort: resolve the current state's display name from the editor-side controller asset.
        // Returns null when the controller isn't an AnimatorController (e.g. pure override chains we can't unwrap)
        // or the hash isn't found (state inside a nested sub-state machine, etc).
        private static string ResolveStateName(Animator animator, int layer, int shortNameHash)
        {
            var runtimeController = animator.runtimeAnimatorController;
            var overrideController = runtimeController as AnimatorOverrideController;
            if (overrideController != null)
                runtimeController = overrideController.runtimeAnimatorController;

            var controller = runtimeController as AnimatorController;
            if (controller == null || layer >= controller.layers.Length)
                return null;

            foreach (var childState in controller.layers[layer].stateMachine.states)
            {
                if (Animator.StringToHash(childState.state.name) == shortNameHash)
                    return childState.state.name;
            }
            return null;
        }
    }
}
#endif
