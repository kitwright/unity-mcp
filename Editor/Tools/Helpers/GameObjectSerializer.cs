// Copyright (C) KitWright. All rights reserved.

using System.Collections.Generic;
using UnityEngine;

namespace KitWright.Editor.Tools.Helpers
{
    /// <summary>
    /// Build structured payloads describing GameObjects and Components.
    /// The shape is fixed (anonymous types serialized through Newtonsoft) so MCP
    /// clients always see the same field names — particularly <c>instanceId</c>,
    /// which lets agents chain <c>by_id</c> lookups instead of re-resolving by name.
    /// </summary>
    public static class GameObjectSerializer
    {
        public static object Describe(GameObject go, bool includeComponents = true, bool includeChildren = false)
        {
            if (go == null) return null;
            return new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                name = go.name,
                path = ObjectsHelper.GetGameObjectPath(go),
                activeSelf = go.activeSelf,
                activeInHierarchy = go.activeInHierarchy,
                tag = go.tag,
                layer = LayerMask.LayerToName(go.layer),
                isStatic = go.isStatic,
                scene = go.scene.IsValid() ? go.scene.name : null,
                transform = new
                {
                    position = ToObj(go.transform.position),
                    localPosition = ToObj(go.transform.localPosition),
                    eulerAngles = ToObj(go.transform.eulerAngles),
                    localScale = ToObj(go.transform.localScale)
                },
                components = includeComponents ? DescribeComponents(go) : null,
                childCount = go.transform.childCount,
                children = includeChildren ? DescribeChildren(go) : null
            };
        }

        public static object DescribeCreated(GameObject go, bool includeComponents = false)
        {
            if (go == null) return null;
            return new
            {
                instanceId = ObjectIdCodec.GetSerializableId(go),
                name = go.name,
                path = ObjectsHelper.GetGameObjectPath(go),
                components = includeComponents ? DescribeComponents(go, brief: true) : null
            };
        }

        public static List<object> DescribeMany(IEnumerable<GameObject> gos)
        {
            var list = new List<object>();
            foreach (var go in gos)
                list.Add(Describe(go, includeComponents: false));
            return list;
        }

        private static List<object> DescribeComponents(GameObject go, bool brief = false)
        {
            var list = new List<object>();
            foreach (var c in go.GetComponents<Component>())
            {
                if (c == null)
                {
                    list.Add(new { type = "<missing>", instanceId = 0 });
                    continue;
                }
                var id = ObjectIdCodec.GetSerializableId(c);
                list.Add(brief
                    ? (object)new { instanceId = id, type = c.GetType().Name }
                    : new { instanceId = id, type = c.GetType().Name, fullType = c.GetType().FullName });
            }
            return list;
        }

        private static List<object> DescribeChildren(GameObject go)
        {
            var list = new List<object>();
            for (int i = 0; i < go.transform.childCount; i++)
            {
                var child = go.transform.GetChild(i).gameObject;
                list.Add(new
                {
                    instanceId = ObjectIdCodec.GetSerializableId(child),
                    name = child.name,
                    activeSelf = child.activeSelf,
                    childCount = child.transform.childCount
                });
            }
            return list;
        }

        private static object ToObj(Vector3 v) => new { x = v.x, y = v.y, z = v.z };
    }
}
