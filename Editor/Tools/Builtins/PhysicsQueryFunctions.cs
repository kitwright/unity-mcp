// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Read-only physics query tools. All queries run against colliders already present in the
    /// open scene(s), so they work in Edit Mode (static colliders) without entering Play Mode.
    /// </summary>
    [ToolProvider("Physics")]
    internal static class PhysicsQueryFunctions
    {
        // com.unity.modules.physics is optional; without it the 3D queries disappear.
#if KITWRIGHT_PHYSICS
        [Description("Cast a 3D ray against scene colliders (Physics.RaycastAll) and return every hit ordered nearest-first. " +
                     "Works in Edit Mode against static colliders. origin/direction are 'x,y,z' strings; direction is normalized internally.")]
        [ReadOnlyTool]
        public static object PhysicsRaycast(
            [ToolParam("Ray origin as 'x,y,z'")] string origin,
            [ToolParam("Ray direction as 'x,y,z' (normalized internally)")] string direction,
            [ToolParam("Maximum ray distance", Required = false)] float max_distance = 1000f,
            [ToolParam("Layer mask bitfield (-1 = all layers)", Required = false)] int layer_mask = -1,
            [ToolParam("Maximum hits to return", Required = false)] int max_hits = 20)
        {
            if (!ValueConverter.TryParseVector3(origin, out var o, out var originError))
                return Response.Error("INVALID_PARAM", new { param = "origin", provided = origin, expected = "Vector3 'x,y,z'", detail = originError });
            if (!ValueConverter.TryParseVector3(direction, out var d, out var directionError))
                return Response.Error("INVALID_PARAM", new { param = "direction", provided = direction, expected = "Vector3 'x,y,z'", detail = directionError });

            // Vector3.normalized answers zero below 1e-5 and when the length overflows, so the guard
            // has to be on the result: a squared-length test lets those through as a ray that hits nothing.
            var unitDirection = d.normalized;
            if (unitDirection == Vector3.zero)
                return Response.Error("INVALID_PARAM", new { param = "direction", provided = direction, expected = "a Vector3 'x,y,z' that is neither zero nor vanishingly small or large" });
            if (!IsFinite(max_distance) || max_distance <= 0f)
                return Response.Error("INVALID_PARAM", new { param = "max_distance", provided = max_distance, expected = "a finite number > 0" });
            if (max_hits <= 0)
                return Response.Error("INVALID_PARAM", new { param = "max_hits", provided = max_hits, expected = "an integer > 0" });

            // In Edit Mode Unity does not auto-step physics; with autoSyncTransforms off (the
            // default) the PhysX scene can hold stale collider poses after a transform edit.
            // Sync first so queries reflect current geometry.
            Physics.SyncTransforms();
            var hits = Physics.RaycastAll(new Ray(o, unitDirection), max_distance, layer_mask);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            var list = new List<object>();
            foreach (var h in hits)
            {
                if (list.Count >= max_hits) break;
                var col = h.collider;
                var go = col != null ? col.gameObject : null;
                list.Add(new
                {
                    collider = col != null ? col.name : null,
                    path = go != null ? ObjectsHelper.GetGameObjectPath(go) : null,
                    instanceId = go != null ? ObjectIdCodec.GetSerializableId(go) : "0",
                    point = new { x = h.point.x, y = h.point.y, z = h.point.z },
                    normal = new { x = h.normal.x, y = h.normal.y, z = h.normal.z },
                    distance = h.distance
                });
            }

            return Response.Success($"Raycast hit {list.Count} collider(s) (of {hits.Length} total).",
                new { count = list.Count, totalHits = hits.Length, hits = list });
        }

        [Description("Overlap query against 3D scene colliders using Physics.OverlapSphere (shape=sphere) or Physics.OverlapBox (shape=box). " +
                     "Returns the overlapping colliders. Works in Edit Mode against static colliders. center/size are 'x,y,z' strings.")]
        [ReadOnlyTool]
        public static object PhysicsOverlap(
            [ToolParam("Center as 'x,y,z'")] string center,
            [ToolParam("Shape: 'sphere' or 'box'", Required = false)] string shape = "sphere",
            [ToolParam("Sphere radius (used when shape=sphere)", Required = false)] float radius = 1f,
            [ToolParam("Box full size as 'x,y,z' (used when shape=box)", Required = false)] string size = "1,1,1",
            [ToolParam("Layer mask bitfield (-1 = all layers)", Required = false)] int layer_mask = -1,
            [ToolParam("Maximum hits to return", Required = false)] int max_hits = 50)
        {
            if (!ValueConverter.TryParseVector3(center, out var c, out var centerError))
                return Response.Error("INVALID_PARAM", new { param = "center", provided = center, expected = "Vector3 'x,y,z'", detail = centerError });
            if (max_hits <= 0)
                return Response.Error("INVALID_PARAM", new { param = "max_hits", provided = max_hits, expected = "an integer > 0" });

            Physics.SyncTransforms(); // reflect current poses in Edit Mode (see PhysicsRaycast note)
            Collider[] cols;
            var shapeLower = (shape ?? "sphere").Trim().ToLowerInvariant();
            switch (shapeLower)
            {
                case "sphere":
                    if (!IsFinite(radius) || radius <= 0f)
                        return Response.Error("INVALID_PARAM", new { param = "radius", provided = radius, expected = "a finite number > 0" });
                    cols = Physics.OverlapSphere(c, radius, layer_mask);
                    break;
                case "box":
                    if (!ValueConverter.TryParseVector3(size, out var sz, out var sizeError))
                        return Response.Error("INVALID_PARAM", new { param = "size", provided = size, expected = "Vector3 'x,y,z'", detail = sizeError });
                    if (sz.x <= 0f || sz.y <= 0f || sz.z <= 0f)
                        return Response.Error("INVALID_PARAM", new { param = "size", provided = size, expected = "positive Vector3 full extents 'x,y,z'" });
                    // OverlapBox takes half-extents; `size` is the full box dimension.
                    cols = Physics.OverlapBox(c, sz * 0.5f, Quaternion.identity, layer_mask);
                    break;
                default:
                    return Response.Error("INVALID_PARAM", new { param = "shape", provided = shape, expected = "'sphere' or 'box'" });
            }

            var list = new List<object>();
            foreach (var col in cols)
            {
                if (list.Count >= max_hits) break;
                if (col == null) continue;
                var go = col.gameObject;
                list.Add(new
                {
                    collider = col.name,
                    path = ObjectsHelper.GetGameObjectPath(go),
                    instanceId = ObjectIdCodec.GetSerializableId(go)
                });
            }

            return Response.Success($"Overlap ({shapeLower}) matched {list.Count} collider(s) (of {cols.Length} total).",
                new { count = list.Count, totalHits = cols.Length, shape = shapeLower, hits = list });
        }
#endif

        // com.unity.modules.physics2d is optional; without it the 2D query disappears.
#if KITWRIGHT_PHYSICS2D
        [Description("2D overlap-point query (Physics2D.OverlapPointAll) for the 2D merge board. " +
                     "Returns every 2D collider overlapping the point. Works in Edit Mode. point is an 'x,y' string.")]
        [ReadOnlyTool]
        public static object Physics2DOverlapPoint(
            [ToolParam("Point as 'x,y'")] string point,
            [ToolParam("Layer mask bitfield (-1 = all layers)", Required = false)] int layer_mask = -1)
        {
            if (!ValueConverter.TryParseVector2(point, out var p, out var pointError))
                return Response.Error("INVALID_PARAM", new { param = "point", provided = point, expected = "Vector2 'x,y'", detail = pointError });

            Physics2D.SyncTransforms(); // reflect current poses in Edit Mode (see PhysicsRaycast note)
            var cols = Physics2D.OverlapPointAll(p, layer_mask);
            var list = new List<object>();
            foreach (var col in cols)
            {
                if (col == null) continue;
                var go = col.gameObject;
                list.Add(new
                {
                    collider = col.name,
                    path = ObjectsHelper.GetGameObjectPath(go),
                    instanceId = ObjectIdCodec.GetSerializableId(go)
                });
            }

            return Response.Success($"Physics2D overlap-point matched {list.Count} collider(s).",
                new { count = list.Count, hits = list });
        }
#endif

        // -------- Helpers --------

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
