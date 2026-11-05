// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.animation is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_ANIMATION
using System;
using System.Collections.Generic;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Animation")]
    internal static class AnimationInspectFunctions
    {
        private const int MaxBindings = 500;

        [Description("Add a layer to an Animator Controller asset. The layer starts with an empty state machine; " +
                     "fill it with add_animator_state using the new layer's index (get_animator_controller lists it).")]
        public static object AddAnimatorLayer(
            [ToolParam("Path to the .controller asset")] string controller_path,
            [ToolParam("Layer name")] string name,
            [ToolParam("Blending mode: 'Override' or 'Additive'. Default Override.", Required = false)] string blending_mode = "Override",
            [ToolParam("Layer weight, 0 to 1. Default 1.", Required = false)] float weight = 1f)
        {
            if (string.IsNullOrWhiteSpace(name))
                return Response.Error("INVALID_PARAM", new { param = "name", provided = name });

            if (float.IsNaN(weight) || float.IsInfinity(weight) || weight < 0f || weight > 1f)
                return Response.Error("INVALID_PARAM", new { param = "weight", provided = weight.ToString() },
                    "weight must be a finite number from 0 to 1.");

            if (!ValueConverter.TryParseEnum<AnimatorLayerBlendingMode>(blending_mode, out var mode))
                return Response.Error("UNKNOWN_BLENDING_MODE",
                    new { blending_mode, valid = Enum.GetNames(typeof(AnimatorLayerBlendingMode)) });

            var controller = LoadController(controller_path, out var error);
            if (controller == null) return error;

            if (controller.layers.Any(l => l.name == name))
                return Response.Error("LAYER_EXISTS", new { controller_path, name });

            Undo.RecordObject(controller, $"Add layer {name}");
            controller.AddLayer(name);

            // layers hands back a copy, so the weight and blending only land on assignment back.
            var layers = controller.layers;
            var index = layers.Length - 1;
            layers[index].defaultWeight = weight;
            layers[index].blendingMode = mode;
            controller.layers = layers;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);

            return Response.Success($"Added layer '{name}' (index {index}) to '{controller_path}'.",
                new { controller_path, name, index, blendingMode = mode.ToString(), weight });
        }

        [Description("Read an Animator Controller's full structure: parameters, layers, states (with motion and speed) " +
                     "and transitions (with conditions). States inside sub-state machines are listed with a 'Sub/State' name.")]
        [ReadOnlyTool]
        public static object GetAnimatorController(
            [ToolParam("Path to the .controller asset")] string controller_path)
        {
            var controller = LoadController(controller_path, out var error);
            if (controller == null) return error;

            var parameters = controller.parameters.Select(p => new
            {
                name = p.name,
                type = p.type.ToString(),
                defaultValue = DefaultOf(p)
            }).ToArray();

            var layers = controller.layers.Select((layer, index) => new
            {
                index,
                name = layer.name,
                weight = layer.defaultWeight,
                blendingMode = layer.blendingMode.ToString(),
                defaultState = layer.stateMachine.defaultState != null ? layer.stateMachine.defaultState.name : null,
                states = CollectStates(layer.stateMachine, string.Empty),
                anyStateTransitions = layer.stateMachine.anyStateTransitions.Select(DescribeTransition).ToArray()
            }).ToArray();

            return Response.Success($"Read '{controller_path}' ({parameters.Length} parameter(s), {layers.Length} layer(s)).",
                new { controller_path, name = controller.name, parameters, layers });
        }

        [Description("Read an Animation Clip's metadata and its float curve bindings. include_keys adds each binding's " +
                     "keyframes; a non-finite tangent (a stepped key) is reported as null. At most 500 bindings are listed.")]
        [ReadOnlyTool]
        public static object GetAnimationClip(
            [ToolParam("Path to the .anim clip asset")] string clip_path,
            [ToolParam("Include every binding's keyframes. Default false.", Required = false)] bool include_keys = false)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clip_path);
            if (clip == null)
                return Response.Error("ANIMATION_CLIP_NOT_FOUND", new { clip_path });

            var bindings = AnimationUtility.GetCurveBindings(clip);
            var listed = bindings.Take(MaxBindings).Select(binding =>
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                return new
                {
                    path = binding.path,
                    type = binding.type?.FullName,
                    property = binding.propertyName,
                    keyCount = curve?.length ?? 0,
                    keys = include_keys && curve != null
                        ? curve.keys.Select(k => new
                        {
                            time = k.time,
                            value = k.value,
                            inTangent = Finite(k.inTangent),
                            outTangent = Finite(k.outTangent)
                        }).ToArray()
                        : null
                };
            }).ToArray();

            return Response.Success($"Read '{clip_path}' ({bindings.Length} curve(s)).", new
            {
                clip_path,
                name = clip.name,
                length = clip.length,
                frameRate = clip.frameRate,
                isLooping = clip.isLooping,
                wrapMode = clip.wrapMode.ToString(),
                legacy = clip.legacy,
                curveCount = bindings.Length,
                truncated = bindings.Length > MaxBindings,
                objectReferenceCurveCount = AnimationUtility.GetObjectReferenceCurveBindings(clip).Length,
                eventCount = AnimationUtility.GetAnimationEvents(clip).Length,
                bindings = listed
            });
        }

        [Description("Remove one float curve binding from an Animation Clip. Destructive: requires confirm=true. " +
                     "dry_run reports the binding without removing it. Use get_animation_clip to see the bindings.")]
        public static object RemoveAnimationCurve(
            [ToolParam("Path to the .anim clip asset")] string clip_path,
            [ToolParam("Component type of the binding, e.g. 'Transform'")] string type,
            [ToolParam("Curve property name, e.g. 'm_LocalPosition.x'")] string property,
            [ToolParam("Child path under the animated root. Empty for the root object.", Required = false)] string relative_path = "",
            [ToolParam("Must be true to actually remove the curve", Required = false)] bool confirm = false,
            [ToolParam("Report the curve that would be removed without removing it", Required = false)] bool dry_run = false)
        {
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clip_path);
            if (clip == null)
                return Response.Error("ANIMATION_CLIP_NOT_FOUND", new { clip_path });

            var componentType = TypeResolver.Resolve(type);
            if (componentType == null)
                return Response.Error("TYPE_NOT_FOUND", new { type });

            if (string.IsNullOrWhiteSpace(property))
                return Response.Error("INVALID_PARAM", new { param = "property", provided = property });

            var binding = EditorCurveBinding.FloatCurve(relative_path ?? string.Empty, componentType, property);
            var curve = AnimationUtility.GetEditorCurve(clip, binding);
            if (curve == null)
                return Response.Error("CURVE_NOT_FOUND", new { clip_path, type = componentType.FullName, property, relative_path });

            if (dry_run)
                return Response.Success($"Would remove the {curve.length}-key curve on '{property}' from '{clip_path}'.",
                    new { clip_path, property, keyCount = curve.length, dry_run = true });

            if (!confirm)
                return Response.Error("CONFIRM_REQUIRED", new { clip_path, property, keyCount = curve.length },
                    "Removing a curve is destructive. Pass confirm=true to proceed, or dry_run=true to preview.");

            Undo.RecordObject(clip, $"Remove curve {property} from {clip.name}");
            AnimationUtility.SetEditorCurve(clip, binding, null);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);

            return Response.Success($"Removed the {curve.length}-key curve on '{property}' from '{clip_path}'.",
                new { clip_path, property, removedKeys = curve.length });
        }

        private static AnimatorController LoadController(string controllerPath, out object error)
        {
            error = null;
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
            if (controller == null)
                error = Response.Error("ANIMATOR_CONTROLLER_NOT_FOUND", new { controller_path = controllerPath });
            return controller;
        }

        private static object DefaultOf(AnimatorControllerParameter p)
        {
            switch (p.type)
            {
                case AnimatorControllerParameterType.Float: return p.defaultFloat;
                case AnimatorControllerParameterType.Int: return p.defaultInt;
                case AnimatorControllerParameterType.Bool: return p.defaultBool;
                default: return null;
            }
        }

        private static List<object> CollectStates(AnimatorStateMachine machine, string prefix)
        {
            var states = new List<object>();
            foreach (var child in machine.states)
            {
                var state = child.state;
                states.Add(new
                {
                    name = prefix + state.name,
                    motion = DescribeMotion(state.motion),
                    speed = state.speed,
                    tag = state.tag,
                    transitions = state.transitions.Select(DescribeTransition).ToArray()
                });
            }

            foreach (var sub in machine.stateMachines)
                states.AddRange(CollectStates(sub.stateMachine, prefix + sub.stateMachine.name + "/"));

            return states;
        }

        private static object DescribeMotion(Motion motion)
        {
            if (motion == null) return null;
            return new { name = motion.name, type = motion.GetType().Name, path = AssetDatabase.GetAssetPath(motion) };
        }

        private static object DescribeTransition(AnimatorStateTransition t)
        {
            var to = t.isExit ? "Exit"
                : t.destinationState != null ? t.destinationState.name
                : t.destinationStateMachine != null ? t.destinationStateMachine.name
                : null;

            return new
            {
                to,
                hasExitTime = t.hasExitTime,
                exitTime = t.exitTime,
                duration = t.duration,
                conditions = t.conditions.Select(c => new { parameter = c.parameter, mode = c.mode.ToString(), threshold = c.threshold }).ToArray()
            };
        }

        private static float? Finite(float value) =>
            float.IsNaN(value) || float.IsInfinity(value) ? (float?)null : value;
    }
}
#endif
