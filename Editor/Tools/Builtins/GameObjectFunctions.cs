// Copyright (C) KitWright. Licensed under MIT.
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("GameObject")]
    internal static class GameObjectFunctions
    {
        [Description("Create a new empty GameObject in the scene. Returns the new instanceId so it can be targeted by_id in follow-up calls.")]
        public static object CreateGameObject(
            [ToolParam("Name of the new GameObject")] string name,
            [ToolParam("Parent GameObject identifier (instance id, name, or path)", Required = false)] string parent = null,
            [ToolParam("How to resolve parent (by_id, by_name, by_path, by_id_or_name_or_path)", Required = false)] string find_method = null)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            if (!string.IsNullOrEmpty(parent))
            {
                var parentGo = ObjectsHelper.FindObject(parent, find_method);
                if (parentGo == null)
                    return Response.Error("PARENT_NOT_FOUND", new { parent, find_method });
                Undo.SetTransformParent(go.transform, parentGo.transform, $"Set parent of {name}");
            }

            Selection.activeGameObject = go;
            return Response.Success($"Created GameObject '{name}'.", GameObjectSerializer.DescribeCreated(go));
        }

        [Description("Create a primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad).")]
        public static object CreatePrimitive(
            [ToolParam("Name of the new object")] string name,
            [ToolParam("Primitive type: Cube, Sphere, Capsule, Cylinder, Plane, Quad")] string primitive_type,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = "0,0,0",
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = "1,1,1",
            [ToolParam("Euler rotation as 'x,y,z'", Required = false)] string rotation = "0,0,0",
            [ToolParam("Parent GameObject identifier (instance id, name, or path)", Required = false)] string parent = null,
            [ToolParam("How to resolve parent (by_id, by_name, by_path, by_id_or_name_or_path)", Required = false)] string find_method = null)
        {
            PrimitiveType type;
            switch (primitive_type.ToLowerInvariant())
            {
                case "cube": type = PrimitiveType.Cube; break;
                case "sphere": type = PrimitiveType.Sphere; break;
                case "capsule": type = PrimitiveType.Capsule; break;
                case "cylinder": type = PrimitiveType.Cylinder; break;
                case "plane": type = PrimitiveType.Plane; break;
                case "quad": type = PrimitiveType.Quad; break;
                default: return Response.Error("UNKNOWN_PRIMITIVE", new { primitive_type });
            }

            if (!ValueConverter.TryParseVector3(position, out var primPos, out var primPosErr))
                return Response.Error("INVALID_PARAM", new { param = "position", provided = position, expected = "Vector3 'x,y,z'", detail = primPosErr });
            if (!ValueConverter.TryParseVector3(scale, out var primScale, out var primScaleErr))
                return Response.Error("INVALID_PARAM", new { param = "scale", provided = scale, expected = "Vector3 'x,y,z'", detail = primScaleErr });
            if (!ValueConverter.TryParseVector3(rotation, out var primRot, out var primRotErr))
                return Response.Error("INVALID_PARAM", new { param = "rotation", provided = rotation, expected = "Vector3 'x,y,z'", detail = primRotErr });

            // Resolved before the primitive exists, so a bad parent cannot leave one behind.
            GameObject parentGo = null;
            if (!string.IsNullOrEmpty(parent))
            {
                parentGo = ObjectsHelper.FindObject(parent, find_method);
                if (parentGo == null)
                    return Response.Error("PARENT_NOT_FOUND", new { parent, find_method });
            }

            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");
            if (parentGo != null)
                Undo.SetTransformParent(go.transform, parentGo.transform, $"Set parent of {name}");
            go.transform.position = primPos;
            go.transform.eulerAngles = primRot;
            go.transform.localScale = primScale;
            Selection.activeGameObject = go;

            return Response.Success($"Created {primitive_type} '{name}'.", GameObjectSerializer.DescribeCreated(go, includeComponents: true));
        }

        [Description("Delete a GameObject. Targets resolved through ObjectsHelper (instance id, name, path, tag, layer, component).")]
        public static object DeleteGameObject(
            [ToolParam("Identifier of the GameObject to delete")] string target,
            [ToolParam("How to resolve the target (by_id/by_name/by_path/by_tag/by_layer/by_component)", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var info = new { instanceId = ObjectIdCodec.GetSerializableId(go), name = go.name };
            Undo.DestroyObjectImmediate(go);
            return Response.Success($"Deleted GameObject '{info.name}'.", info);
        }

        [Description("Duplicate a GameObject and optionally rename the copy.")]
        public static object DuplicateGameObject(
            [ToolParam("Identifier of the GameObject to duplicate")] string target,
            [ToolParam("Name for the duplicate", Required = false)] string new_name = null,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var dup = Object.Instantiate(go);
            Undo.RegisterCreatedObjectUndo(dup, $"Duplicate {go.name}");
            dup.name = string.IsNullOrEmpty(new_name) ? go.name + " (Copy)" : new_name;
            Selection.activeGameObject = dup;

            return Response.Success($"Duplicated '{go.name}' as '{dup.name}'.",
                GameObjectSerializer.DescribeCreated(dup));
        }

        [Description("Rename a GameObject.")]
        public static object RenameGameObject(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("New name")] string new_name,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var oldName = go.name;
            Undo.RecordObject(go, $"Rename {oldName} to {new_name}");
            go.name = new_name;
            return Response.Success($"Renamed '{oldName}' to '{new_name}'.",
                new { instanceId = ObjectIdCodec.GetSerializableId(go), name = go.name });
        }

        [Description("Set position, rotation, and/or scale on a GameObject's transform.")]
        public static object SetTransform(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = null,
            [ToolParam("Euler rotation as 'x,y,z'", Required = false)] string rotation = null,
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = null,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            // Validate every provided vector BEFORE touching the transform, so a malformed value
            // returns a clear INVALID_PARAM instead of silently writing (0,0,0).
            var hasPos = !string.IsNullOrEmpty(position);
            var hasRot = !string.IsNullOrEmpty(rotation);
            var hasScl = !string.IsNullOrEmpty(scale);
            Vector3 pos = default, rot = default, scl = default;
            if (hasPos && !ValueConverter.TryParseVector3(position, out pos, out var posErr))
                return Response.Error("INVALID_PARAM", new { param = "position", provided = position, expected = "Vector3 'x,y,z'", detail = posErr });
            if (hasRot && !ValueConverter.TryParseVector3(rotation, out rot, out var rotErr))
                return Response.Error("INVALID_PARAM", new { param = "rotation", provided = rotation, expected = "Vector3 'x,y,z'", detail = rotErr });
            if (hasScl && !ValueConverter.TryParseVector3(scale, out scl, out var sclErr))
                return Response.Error("INVALID_PARAM", new { param = "scale", provided = scale, expected = "Vector3 'x,y,z'", detail = sclErr });

            Undo.RecordObject(go.transform, $"Set transform of {go.name}");

            if (hasPos) go.transform.position = pos;
            if (hasRot) go.transform.eulerAngles = rot;
            if (hasScl) go.transform.localScale = scl;

            return Response.Success($"Updated transform of '{go.name}'.", new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                position = new { x = go.transform.position.x, y = go.transform.position.y, z = go.transform.position.z },
                rotation = new { x = go.transform.eulerAngles.x, y = go.transform.eulerAngles.y, z = go.transform.eulerAngles.z },
                scale = new { x = go.transform.localScale.x, y = go.transform.localScale.y, z = go.transform.localScale.z }
            });
        }

        [Description("Reparent a GameObject. Pass empty parent to unparent.")]
        public static object SetParent(
            [ToolParam("Child GameObject identifier")] string child,
            [ToolParam("Parent GameObject identifier (empty to unparent)", Required = false)] string parent = null,
            [ToolParam("How to resolve targets", Required = false)] string find_method = null)
        {
            var childGo = ObjectsHelper.FindObject(child, find_method);
            if (childGo == null)
                return Response.Error("CHILD_NOT_FOUND", new { target = child, find_method });

            if (string.IsNullOrEmpty(parent))
            {
                Undo.SetTransformParent(childGo.transform, null, $"Unparent {childGo.name}");
                return Response.Success($"Unparented '{childGo.name}'.");
            }

            var parentGo = ObjectsHelper.FindObject(parent, find_method);
            if (parentGo == null)
                return Response.Error("PARENT_NOT_FOUND", new { target = parent, find_method });

            // Cycle protection: prevent setting parent to self or own descendant
            var t = parentGo.transform;
            while (t != null)
            {
                if (t == childGo.transform)
                    return Response.Error("CYCLE_DETECTED",
                        new { reason = "Parent is a descendant of child; would create a cycle." });
                t = t.parent;
            }

            Undo.SetTransformParent(childGo.transform, parentGo.transform, $"Parent {childGo.name} to {parentGo.name}");
            return Response.Success($"Parented '{childGo.name}' to '{parentGo.name}'.",
                new { childInstanceId = ObjectIdCodec.GetSerializableId(childGo), parentInstanceId = ObjectIdCodec.GetSerializableId(parentGo) });
        }

        [Description("Add a component to a GameObject. Returns the new component's instanceId.")]
        public static object AddComponent(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Component type name (e.g. 'Rigidbody', 'BoxCollider', 'AudioSource')")] string component_type,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var type = TypeResolver.ResolveComponent(component_type);
            if (type == null)
                return TypeResolver.UnresolvedError(component_type, "COMPONENT_TYPE_NOT_FOUND", "component_type");

            var comp = Undo.AddComponent(go, type);
            if (comp == null)
                return Response.Error("ADD_COMPONENT_FAILED", new { component_type, target = go.name });

            return Response.Success($"Added {component_type} to '{go.name}'.",
                new { gameObjectInstanceId = ObjectIdCodec.GetSerializableId(go),
                      componentInstanceId = ObjectIdCodec.GetSerializableId(comp),
                      type = comp.GetType().Name });
        }

        [Description("Remove a component from a GameObject by type name. Removes the first match. Transform cannot be removed. Use index to disambiguate when multiple of the same type exist.")]
        public static object RemoveComponent(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Component type name (e.g. 'Rigidbody', 'BoxCollider')")] string component_type,
            [ToolParam("Which match to remove when several of the same type exist (0-based)", Required = false)] int index = 0,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var type = TypeResolver.ResolveComponent(component_type);
            if (type == null)
                return TypeResolver.UnresolvedError(component_type, "COMPONENT_TYPE_NOT_FOUND", "component_type");

            if (type == typeof(Transform))
                return Response.Error("CANNOT_REMOVE_TRANSFORM", new { target = go.name });

            var matches = go.GetComponents(type);
            if (matches == null || matches.Length == 0)
                return Response.Error("COMPONENT_NOT_PRESENT", new { component_type, target = go.name });
            if (index < 0 || index >= matches.Length)
                return Response.Error("INDEX_OUT_OF_RANGE", new { index, count = matches.Length });

            var comp = matches[index];
            Undo.DestroyObjectImmediate(comp);

            return Response.Success($"Removed {component_type} from '{go.name}'.",
                new { instanceId = ObjectIdCodec.GetSerializableId(go), type = type.Name, remaining = matches.Length - 1 });
        }

        [Description("Enable or disable a component (its 'enabled' checkbox) on a GameObject. Works for Behaviour/MonoBehaviour, Renderer, Collider. Use index when several of the same type exist.")]
        public static object SetComponentEnabled(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Component type name (e.g. 'BoxCollider', 'MyScript')")] string component_type,
            [ToolParam("true to enable, false to disable")] bool enabled,
            [ToolParam("Which match when several of the same type exist (0-based)", Required = false)] int index = 0,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var type = TypeResolver.ResolveComponent(component_type);
            if (type == null)
                return TypeResolver.UnresolvedError(component_type, "COMPONENT_TYPE_NOT_FOUND", "component_type");

            var matches = go.GetComponents(type);
            if (matches == null || matches.Length == 0)
                return Response.Error("COMPONENT_NOT_PRESENT", new { component_type, target = go.name });
            if (index < 0 || index >= matches.Length)
                return Response.Error("INDEX_OUT_OF_RANGE", new { index, count = matches.Length });

            var comp = matches[index];
            var prop = comp.GetType().GetProperty("enabled");
            if (prop == null || !prop.CanWrite)
                return Response.Error("COMPONENT_NOT_TOGGLEABLE", new { component_type, hint = "Component has no 'enabled' property." });

            Undo.RecordObject(comp, $"Set {component_type} enabled");
            prop.SetValue(comp, enabled);

            return Response.Success($"{(enabled ? "Enabled" : "Disabled")} {component_type} on '{go.name}'.",
                new { instanceId = ObjectIdCodec.GetSerializableId(go), type = type.Name, enabled });
        }

        [Description("Set tag and/or layer on a GameObject.")]
        public static object SetTagAndLayer(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Tag to set", Required = false)] string tag = null,
            [ToolParam("Layer name to set", Required = false)] string layer = null,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            Undo.RecordObject(go, $"Set tag/layer of {go.name}");
            var changes = new List<string>();
            var warnings = new List<string>();

            if (!string.IsNullOrEmpty(tag))
            {
                try { go.tag = tag; changes.Add($"tag={tag}"); }
                catch (UnityException) { warnings.Add($"Tag '{tag}' is not defined; use add_tag first."); }
            }
            if (!string.IsNullOrEmpty(layer))
            {
                int idx = LayerMask.NameToLayer(layer);
                if (idx >= 0) { go.layer = idx; changes.Add($"layer={layer}"); }
                else warnings.Add($"Layer '{layer}' is not defined; use add_layer first.");
            }

            return Response.Success($"Updated '{go.name}'.", new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                changes,
                warnings
            });
        }

        [Description("Activate or deactivate a GameObject.")]
        public static object SetActive(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("true to activate, false to deactivate")] string active,
            [ToolParam("How to resolve target", Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            bool isActive = active == "true" || active == "1";
            Undo.RecordObject(go, $"Set active {go.name}");
            go.SetActive(isActive);
            return Response.Success($"Set '{go.name}' active = {isActive}.",
                new { instanceId = ObjectIdCodec.GetSerializableId(go), activeSelf = go.activeSelf });
        }

        [Description("Find GameObjects by id/name/path/tag/layer/component. Returns full structured results so the agent can chain by_id calls.")]
        [ReadOnlyTool]
        public static object FindGameObjects(
            [ToolParam("Search query (id, name, path, tag name, layer name/index, or component type)")] string query,
            [ToolParam("Search method (by_id/by_name/by_path/by_tag/by_layer/by_component)", Required = false)] string find_method = null,
            [ToolParam("Include inactive objects", Required = false)] string include_inactive = null,
            [ToolParam("Limit results to children of this GameObject identifier (used with find_method=by_*)", Required = false)] string in_parent = null,
            [ToolParam("Maximum results to return (default 50)", Required = false)] string max = "50")
        {
            bool inactive = include_inactive == "true" || include_inactive == "1";
            GameObject root = null;
            if (!string.IsNullOrEmpty(in_parent))
            {
                root = ObjectsHelper.FindObject(in_parent, null, searchInactive: inactive);
                if (root == null)
                    return Response.Error("PARENT_NOT_FOUND", new { in_parent });
            }

            var matches = ObjectsHelper.FindObjects(query, find_method, findAll: true,
                searchInactive: inactive, searchInChildren: root != null, root: root);

            int.TryParse(max, out var cap);
            if (cap <= 0) cap = 50;
            var total = matches.Count;
            if (matches.Count > cap)
                matches = matches.GetRange(0, cap);

            var message = matches.Count < total
                ? $"Found {total} object(s), showing {matches.Count} (raise 'max' for more)."
                : $"Found {total} object(s).";
            return Response.Success(message, GameObjectSerializer.DescribeMany(matches));
        }

        [Description("Get full info on a GameObject: transform, components (with instance ids), active state, tag, layer.")]
        [ReadOnlyTool]
        public static object GetGameObjectInfo(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("How to resolve target", Required = false)] string find_method = null,
            [ToolParam("Include immediate children list", Required = false)] string include_children = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method, searchInactive: true);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            bool kids = include_children == "true" || include_children == "1";
            return Response.Success($"GameObject '{go.name}'.",
                GameObjectSerializer.Describe(go, includeComponents: true, includeChildren: kids));
        }

    }
}
