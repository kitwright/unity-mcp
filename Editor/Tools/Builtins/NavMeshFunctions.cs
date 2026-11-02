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
        [Description("Bake the NavMesh for the current scene using the legacy scene NavMesh settings. Objects must be flagged Navigation Static for surfaces to be included. The reply counts cover every loaded NavMesh, NavMeshSurface bakes included, not only this bake. An untitled scene is refused (SCENE_NOT_SAVED): the bake writes a NavMesh asset next to the scene.")]
        [LongRunningTool(900)]
        public static object BakeNavMesh()
        {
            if (EditorApplication.isPlaying)
                return Response.Error("BAKE_REQUIRES_EDIT_MODE",
                    new { hint = "NavMesh baking only runs in Edit Mode. Call exit_play_mode first, then retry bake_nav_mesh." });

            var unsaved = RefuseUnsavedScene(UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            if (unsaved != null) return unsaved;

#pragma warning disable CS0618 // No drop-in replacement for editor NavMesh baking
            UnityEditor.AI.NavMeshBuilder.BuildNavMesh();
#pragma warning restore CS0618
            var triangulation = NavMesh.CalculateTriangulation();
            return BakeReply(triangulation.vertices?.Length ?? 0, (triangulation.indices?.Length ?? 0) / 3);
        }

        [Description("Clear all baked NavMesh data from the current scene. Edit Mode only. Runtime NavMesh data (NavMeshSurface bakes, NavMesh.AddNavMeshData) is not cleared; the reply reports what remains.")]
        public static object ClearNavMesh()
        {
            if (EditorApplication.isPlaying)
                return Response.Error("EDIT_MODE_REQUIRED",
                    new { hint = "NavMesh data can only be cleared in Edit Mode. Call exit_play_mode first, then retry clear_nav_mesh." });

            var verticesBefore = NavMesh.CalculateTriangulation().vertices?.Length ?? 0;
#pragma warning disable CS0618 // No drop-in replacement for editor NavMesh clearing
            UnityEditor.AI.NavMeshBuilder.ClearAllNavMeshes();
#pragma warning restore CS0618
            var after = NavMesh.CalculateTriangulation();
            return ClearReply(verticesBefore, after.vertices?.Length ?? 0, (after.indices?.Length ?? 0) / 3);
        }

        [Description("Get NavMesh info: baked vertex and triangle counts. The counts cover every loaded NavMesh (all loaded scenes and NavMeshSurface bakes), not only the current scene.")]
        [ReadOnlyTool]
        public static object GetNavMeshInfo()
        {
            var triangulation = NavMesh.CalculateTriangulation();
            int vertexCount = triangulation.vertices?.Length ?? 0;
            int triangleCount = (triangulation.indices?.Length ?? 0) / 3;

            return Response.Success(
                vertexCount > 0 ? "NavMesh data is loaded." : "No NavMesh data is loaded.",
                new
                {
                    hasNavMesh = vertexCount > 0,
                    vertexCount,
                    triangleCount
                });
        }

        [Description("Add a NavMeshAgent component to a GameObject so it can path-find on a baked NavMesh. Optionally set speed, radius, and height; an omitted value is left as it is (a new agent starts at Unity's defaults).")]
        public static object AddNavMeshAgent(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Agent movement speed, 0 or more (omitted: unchanged, new agent 3.5)", Required = false)] float? speed = null,
            [ToolParam("Agent radius, greater than 0 (omitted: unchanged, new agent 0.5)", Required = false)] float? radius = null,
            [ToolParam("Agent height, greater than 0 (omitted: unchanged, new agent 2)", Required = false)] float? height = null)
        {
            var invalid = AgentSettingsError(speed, radius, height);
            if (invalid != null) return Response.Error("INVALID_VALUE", new { speed, radius, height, hint = invalid });

            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            // `== null`, not `??`: a missing component comes back from GetComponent as a stub that
            // Unity's == calls null and reference comparison does not, so ?? never added it.
            var agent = go.GetComponent<NavMeshAgent>();
            var added = agent == null;
            if (added)
                agent = Undo.AddComponent<NavMeshAgent>(go);
            else
                Undo.RecordObject(agent, "Configure NavMeshAgent");
            if (speed.HasValue) agent.speed = speed.Value;
            if (radius.HasValue) agent.radius = radius.Value;
            if (height.HasValue) agent.height = height.Value;
            EditorUtility.SetDirty(agent);

            return Response.Success(
                added ? $"Added NavMeshAgent to '{go.name}'." : $"Updated NavMeshAgent on '{go.name}'.",
                new { target = go.name, added, speed = agent.speed, radius = agent.radius, height = agent.height });
        }

        [Description("Add a NavMeshObstacle component to a GameObject so it blocks or carves the NavMesh. Optionally enable carving; omitted, a new obstacle carves and an existing one is left as it is.")]
        public static object AddNavMeshObstacle(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Carve a hole in the NavMesh instead of only blocking agents (omitted: true on a new obstacle, unchanged on an existing one)", Required = false)] bool? carving = null)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var obstacle = go.GetComponent<NavMeshObstacle>();
            var added = obstacle == null;
            if (added)
                obstacle = Undo.AddComponent<NavMeshObstacle>(go);
            else if (carving.HasValue)
                Undo.RecordObject(obstacle, "Configure NavMeshObstacle");
            if (added || carving.HasValue)
            {
                obstacle.carving = carving ?? true;
                EditorUtility.SetDirty(obstacle);
            }

            return Response.Success(
                added ? $"Added NavMeshObstacle to '{go.name}'." : $"NavMeshObstacle on '{go.name}'.",
                new { target = go.name, added, carving = obstacle.carving });
        }

        [Description("Set a NavMeshAgent's destination so it starts path-finding. Requires Play Mode, a baked NavMesh, and an enabled agent placed on it. The path is computed over the next frames: the reply reports pathPending and isStopped, not that the destination was reached.")]
        public static object SetAgentDestination(
            [ToolParam("GameObject name, hierarchy path, or instance ID (must have a NavMeshAgent)")] string target,
            [ToolParam("Destination position as 'x,y,z'")] string destination)
        {
            if (!EditorApplication.isPlaying)
                return Response.Error("PLAY_MODE_REQUIRED", new { hint = "NavMeshAgent path-finding only runs in Play Mode." });

            if (!ValueConverter.TryParseVector3(destination, out var dest, out var vectorError))
                return Response.Error("INVALID_VECTOR", new { destination, hint = vectorError });

            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            var agent = go.GetComponent<NavMeshAgent>();
            if (agent == null) return Response.Error("NO_NAVMESH_AGENT", new { target });

            var unusable = UnusableAgentHint(agent.enabled, go.activeInHierarchy);
            if (unusable != null) return Response.Error("AGENT_DISABLED", new { target }, unusable);
            if (!agent.isOnNavMesh) return Response.Error("AGENT_NOT_ON_NAVMESH", new { target });

            bool ok = agent.SetDestination(dest);
            return ok
                ? Response.Success(
                    $"Destination of agent '{go.name}' set to {dest}.",
                    DestinationData(go.name, dest, agent.pathPending, agent.isStopped),
                    agent.isStopped ? "The agent is stopped (isStopped = true) and will not move until it is resumed." : null)
                : Response.Error("SET_DESTINATION_FAILED", new { target, destination });
        }

        internal static object RefuseUnsavedScene(string scenePath) =>
            string.IsNullOrEmpty(scenePath)
                ? Response.Error("SCENE_NOT_SAVED", null,
                    "Save the scene first, for example with save_scene and a path: a NavMesh bake writes a NavMesh asset next to the scene.")
                : null;

        internal static object ClearReply(int verticesBefore, int verticesAfter, int trianglesAfter)
        {
            var cleared = verticesBefore > 0 && verticesAfter == 0;
            var data = new { cleared, remainingVertexCount = verticesAfter, remainingTriangleCount = trianglesAfter };
            if (verticesAfter > 0)
                return Response.Success(
                    $"Only the scene's own (legacy) NavMesh is cleared by this tool. {verticesAfter} vertices of runtime NavMesh data (NavMeshSurface / AddNavMeshData) are still loaded.",
                    data);
            return Response.Success(
                cleared ? "Cleared all NavMesh data from the current scene." : "There was no NavMesh data to clear.",
                data);
        }

        internal static object BakeReply(int vertexCount, int triangleCount) =>
            triangleCount > 0
                ? Response.Success("NavMesh baked.", new { vertexCount, triangleCount })
                : Response.Success("The bake finished but produced no NavMesh.", new { vertexCount, triangleCount },
                    "No Navigation Static geometry was found. Flag the ground and obstacles as Navigation Static, or bake a NavMeshSurface instead; this tool only runs the legacy scene bake.");

        internal static string AgentSettingsError(float? speed, float? radius, float? height)
        {
            if (speed < 0f) return "speed must be 0 or more.";
            if (radius <= 0f) return "radius must be greater than 0.";
            if (height <= 0f) return "height must be greater than 0.";
            return null;
        }

        internal static string UnusableAgentHint(bool enabled, bool activeInHierarchy)
        {
            if (!enabled) return "The NavMeshAgent is disabled, so it is not on the NavMesh. Enable it first.";
            if (!activeInHierarchy) return "The GameObject is inactive in the hierarchy, so its agent is not on the NavMesh. Activate it first.";
            return null;
        }

        // A raw Vector3 cannot go in the reply: serializing it recurses through Vector3.normalized.
        internal static object DestinationData(string target, Vector3 destination, bool pathPending, bool isStopped) =>
            new { target, destination = new { x = destination.x, y = destination.y, z = destination.z }, pathPending, isStopped };
    }
}
#endif
