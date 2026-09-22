// Copyright (C) KitWright. All rights reserved.
using System;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
#if KITWRIGHT_ANIMATION
using UnityEngine.Animations;
#endif

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("LodConstraint")]
    internal static class LodConstraintFunctions
    {
        // ----- LOD -----

        [Description("Create or reconfigure a LODGroup on a GameObject with N evenly-spaced LOD levels. Existing child Renderers are assigned to LOD0; other levels start empty.")]
        public static object CreateLodGroup(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Number of LOD levels (1-8)", Required = false)] int levels = 3)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            levels = Mathf.Clamp(levels, 1, 8);
            // `== null`, not `??`: a missing component comes back from GetComponent as a stub that
            // Unity's == calls null and reference comparison does not, so ?? never added it.
            var group = go.GetComponent<LODGroup>();
            if (group == null)
                group = Undo.AddComponent<LODGroup>(go);
            Undo.RecordObject(group, "Configure LODGroup");

            var renderers = go.GetComponentsInChildren<Renderer>(true);
            var lods = new LOD[levels];
            for (int i = 0; i < levels; i++)
            {
                // Transition heights descend geometrically: 0.5, 0.25, ... last level culls at ~0.01.
                // Min, not a flat 0.01: from 8 levels up the geometric run passes under 0.01, and a
                // cull height above the level before it makes Unity reject the whole SetLODs call.
                float geometric = Mathf.Pow(0.5f, i + 1);
                float height = (i == levels - 1) ? Mathf.Min(0.01f, geometric) : geometric;
                lods[i] = new LOD(height, i == 0 ? renderers : Array.Empty<Renderer>());
            }
            group.SetLODs(lods);
            group.RecalculateBounds();
            EditorUtility.SetDirty(group);

            return Response.Success($"LODGroup with {levels} level(s) on '{go.name}'.", new
            {
                levels,
                lod0RendererCount = renderers.Length,
                transitionHeights = lods.Select(l => l.screenRelativeTransitionHeight).ToArray()
            });
        }

        [Description("Get LODGroup info on a GameObject: level count, per-level transition height and renderer count.")]
        [ReadOnlyTool]
        public static object GetLodGroupInfo(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);
            var group = go.GetComponent<LODGroup>();
            if (group == null) return Response.Error("NO_LOD_GROUP", new { target });

            var lods = group.GetLODs();
            return Response.Success($"LODGroup on '{go.name}'.", new
            {
                levelCount = lods.Length,
                size = group.size,
                fadeMode = group.fadeMode.ToString(),
                levels = lods.Select((l, i) => new
                {
                    index = i,
                    screenRelativeTransitionHeight = l.screenRelativeTransitionHeight,
                    rendererCount = l.renderers?.Length ?? 0
                }).ToArray()
            });
        }

        // ----- Constraints -----
        // Only this half is gated: the LOD half above does not need the animation module.
#if KITWRIGHT_ANIMATION

        [Description("Add an animation constraint (position, rotation, scale, aim, lookat, parent) to a GameObject, optionally binding a source object and activating it.")]
        public static object AddConstraint(
            [ToolParam("Target GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Constraint type: position, rotation, scale, aim, lookat, parent")] string type,
            [ToolParam("Source GameObject to drive the constraint", Required = false)] string source = null,
            [ToolParam("Activate (and lock) the constraint immediately", Required = false)] bool activate = true)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var (componentType, canonical) = ResolveConstraintType(type);
            if (componentType == null)
                return Response.Error("INVALID_CONSTRAINT_TYPE", new { type, valid = new[] { "position", "rotation", "scale", "aim", "lookat", "parent" } });

            // Resolved before the component is added: doing it after left a dead constraint on the
            // object whenever the source name was a typo, next to an answer that said the call failed.
            Transform sourceTransform = null;
            if (!string.IsNullOrEmpty(source))
            {
                var src = ObjectsHelper.FindTarget(source);
                if (src == null) return Response.Error("SOURCE_NOT_FOUND", new { source });
                sourceTransform = src.transform;
            }

            var existing = go.GetComponent(componentType);
            var component = existing != null ? existing : Undo.AddComponent(go, componentType);
            var constraint = (IConstraint)component;

            if (sourceTransform != null)
            {
                Undo.RecordObject(component, "Add constraint source");
                constraint.AddSource(new ConstraintSource { sourceTransform = sourceTransform, weight = 1f });
            }

            if (activate)
            {
                constraint.locked = true;
                constraint.constraintActive = true;
            }

            EditorUtility.SetDirty(component);
            return Response.Success($"{canonical} constraint on '{go.name}'.", new
            {
                type = canonical,
                sourceCount = constraint.sourceCount,
                active = constraint.constraintActive,
                locked = constraint.locked
            });
        }

        [Description("List all animation constraints on a GameObject with type, active/locked state, weight, and source count.")]
        [ReadOnlyTool]
        public static object GetConstraintInfo(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var constraints = go.GetComponents<IConstraint>();
            if (constraints.Length == 0) return Response.Error("NO_CONSTRAINTS", new { target });

            var data = constraints.Select(c => new
            {
                type = c.GetType().Name,
                active = c.constraintActive,
                locked = c.locked,
                weight = c.weight,
                sourceCount = c.sourceCount
            }).ToArray();

            return Response.Success($"{data.Length} constraint(s) on '{go.name}'.", new { constraints = data });
        }

        internal static (Type type, string canonical) ResolveConstraintType(string type)
        {
            switch (type?.ToLowerInvariant())
            {
                case "position": return (typeof(PositionConstraint), "Position");
                case "rotation": return (typeof(RotationConstraint), "Rotation");
                case "scale": return (typeof(ScaleConstraint), "Scale");
                case "aim": return (typeof(AimConstraint), "Aim");
                case "lookat": return (typeof(LookAtConstraint), "LookAt");
                case "parent": return (typeof(ParentConstraint), "Parent");
                default: return (null, null);
            }
        }
#endif
    }
}
