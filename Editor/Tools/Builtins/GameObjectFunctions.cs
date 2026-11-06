// Copyright (C) KitWright. Licensed under MIT.
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("GameObject")]
    internal static class GameObjectFunctions
    {
        [Description("Create a GameObject in the scene: empty, or a primitive (Cube, Sphere, Capsule, Cylinder, Plane, Quad) with its mesh and collider. " +
                     "Position and rotation are world space, scale is local; an omitted one is left as Unity creates it, except that a primitive " +
                     "under a parent defaults to local scale 1. " +
                     "Returns the new instanceId so it can be targeted by_id in follow-up calls, plus the components of a primitive.")]
        public static object CreateGameObject(
            [ToolParam("Name of the new GameObject")] string name,
            [ToolParam("Parent GameObject identifier (instance id, name, or path)", Required = false)] string parent = null,
            [ToolParam("How to resolve parent: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null,
            [ToolParam("Primitive type: Cube, Sphere, Capsule, Cylinder, Plane, Quad. Empty = an empty GameObject.", Required = false)] string primitive = null,
            [ToolParam("Position as 'x,y,z'", Required = false)] string position = null,
            [ToolParam("Euler rotation as 'x,y,z'", Required = false)] string rotation = null,
            [ToolParam("Scale as 'x,y,z'", Required = false)] string scale = null)
        {
            ObjectsHelper.ValidateFindMethod(find_method);

            var isPrimitive = !string.IsNullOrWhiteSpace(primitive);
            var type = PrimitiveType.Cube;
            if (isPrimitive)
            {
                switch (primitive.Trim().ToLowerInvariant())
                {
                    case "cube": type = PrimitiveType.Cube; break;
                    case "sphere": type = PrimitiveType.Sphere; break;
                    case "capsule": type = PrimitiveType.Capsule; break;
                    case "cylinder": type = PrimitiveType.Cylinder; break;
                    case "plane": type = PrimitiveType.Plane; break;
                    case "quad": type = PrimitiveType.Quad; break;
                    default: return Response.Error("UNKNOWN_PRIMITIVE", new { primitive });
                }
            }

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

            // Resolved before the object exists, so a bad parent cannot leave one behind. Creating
            // first meant PARENT_NOT_FOUND came back with an orphan sitting at the scene root.
            GameObject parentGo = null;
            if (!string.IsNullOrEmpty(parent))
            {
                parentGo = ObjectsHelper.FindObject(parent, find_method);
                if (parentGo == null)
                    return Response.Error("PARENT_NOT_FOUND", new { parent, find_method });
            }

            GameObject go;
            if (isPrimitive)
            {
                go = GameObject.CreatePrimitive(type);
                go.name = name;
            }
            else
                go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, $"Create {name}");

            if (parentGo != null)
                Undo.SetTransformParent(go.transform, parentGo.transform, $"Set parent of {name}");
            if (hasPos) go.transform.position = pos;
            if (hasRot) go.transform.eulerAngles = rot;
            if (hasScl) go.transform.localScale = scl;
            // create_primitive's behaviour: a primitive is unit-sized in its parent's space, not in world space.
            else if (isPrimitive) go.transform.localScale = Vector3.one;

            Selection.activeGameObject = go;
            return isPrimitive
                ? Response.Success($"Created {type} '{name}'.", GameObjectSerializer.DescribeCreated(go, includeComponents: true))
                : Response.Success($"Created GameObject '{name}'.", GameObjectSerializer.DescribeCreated(go));
        }

        [Description("Delete a GameObject. Targets resolved through ObjectsHelper (instance id, name, path, tag, layer, component).")]
        public static object DeleteGameObject(
            [ToolParam("Identifier of the GameObject to delete")] string target,
            [ToolParam("How to resolve the target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
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
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var dup = Object.Instantiate(go, go.transform.parent, false);
            Undo.RegisterCreatedObjectUndo(dup, $"Duplicate {go.name}");
            dup.transform.SetSiblingIndex(go.transform.GetSiblingIndex() + 1);
            dup.name = string.IsNullOrEmpty(new_name) ? go.name + " (Copy)" : new_name;
            Selection.activeGameObject = dup;

            return Response.Success($"Duplicated '{go.name}' as '{dup.name}'.",
                GameObjectSerializer.DescribeCreated(dup));
        }

        [Description("Rename a GameObject.")]
        public static object RenameGameObject(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("New name")] string new_name,
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
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
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
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
            [ToolParam("How to resolve child and parent: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
        {
            var childGo = ObjectsHelper.FindObject(child, find_method);
            if (childGo == null)
                return Response.Error("CHILD_NOT_FOUND", new { target = child, find_method });

            if (IsLockedPrefabInstanceChild(childGo))
                return Response.Error("PREFAB_INSTANCE_CHILD",
                    new { target = child, prefabInstanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(childGo).name },
                    "Unity does not let a non-root object of a prefab instance change parent. Unpack the instance first" +
                    (MCPToolExportPolicy.IsToolExposed("unpack_prefab") ? " (unpack_prefab)" : string.Empty) +
                    ", or edit the prefab asset instead.");

            if (string.IsNullOrEmpty(parent))
            {
                if (childGo.transform.parent == null)
                    return Response.Success($"'{childGo.name}' is already a root object; nothing changed.");

                Undo.SetTransformParent(childGo.transform, null, $"Unparent {childGo.name}");
                if (childGo.transform.parent != null)
                    return ReparentFailed(childGo, null);
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
            if (childGo.transform.parent != parentGo.transform)
                return ReparentFailed(childGo, parentGo);
            return Response.Success($"Parented '{childGo.name}' to '{parentGo.name}'.",
                new { childInstanceId = ObjectIdCodec.GetSerializableId(childGo), parentInstanceId = ObjectIdCodec.GetSerializableId(parentGo) });
        }

        private static bool IsLockedPrefabInstanceChild(GameObject go) =>
            PrefabUtility.IsPartOfPrefabInstance(go)
            && !PrefabUtility.IsOutermostPrefabInstanceRoot(go)
            && !PrefabUtility.IsAddedGameObjectOverride(go);

        private static object ReparentFailed(GameObject child, GameObject parent) =>
            Response.Error("REPARENT_FAILED",
                new { child = child.name, requestedParent = parent != null ? parent.name : null, currentParent = child.transform.parent != null ? child.transform.parent.name : null },
                "Unity refused the change and left the hierarchy as it was; see the console for the reason.");

        [Description("Add a component to a GameObject. Returns the new component's instanceId. component_type is a type name, or a " +
                     "MonoBehaviour's script asset path ('Assets/Scripts/Player.cs'), whose class is read from the MonoScript so it may " +
                     "be named differently from the file. A script that has not compiled yet answers SCRIPT_NOT_COMPILED: request_recompile, then retry.")]
        public static object AddComponent(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Component type name (e.g. 'Rigidbody', 'BoxCollider', 'AudioSource') or script asset path ending in .cs")] string component_type,
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            System.Type type;
            if (component_type != null && component_type.EndsWith(".cs", System.StringComparison.OrdinalIgnoreCase))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(component_type);
                if (script == null)
                    return Response.Error("SCRIPT_NOT_FOUND", new { script_path = component_type });

                type = script.GetClass();
                if (type == null)
                    return Response.Error("SCRIPT_NOT_COMPILED", new { script_path = component_type },
                        "The file has no compiled class yet, or its class name differs from what Unity expects. Run request_recompile and retry.");

                if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract)
                    return Response.Error("NOT_A_MONOBEHAVIOUR", new { script_path = component_type, type = type.FullName },
                        "Only a concrete MonoBehaviour can be attached to a GameObject.");
            }
            else
            {
                type = TypeResolver.ResolveComponent(component_type);
                if (type == null)
                    return TypeResolver.UnresolvedError(component_type, "COMPONENT_TYPE_NOT_FOUND", "component_type");
            }

            var comp = Undo.AddComponent(go, type);
            if (comp == null)
                return Response.Error("ADD_COMPONENT_FAILED", new { component_type, target = go.name },
                    "Unity refused the component: it may conflict with one already on the GameObject (a [DisallowMultipleComponent] or [RequireComponent] rule).");

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
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
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

        [Description("Set tag and/or layer on a GameObject.")]
        public static object SetTagAndLayer(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Tag to set", Required = false)] string tag = null,
            [ToolParam("Layer name to set", Required = false)] string layer = null,
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            tag = tag?.Trim();
            layer = layer?.Trim();
            var hasTag = !string.IsNullOrEmpty(tag);
            var hasLayer = !string.IsNullOrEmpty(layer);
            if (!hasTag && !hasLayer)
                return Response.Error("NO_PARAMS", new { hint = "Provide tag and/or layer." });

            var changes = new List<string>();
            var warnings = new List<string>();

            // Checked against the defined tags rather than caught: assigning an undefined tag
            // raises UnityException on some versions and simply takes the string on others (6000.3
            // does), so the catch reported nothing while the tag silently did not stick. The layer
            // half has always pre-checked, and this is the same warning in the same shape.
            var tagDefined = hasTag && System.Array.IndexOf(UnityEditorInternal.InternalEditorUtility.tags, tag) >= 0;
            var layerIndex = hasLayer ? LayerMask.NameToLayer(layer) : -1;
            if (hasTag && !tagDefined)
                warnings.Add($"Tag '{tag}' is not defined; add it with set_tags_layers first.");
            if (hasLayer && layerIndex < 0)
                warnings.Add($"Layer '{layer}' is not defined; add it with set_tags_layers first.");

            // A call that applied nothing is not a success; one that applied one half and could not
            // apply the other still is, with the warning saying which.
            if (!tagDefined && layerIndex < 0)
                return Response.Error("INVALID_PARAM", new { target = go.name, warnings },
                    "Nothing was applied: neither the tag nor the layer exists.");

            Undo.RecordObject(go, $"Set tag/layer of {go.name}");
            if (tagDefined)
            {
                go.tag = tag;
                changes.Add($"tag={tag}");
            }
            if (layerIndex >= 0)
            {
                go.layer = layerIndex;
                changes.Add($"layer={layer}");
            }

            return Response.Success($"Updated '{go.name}'.", new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                changes,
                warnings
            });
        }

        [Description("Activate or deactivate a GameObject.")]
        // `active` is a bool rather than a string parsed here: the hand-rolled `active == "true"` read
        // anything else - "True", "yes", a typo - as false, deactivated the object and reported
        // success. The invoker's own conversion takes true/false/1/0/yes/no and answers INVALID_PARAM
        // for the rest, which is the difference between a wrong write and a refusal.
        public static object SetActive(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("true to activate, false to deactivate")] bool active,
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            Undo.RecordObject(go, $"Set active {go.name}");
            go.SetActive(active);
            return Response.Success($"Set '{go.name}' active = {active}.",
                new { instanceId = ObjectIdCodec.GetSerializableId(go), activeSelf = go.activeSelf });
        }

        [Description("Find GameObjects by id/name/path/tag/layer/component. Returns full structured results so the agent can chain by_id calls. " +
                     "A page cut short by max reports a next_cursor; pass it back as cursor for the rest instead of raising max and re-reading page one.")]
        [ReadOnlyTool]
        public static object FindGameObjects(
            [ToolParam("Search query (id, name, path, tag name, layer name/index, or component type)")] string query,
            [ToolParam("Search method: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null,
            [ToolParam("Include inactive objects", Required = false)] string include_inactive = null,
            [ToolParam("Limit results to children of this GameObject identifier (used with find_method=by_*)", Required = false)] string in_parent = null,
            [ToolParam("Maximum results to return (default 50)", Required = false)] string max = "50",
            [ToolParam(Paging.CursorParam, Required = false)] int cursor = 0)
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
            var page = Paging.Page(matches, cursor, cap);
            var nextCursor = Paging.Next(cursor, page.Count, total);

            var message = $"Found {total} object(s).{Paging.Suffix(cursor, page.Count, total)}";
            if (total == 0 && !inactive)
            {
                // "Found 0" read as "no such object" while 39 inactive ones matched.
                var hidden = ObjectsHelper.FindObjects(query, find_method, findAll: true,
                    searchInactive: true, searchInChildren: root != null, root: root).Count;
                if (hidden > 0)
                    message += $" {hidden} inactive match(es) hidden; include_inactive=true shows them.";
            }
            // data stays a bare array, so the cursor rides in hint rather than reshaping it.
            return Response.Success(message, GameObjectSerializer.DescribeMany(page),
                nextCursor > 0 ? $"next_cursor={nextCursor}" : null);
        }

        [Description("Get full info on a GameObject: transform, components (with instance ids), active state, tag, layer.")]
        [ReadOnlyTool]
        public static object GetGameObjectInfo(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null,
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
