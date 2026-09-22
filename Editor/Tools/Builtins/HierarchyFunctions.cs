// Copyright (C) KitWright. All rights reserved.
using System;
using System.Text;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Hierarchy")]
    internal static class HierarchyFunctions
    {
        [Description("Browse the scene hierarchy tree. Returns a tree-like view of GameObjects " +
                     "with their instance IDs, components, active state, and tags. " +
                     "Each object is printed as 'Name #instanceId', and that id can be fed straight back " +
                     "into root_name or any other tool that takes a target, so browsing the tree is enough " +
                     "to address an object without a follow-up lookup. " +
                     "Use root_name to start from a specific object, or leave empty for full scene. " +
                     "A tree cut short by max_nodes reports a next_cursor; pass it back as cursor to " +
                     "read the next page instead of re-reading the part already seen.")]
        [ReadOnlyTool]
        public static string GetHierarchy(
            [ToolParam("Root object name, hierarchy path, or instance ID to start from (empty = entire scene). Finds inactive objects too.", Required = false)] string root_name = "",
            [ToolParam("Maximum depth to traverse (1-10)", Required = false)] int depth = 3,
            [ToolParam("Include component names on each object", Required = false)] bool include_components = true,
            [ToolParam("Include inactive objects", Required = false)] bool include_inactive = true,
            [ToolParam("Print each object's instance ID as '#id'. Turn off to browse pure scene shape more cheaply.", Required = false)] bool include_ids = true,
            [ToolParam("Stop after this many objects (1-5000). The response says when it truncated.", Required = false)] int max_nodes = 500,
            [ToolParam("Resume at this node index, as reported by a previous call's next_cursor. 0 starts at the top.", Required = false)] int cursor = 0)
        {
            try
            {
                depth = Mathf.Clamp(depth, 1, 10);
                max_nodes = Mathf.Clamp(max_nodes, 1, 5000);
                cursor = Mathf.Max(cursor, 0);
                var walk = new Walk { Budget = max_nodes, Cursor = cursor };
                var sb = new StringBuilder();

                if (!string.IsNullOrEmpty(root_name))
                {
                    // ObjectsHelper already searches every loaded scene (additively loaded ones
                    // included) plus the open prefab stage, and finds inactive objects.
                    var root = ObjectsHelper.FindTarget(root_name);
                    if (root == null)
                        return ObjectsHelper.NotFoundText("root_name", root_name);

                    PrintNode(sb, root.transform, 0, depth, include_components, include_inactive, include_ids, walk);
                }
                else
                {
                    // Through the helper so play mode's DontDestroyOnLoad scene comes with it.
                    var activeScene = SceneManager.GetActiveScene();
                    foreach (var scene in ObjectsHelper.EnumerateLoadedScenes())
                    {
                        // Held back rather than written: a page that starts inside a later scene
                        // would otherwise be preceded by the headers of scenes it skipped past.
                        walk.PendingHeader = scene == activeScene ? $"Scene: {scene.name}" : $"Scene: {scene.name} (additive)";
                        foreach (var root in scene.GetRootGameObjects())
                        {
                            if (!include_inactive && !root.activeSelf) continue;
                            PrintNode(sb, root.transform, 0, depth, include_components, include_inactive, include_ids, walk);
                        }
                    }
                }

                if (walk.Truncated)
                    sb.AppendLine($"... truncated at max_nodes={max_nodes}. next_cursor={cursor + walk.Printed}");
                else if (walk.Printed == 0 && cursor > 0)
                    sb.AppendLine($"cursor={cursor} is past the end; the walk holds {walk.Visited} objects.");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return ToolResultFormatter.Exception(ex);
            }
        }

        // --- Helpers ---

        // Depth-first order is stable across calls, so a plain visit index is enough to resume:
        // page two fast-forwards over the same nodes page one printed and picks up after them.
        private sealed class Walk
        {
            public int Budget;
            public int Cursor;
            public int Visited;
            public int Printed;
            public string PendingHeader;
            public bool Truncated;
        }

        private static void PrintNode(StringBuilder sb, Transform t, int indent, int maxDepth,
            bool includeComponents, bool includeInactive, bool includeIds, Walk walk)
        {
            if (!includeInactive && !t.gameObject.activeSelf) return;
            if (walk.Truncated) return;

            var index = walk.Visited++;
            if (index < walk.Cursor)
            {
                // Descend under the same depth rule as the printing pass, or the indices would
                // not line up with the ones the previous page counted.
                if (indent < maxDepth)
                {
                    for (int i = 0; i < t.childCount; i++)
                        PrintNode(sb, t.GetChild(i), indent + 1, maxDepth, includeComponents, includeInactive, includeIds, walk);
                }
                return;
            }

            if (walk.Budget <= 0) { walk.Truncated = true; return; }
            walk.Budget--;

            if (walk.PendingHeader != null)
            {
                sb.AppendLine(walk.PendingHeader);
                walk.PendingHeader = null;
            }

            // A page that opens in the middle of a subtree is indented under a parent the reader
            // cannot see, so name it once rather than leave the indentation dangling.
            if (walk.Printed == 0 && walk.Cursor > 0 && indent > 0 && t.parent != null)
                sb.AppendLine($"(resuming under {ObjectsHelper.GetGameObjectPath(t.parent.gameObject)})");

            walk.Printed++;

            string prefix = indent > 0 ? new string(' ', indent * 2) + "|- " : "";
            string id = includeIds ? $" #{ObjectIdCodec.GetSerializableId(t.gameObject)}" : "";
            string active = t.gameObject.activeSelf ? "" : " [INACTIVE]";
            string tag = t.tag != "Untagged" ? $" tag={t.tag}" : "";

            if (includeComponents)
            {
                string comps = GetComponentSummary(t.gameObject);
                sb.AppendLine($"{prefix}{t.name}{id}{active}{tag} [{comps}]");
            }
            else
            {
                sb.AppendLine($"{prefix}{t.name}{id}{active}{tag}");
            }

            if (indent < maxDepth)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    PrintNode(sb, t.GetChild(i), indent + 1, maxDepth, includeComponents, includeInactive, includeIds, walk);
                }
            }
            else if (t.childCount > 0)
            {
                string childPrefix = new string(' ', (indent + 1) * 2) + "|- ";
                sb.AppendLine($"{childPrefix}... ({t.childCount} children)");
            }
        }

        private static string GetComponentSummary(GameObject go)
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                string name = comp.GetType().Name;
                if (name == "Transform" || name == "RectTransform") continue; // Always present, skip
                names.Add(name);
            }
            return names.Count > 0 ? string.Join(", ", names) : "-";
        }
    }
}
