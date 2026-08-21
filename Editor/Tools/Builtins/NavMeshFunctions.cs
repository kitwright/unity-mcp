// Copyright (C) KitWright. Licensed under MIT.

// com.unity.modules.ai is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_AI
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("NavMesh")]
    internal static class NavMeshFunctions
    {
        [Description("Bake the NavMesh for the current scene using the legacy scene NavMesh settings. Objects must be flagged Navigation Static for surfaces to be included.")]
        [LongRunningTool(900)]
        public static object BakeNavMesh()
        {
            if (EditorApplication.isPlaying)
                return Response.Error("BAKE_REQUIRES_EDIT_MODE",
                    new { hint = "NavMesh baking only runs in Edit Mode. Call exit_play_mode first, then retry bake_nav_mesh." });

#pragma warning disable CS0618 // No drop-in replacement for editor NavMesh baking
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
#pragma warning restore CS0618
            var triangulation = NavMesh.CalculateTriangulation();
            return Response.Success("NavMesh baked for the current scene.", new
            {
                vertexCount = triangulation.vertices?.Length ?? 0,
                triangleCount = (triangulation.indices?.Length ?? 0) / 3
            });
        }

        [Description("Clear all baked NavMesh data from the current scene.")]
        public static object ClearNavMesh()
        {
#pragma warning disable CS0618 // No drop-in replacement for editor NavMesh clearing
            UnityEditor.AI.NavMeshBuilder.ClearAllNavMeshes();
#pragma warning restore CS0618
            return Response.Success("Cleared all NavMesh data from the current scene.");
        }

        [Description("Get NavMesh info for the current scene: baked vertex and triangle counts, and per-area default costs.")]
        [ReadOnlyTool]
        public static object GetNavMeshInfo()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            int vertexCount = triangulation.vertices?.Length ?? 0;
            int triangleCount = (triangulation.indices?.Length ?? 0) / 3;

            return Response.Success(
                vertexCount > 0 ? "NavMesh present in the current scene." : "No baked NavMesh found in the current scene.",
                new
                {
                    hasNavMesh = vertexCount > 0,
                    vertexCount,
                    triangleCount
                });
        }

        [Description("Add a NavMeshAgent component to a GameObject so it can path-find on a baked NavMesh. Optionally set speed, radius, and height.")]
        public static object AddNavMeshAgent(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Agent movement speed", Required = false)] float speed = 3.5f,
            [ToolParam("Agent radius", Required = false)] float radius = 0.5f,
            [ToolParam("Agent height", Required = false)] float height = 2f)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var agent = go.GetComponent<NavMeshAgent>() ?? Undo.AddComponent<NavMeshAgent>(go);
            agent.speed = speed;
            agent.radius = radius;
            agent.height = height;
            EditorUtility.SetDirty(agent);

            return Response.Success($"NavMeshAgent on '{go.name}'.", new { target = go.name, speed, radius, height });
        }

        [Description("Add a NavMeshObstacle component to a GameObject so it blocks or carves the NavMesh. Optionally enable carving.")]
        public static object AddNavMeshObstacle(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Carve a hole in the NavMesh instead of only blocking agents", Required = false)] bool carving = true)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var obstacle = go.GetComponent<NavMeshObstacle>() ?? Undo.AddComponent<NavMeshObstacle>(go);
            obstacle.carving = carving;
            EditorUtility.SetDirty(obstacle);

            return Response.Success($"NavMeshObstacle on '{go.name}'.", new { target = go.name, carving });
        }

        [Description("Set a NavMeshAgent's destination so it starts path-finding. Requires Play Mode, a baked NavMesh, and an agent placed on it.")]
        public static object SetAgentDestination(
            [ToolParam("GameObject name, hierarchy path, or instance ID (must have a NavMeshAgent)")] string target,
            [ToolParam("Destination position as 'x,y,z'")] string destination)
        {
            if (!EditorApplication.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { hint = "NavMeshAgent path-finding only runs in Play Mode." });

            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null) return Response.Error("NO_NAVMESH_AGENT", new { target });
            if (!agent.isOnNavMesh) return Response.Error("AGENT_NOT_ON_NAVMESH", new { target });

            if (!ValueConverter.TryParseVector3(destination, out var dest, out _))
                return Response.Error("INVALID_VECTOR", new { destination });

            bool ok = agent.SetDestination(dest);
            return ok
                ? Response.Success($"Agent '{go.name}' heading to {dest}.", new { target = go.name, destination = dest })
                : Response.Error("SET_DESTINATION_FAILED", new { target, destination });
        }
    }
}
#endif
