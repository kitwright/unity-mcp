// Copyright (C) KitWright. All rights reserved.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stopwatch = System.Diagnostics.Stopwatch;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("References")]
    internal static class ReferenceFunctions
    {
        [Description("Find asset references in both directions. 'depends_on' = the target's direct forward " +
                     "dependencies (AssetDatabase.GetDependencies(path, recursive:false)). 'referenced_by' = assets that " +
                     "depend on the target, answered from Unity's Search dependency index when available (milliseconds, " +
                     "complete) and otherwise by a bounded reverse scan of 'Assets/' paths (the response reports the " +
                     "method used and whether the scan was truncated). Set include_referenced_by=false for a fast " +
                     "forward-only query.")]
        [ReadOnlyTool]
        public static async Task<object> FindReferences(
            [ToolParam("Project-relative asset path (e.g. 'Assets/Art/hero.prefab')")] string path,
            [ToolParam("Maximum entries to collect per direction (default 200)", Required = false)] int max_results = 200,
            [ToolParam("Whether to scan the project for assets that reference this target. Set false for a fast forward-dependency-only query.", Required = false)] bool include_referenced_by = true,
            [ToolParam("Whether to also scan currently loaded scenes for GameObjects/components that reference the target asset (finds scene usages the asset index cannot see).", Required = false)] bool include_scene = true,
            [ToolParam("Maximum asset files to inspect during the reverse scan (default 10000, max 50000).", Required = false)] int max_assets_scanned = 10000,
            [ToolParam("Maximum wall-clock seconds for the reverse scan (default 10, range 1-60).", Required = false)] int max_scan_seconds = 10,
            [ToolParam("Skip Unity's Search dependency index and always use the bounded manual scan (honors max_assets_scanned/max_scan_seconds). Default false.", Required = false)] bool prefer_manual_scan = false)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                    return Response.Error("PATH_REQUIRED", new { hint = "Pass a project-relative asset path under 'Assets/'." });

                if (max_results < 1) max_results = 200;
                max_assets_scanned = Mathf.Clamp(max_assets_scanned, 1, 50000);
                max_scan_seconds = Mathf.Clamp(max_scan_seconds, 1, 60);

                var target = path.Replace('\\', '/');

                // Guard: the target must actually exist as an asset (file or folder).
                var mainType = AssetDatabase.GetMainAssetTypeAtPath(target);
                bool isFolder = AssetDatabase.IsValidFolder(target);
                if (mainType == null && !isFolder)
                    return Response.Error("ASSET_NOT_FOUND",
                        new { path = target, hint = "No asset exists at this project-relative path. Use forward slashes and an 'Assets/' prefix." });

                // Forward: direct dependencies (exclude the asset itself, which GetDependencies always includes).
                var directDeps = AssetDatabase.GetDependencies(target, false);
                var dependsOn = new List<string>();
                bool dependsOnTruncated = false;
                foreach (var d in directDeps)
                {
                    if (d == target) continue;
                    if (dependsOn.Count >= max_results) { dependsOnTruncated = true; break; }
                    dependsOn.Add(d);
                }

                // Reverse: prefer Unity's Search dependency index (fast + complete); fall back
                // to a bounded manual scan when the index is unavailable or empty.
                var referencedBy = new List<string>();
                int scanned = 0;
                bool referencedByTruncated = false;
                string reverseScanStopReason = null;
                string reverseScanMethod = null;
                var stopwatch = Stopwatch.StartNew();
                if (include_referenced_by && !prefer_manual_scan &&
                    TrySearchIndexReferencedBy(target, max_results, referencedBy, ref referencedByTruncated))
                {
                    reverseScanMethod = "search_index";
                }
                else if (include_referenced_by)
                {
                    reverseScanMethod = "manual_scan";
                    var allPaths = AssetDatabase.GetAllAssetPaths();
                    foreach (var p in allPaths)
                    {
                        if (string.IsNullOrEmpty(p) || !p.StartsWith("Assets/")) continue;
                        if (p == target) continue;
                        if (AssetDatabase.IsValidFolder(p)) continue;

                        if (scanned >= max_assets_scanned)
                        {
                            referencedByTruncated = true;
                            reverseScanStopReason = "asset_limit";
                            break;
                        }

                        if (stopwatch.Elapsed.TotalSeconds >= max_scan_seconds)
                        {
                            referencedByTruncated = true;
                            reverseScanStopReason = "time_limit";
                            break;
                        }

                        scanned++;
                        var deps = AssetDatabase.GetDependencies(p, false);
                        if (Array.IndexOf(deps, target) >= 0)
                        {
                            referencedBy.Add(p);
                            if (referencedBy.Count >= max_results)
                            {
                                referencedByTruncated = true;
                                reverseScanStopReason = "result_limit";
                                break;
                            }
                        }

                        // Keep the Editor responsive and let broker traffic/UI updates run during
                        // large reverse scans instead of monopolizing the main thread.
                        if (scanned % 64 == 0)
                            await Task.Yield();
                    }
                }

                // Scene usages: the asset index only covers files on disk, so a GameObject in a
                // loaded (possibly unsaved) scene that references the target is otherwise invisible.
                var referencedByScene = new List<object>();
                bool referencedBySceneTruncated = false;
                var targetAsset = include_scene ? AssetDatabase.LoadMainAssetAtPath(target) : null;
                if (include_scene && targetAsset != null)
                    ScanLoadedScenesForAssetRefs(targetAsset, referencedByScene, max_results, ref referencedBySceneTruncated);

                return Response.Success(
                    $"'{target}': {dependsOn.Count} direct dependency(ies), {referencedBy.Count} referencing asset(s) (scanned {scanned}), {referencedByScene.Count} scene usage(s).",
                    new
                    {
                        target,
                        depends_on = dependsOn,
                        referenced_by = referencedBy,
                        referenced_by_scene = referencedByScene,
                        reverse_scan_requested = include_referenced_by,
                        reverse_scan_method = reverseScanMethod,
                        scene_scan_requested = include_scene,
                        scanned,
                        truncated = dependsOnTruncated || referencedByTruncated || referencedBySceneTruncated,
                        depends_on_truncated = dependsOnTruncated,
                        referenced_by_truncated = referencedByTruncated,
                        referenced_by_scene_truncated = referencedBySceneTruncated,
                        reverse_scan_stop_reason = reverseScanStopReason,
                        elapsed_ms = stopwatch.ElapsedMilliseconds
                    });
            }
            catch (Exception ex)
            {
                return Response.Error("TOOL_EXCEPTION", new { message = ex.Message });
            }
        }

        [Description("Scan for broken references: (a) missing MonoBehaviour scripts (a null Component entry) and " +
                     "(b) dangling Object references (a serialized ObjectReference whose instanceID is non-zero but whose " +
                     "resolved value is null). scope='scene' walks every root GameObject of all loaded scenes recursively; " +
                     "scope='assets' requires 'path' and loads that prefab/asset (a prefab's whole hierarchy is walked, " +
                     "any other UnityEngine.Object is scanned directly). Findings are capped at max_results " +
                     "(truncated=true when the cap is hit).")]
        [ReadOnlyTool]
        public static object FindBrokenReferences(
            [ToolParam("Scan scope: 'assets' (a single prefab/asset via 'path') or 'scene' (all loaded scenes)", Required = false)] string scope = "assets",
            [ToolParam("Asset path to scan (required when scope='assets')", Required = false)] string path = null,
            [ToolParam("Maximum findings to collect (default 200)", Required = false)] int max_results = 200)
        {
            try
            {
                if (max_results < 1) max_results = 200;
                var normalizedScope = (scope ?? "assets").Trim().ToLowerInvariant();

                var findings = new List<object>();
                bool truncated = false;

                if (normalizedScope == "scene")
                {
                    int scenesScanned = 0;
                    for (int i = 0; i < SceneManager.sceneCount; i++)
                    {
                        var s = SceneManager.GetSceneAt(i);
                        if (!s.IsValid() || !s.isLoaded) continue;
                        scenesScanned++;
                        foreach (var root in s.GetRootGameObjects())
                        {
                            ScanGameObjectHierarchy(root, findings, max_results, ref truncated);
                            if (truncated) break;
                        }
                        if (truncated) break;
                    }

                    return Response.Success(
                        $"Found {findings.Count} broken-reference issue(s) across {scenesScanned} loaded scene(s).",
                        new { scope = normalizedScope, findings, count = findings.Count, scenes_scanned = scenesScanned, truncated });
                }

                if (normalizedScope == "assets")
                {
                    if (string.IsNullOrEmpty(path))
                        return Response.Error("PATH_REQUIRED",
                            new { hint = "For scope='assets', pass 'path' to a prefab/asset. Use scope='scene' to scan loaded scenes." });

                    var target = path.Replace('\\', '/');
                    var main = AssetDatabase.LoadMainAssetAtPath(target);
                    if (main == null)
                        return Response.Error("ASSET_NOT_FOUND", new { path = target });

                    if (main is GameObject rootGo)
                        ScanGameObjectHierarchy(rootGo, findings, max_results, ref truncated);
                    else
                        ScanSerializedObjectRefs(main, target, main.GetType().Name, findings, max_results, ref truncated);

                    return Response.Success(
                        $"Found {findings.Count} broken-reference issue(s) in '{target}'.",
                        new { scope = normalizedScope, path = target, findings, count = findings.Count, truncated });
                }

                return Response.Error("INVALID_SCOPE",
                    new { scope, expected = new[] { "assets", "scene" } });
            }
            catch (Exception ex)
            {
                return Response.Error("TOOL_EXCEPTION", new { message = ex.Message });
            }
        }

        // -------- Helpers --------

        // Query Unity's Search dependency index for assets referencing the target.
        // Returns false when the index is unavailable/empty so the caller falls back
        // to the manual scan. The index can also return the target itself and
        // duplicates -- both filtered out here.
        private static bool TrySearchIndexReferencedBy(string target, int maxResults,
            List<string> referencedBy, ref bool truncated)
        {
            try
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                using (var context = UnityEditor.Search.SearchService.CreateContext(
                           "asset", "ref=\"" + target + "\""))
                using (var request = UnityEditor.Search.SearchService.Request(
                           context, UnityEditor.Search.SearchFlags.Synchronous))
                {
                    foreach (var item in request)
                    {
                        if (item == null) continue;
                        var p = UnityEditor.Search.SearchUtils.GetAssetPath(item);
                        if (string.IsNullOrEmpty(p) || p == target || !seen.Add(p)) continue;

                        if (referencedBy.Count >= maxResults)
                        {
                            truncated = true;
                            break;
                        }
                        referencedBy.Add(p);
                    }
                }

                // 0 hits is ambiguous (truly unreferenced vs index not built yet),
                // so fall back to the manual scan in that case.
                return referencedBy.Count > 0;
            }
            catch
            {
                referencedBy.Clear();
                truncated = false;
                return false;
            }
        }

        // Walk every loaded scene for components whose serialized ObjectReference resolves to the
        // target asset (or a sub-asset / sprite of it). Catches scene usages the on-disk asset
        // index cannot see, including in unsaved scenes.
        private static void ScanLoadedScenesForAssetRefs(Object targetAsset, List<object> findings,
            int maxResults, ref bool truncated)
        {
            var targetPath = AssetDatabase.GetAssetPath(targetAsset);
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(targetPath);
            var targetIds = new HashSet<string>();
            foreach (var sub in subAssets)
            {
                if (sub != null)
                    targetIds.Add(ObjectIdCodec.GetSerializableId(sub));
            }

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                if (truncated) return;
                var s = SceneManager.GetSceneAt(i);
                if (!s.IsValid() || !s.isLoaded) continue;

                foreach (var root in s.GetRootGameObjects())
                {
                    if (truncated) return;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (truncated) return;
                        var go = t.gameObject;
                        var objectPath = ObjectsHelper.GetGameObjectPath(go);
                        foreach (var comp in go.GetComponents<Component>())
                        {
                            if (truncated) return;
                            if (comp == null) continue;
                            ScanComponentForAssetRef(comp, objectPath, s.name, targetIds, findings, maxResults, ref truncated);
                        }
                    }
                }
            }
        }

        private static void ScanComponentForAssetRef(Component comp, string objectPath, string sceneName,
            HashSet<string> targetIds, List<object> findings, int maxResults, ref bool truncated)
        {
            try
            {
                using (var so = new SerializedObject(comp))
                {
                    var sp = so.GetIterator();
                    while (sp.Next(true))
                    {
                        if (sp.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var value = sp.objectReferenceValue;
                        if (value == null || !targetIds.Contains(ObjectIdCodec.GetSerializableId(value))) continue;

                        if (!AddFinding(findings, maxResults, ref truncated,
                                new { scene = sceneName, object_path = objectPath, component = comp.GetType().Name, field = sp.propertyPath }))
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright.References] Failed to scan scene component '{objectPath}' ({comp.GetType().Name}): {ex.Message}");
            }
        }

        // Walk a GameObject subtree (inactive included). Reports missing scripts (null components)
        // and dangling object references on every component.
        private static void ScanGameObjectHierarchy(GameObject root, List<object> findings, int maxResults, ref bool truncated)
        {
            if (truncated || root == null) return;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (truncated) return;
                var go = t.gameObject;
                var objectPath = ObjectsHelper.GetGameObjectPath(go);
                var comps = go.GetComponents<Component>();
                for (int i = 0; i < comps.Length; i++)
                {
                    if (truncated) return;
                    var comp = comps[i];
                    if (comp == null)
                    {
                        // Null entry = missing MonoBehaviour script.
                        if (!AddFinding(findings, maxResults, ref truncated,
                                new { object_path = objectPath, kind = "missing_script", component = "<missing script>" }))
                            return;
                        continue;
                    }

                    ScanSerializedObjectRefs(comp, objectPath, comp.GetType().Name, findings, maxResults, ref truncated);
                }
            }
        }

        // Walk one Object's serialized properties, reporting dangling object references
        // (instanceID set but resolved value null).
        private static void ScanSerializedObjectRefs(Object obj, string objectPath, string componentName,
            List<object> findings, int maxResults, ref bool truncated)
        {
            if (obj == null || truncated) return;

            try
            {
                using (var so = new SerializedObject(obj))
                {
                    var sp = so.GetIterator();
                    while (sp.Next(true))
                    {
                        if (sp.propertyType != SerializedPropertyType.ObjectReference)
                            continue;
                        if (ObjectIdCodec.HasSerializedObjectReferenceId(sp) && sp.objectReferenceValue == null)
                        {
                            if (!AddFinding(findings, maxResults, ref truncated,
                                    new { object_path = objectPath, kind = "broken_reference", component = componentName, field = sp.propertyPath }))
                                return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[KitWright.References] Failed to scan '{objectPath}' ({componentName}): {ex.Message}");
            }
        }

        // Append a finding unless the cap is reached; returns false (and sets truncated) when the caller should stop.
        private static bool AddFinding(List<object> findings, int maxResults, ref bool truncated, object finding)
        {
            if (findings.Count >= maxResults)
            {
                truncated = true;
                return false;
            }
            findings.Add(finding);
            return true;
        }
    }
}
