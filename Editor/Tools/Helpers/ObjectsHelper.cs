// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KitWright.Editor.Tools.Helpers
{
    // Distinct type so the invoker can answer an ambiguous single-target resolve as an error
    // instead of acting on an arbitrary one of the matches.
    internal sealed class AmbiguousTargetException : Exception
    {
        public string Target { get; }
        public int MatchCount { get; }
        public List<object> Candidates { get; }

        public AmbiguousTargetException(string target, int matchCount, List<object> candidates)
            : base($"'{target}' matched {matchCount} GameObjects; refusing to act on an arbitrary one.")
        {
            Target = target;
            MatchCount = matchCount;
            Candidates = candidates;
        }
    }

    // Raised because the locator returns a list of GameObjects: there is no empty list that means
    // "ambiguous" rather than "found nothing", and the caller acts on the latter.
    internal sealed class AmbiguousTypeException : Exception
    {
        public string TypeName { get; }
        public string[] Candidates { get; }

        public AmbiguousTypeException(string typeName, string[] candidates)
            : base($"'{typeName}' matches {candidates.Length} loaded types; refusing to search for an arbitrary one.")
        {
            TypeName = typeName;
            Candidates = candidates;
        }
    }

    /// <summary>
    /// Unified GameObject locator. All KitWright tools should resolve scene objects through here
    /// instead of calling <c>GameObject.Find</c> directly — that way name/path/id/tag/layer/component
    /// lookups, inactive-object handling and prefab-stage awareness stay consistent.
    /// </summary>
    public static class ObjectsHelper
    {
        public const string MethodById = "by_id";
        public const string MethodByName = "by_name";
        public const string MethodByPath = "by_path";
        public const string MethodByTag = "by_tag";
        public const string MethodByLayer = "by_layer";
        public const string MethodByComponent = "by_component";
        public const string MethodByIdOrNameOrPath = "by_id_or_name_or_path";

        /// <summary>
        /// Find a single GameObject. Throws <see cref="AmbiguousTargetException"/> when more than
        /// one object matches, so a destructive tool never picks an arbitrary one.
        /// </summary>
        public static GameObject FindObject(string target, string searchMethod = null,
            bool searchInactive = false, bool searchInChildren = false, GameObject root = null)
        {
            var list = FindObjects(target, searchMethod, findAll: false, searchInactive, searchInChildren, root);
            return list.Count > 0 ? list[0] : null;
        }

        /// <summary>
        /// Drop-in replacement for <c>GameObject.Find(target)</c>: same "just resolve this string"
        /// call shape, but accepts an instance ID or hierarchy path in addition to a bare name,
        /// and finds inactive objects across every loaded scene (and the open prefab stage) --
        /// all things <c>GameObject.Find</c> cannot do.
        /// </summary>
        public static GameObject FindTarget(string target, bool searchInChildren = false, GameObject root = null)
        {
            return FindObject(target, MethodByIdOrNameOrPath, searchInactive: true, searchInChildren, root);
        }

        /// <summary>
        /// Core finder. <paramref name="findAll"/> false returns at most one element, and throws
        /// <see cref="AmbiguousTargetException"/> rather than choosing between several matches.
        /// When the active prefab stage is open it is searched in addition to the active scene.
        /// </summary>
        public static List<GameObject> FindObjects(string target, string searchMethod = null,
            bool findAll = true, bool searchInactive = false, bool searchInChildren = false,
            GameObject root = null)
        {
            var results = new List<GameObject>();
            if (string.IsNullOrEmpty(target))
                return results;

            // Auto-detect default method
            if (string.IsNullOrEmpty(searchMethod))
            {
                if (long.TryParse(target, out _))
                    searchMethod = MethodById;
                else if (target.Contains('/'))
                    searchMethod = MethodByPath;
                else
                    searchMethod = MethodByName;
            }

            // Resolve a child-search root first, if requested
            GameObject rootObj = root;
            if (searchInChildren && rootObj == null)
            {
                rootObj = FindObject(target, MethodByIdOrNameOrPath, searchInactive: true);
                if (rootObj == null)
                    return results;
            }

            switch (searchMethod)
            {
                case MethodById:
                {
                    var go = ObjectIdCodec.ToObject(target) as GameObject;
                    if (go != null)
                        results.Add(go);
                    break;
                }
                case MethodByName:
                {
                    foreach (var go in EnumerateSearchPool(rootObj, searchInactive))
                    {
                        if (go.name == target)
                            results.Add(go);
                    }
                    break;
                }
                case MethodByPath:
                {
                    if (rootObj != null)
                    {
                        var t = rootObj.transform.Find(target);
                        if (t != null) results.Add(t.gameObject);
                    }
                    else
                    {
                        // Search every scene including prefab stage
                        foreach (var scene in EnumerateLoadedScenes())
                        {
                            foreach (var sceneRoot in scene.GetRootGameObjects())
                            {
                                if (sceneRoot.name == target.Split('/')[0])
                                {
                                    var rest = target.Contains('/') ? target.Substring(target.IndexOf('/') + 1) : null;
                                    if (rest == null)
                                    {
                                        results.Add(sceneRoot);
                                    }
                                    else
                                    {
                                        var t = sceneRoot.transform.Find(rest);
                                        if (t != null) results.Add(t.gameObject);
                                    }
                                }
                            }
                        }
                        var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
                        if (prefabStage != null && prefabStage.prefabContentsRoot != null)
                        {
                            var stageRoot = prefabStage.prefabContentsRoot;
                            if (stageRoot.name == target.Split('/')[0])
                            {
                                var rest = target.Contains('/') ? target.Substring(target.IndexOf('/') + 1) : null;
                                if (rest == null)
                                {
                                    results.Add(stageRoot);
                                }
                                else
                                {
                                    var t = stageRoot.transform.Find(rest);
                                    if (t != null) results.Add(t.gameObject);
                                }
                            }
                        }
                    }
                    break;
                }
                case MethodByTag:
                {
                    foreach (var go in EnumerateSearchPool(rootObj, searchInactive))
                    {
                        try
                        {
                            if (go.CompareTag(target))
                                results.Add(go);
                        }
                        catch (UnityException)
                        {
                            // Tag not defined; skip silently
                        }
                    }
                    break;
                }
                case MethodByLayer:
                {
                    int layerIndex;
                    if (!int.TryParse(target, out layerIndex))
                        layerIndex = LayerMask.NameToLayer(target);
                    if (layerIndex >= 0)
                    {
                        foreach (var go in EnumerateSearchPool(rootObj, searchInactive))
                        {
                            if (go.layer == layerIndex)
                                results.Add(go);
                        }
                    }
                    break;
                }
                case MethodByComponent:
                {
                    var compType = TypeResolver.ResolveComponent(target);
                    if (compType == null)
                    {
                        var candidates = TypeResolver.AmbiguousCandidates(target);
                        if (candidates != null)
                            throw new AmbiguousTypeException(target, candidates);
                    }
                    else
                    {
                        foreach (var go in EnumerateSearchPool(rootObj, searchInactive))
                        {
                            if (go.GetComponent(compType) != null)
                                results.Add(go);
                        }
                    }
                    break;
                }
                case MethodByIdOrNameOrPath:
                {
                    var byId = ObjectIdCodec.ToObject(target) as GameObject;
                    if (byId != null) { results.Add(byId); break; }
                    if (target.Contains('/'))
                    {
                        // Re-enter as path
                        return FindObjects(target, MethodByPath, findAll, searchInactive, searchInChildren, root);
                    }
                    return FindObjects(target, MethodByName, findAll, searchInactive, searchInChildren, root);
                }
                default:
                    Debug.LogWarning($"[KitWright] Unknown search method '{searchMethod}'");
                    break;
            }

            var distinct = results.Distinct().ToList();
            if (!findAll && distinct.Count > 1)
                throw new AmbiguousTargetException(target, distinct.Count,
                    distinct.Take(MaxCandidates)
                        .Select(go => (object)new
                        {
                            id = ObjectIdCodec.GetSerializableId(go),
                            path = GetGameObjectPath(go)
                        })
                        .ToList());

            return distinct;
        }

        /// <summary>
        /// Enumerate every GameObject considered "in scope" — active scene roots, additively-loaded
        /// scenes, and the open prefab stage. <paramref name="includeInactive"/> uses
        /// <c>Resources.FindObjectsOfTypeAll</c> to also surface inactive editor-time objects.
        /// </summary>
        private static IEnumerable<GameObject> EnumerateSearchPool(GameObject root, bool includeInactive)
        {
            if (root != null)
            {
                foreach (var t in root.GetComponentsInChildren<Transform>(includeInactive))
                    yield return t.gameObject;
                yield break;
            }

            if (includeInactive)
            {
                foreach (var go in Resources.FindObjectsOfTypeAll<GameObject>())
                {
                    if (!go.scene.IsValid())
                        continue;
                    if (go.hideFlags == HideFlags.NotEditable || go.hideFlags == HideFlags.HideAndDontSave)
                        continue;
                    yield return go;
                }
                yield break;
            }

            foreach (var scene in EnumerateLoadedScenes())
            {
                foreach (var sceneRoot in scene.GetRootGameObjects())
                {
                    foreach (var t in sceneRoot.GetComponentsInChildren<Transform>(false))
                        yield return t.gameObject;
                }
            }

            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.prefabContentsRoot != null)
            {
                foreach (var t in prefabStage.prefabContentsRoot.GetComponentsInChildren<Transform>(true))
                    yield return t.gameObject;
            }
        }

        private static IEnumerable<Scene> EnumerateLoadedScenes()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.IsValid() && scene.isLoaded)
                    yield return scene;
            }
        }

        /// <summary>
        /// The one error code every failed GameObject resolve returns. Tools used to split this
        /// between TARGET_NOT_FOUND and GAME_OBJECT_NOT_FOUND for the same condition, so a client
        /// handling one did not recognise the other.
        /// </summary>
        public const string NotFoundCode = "GAME_OBJECT_NOT_FOUND";

        private const int MaxCandidates = 5;
        private const int MaxEditDistance = 3;
        private const int MaxCandidateScan = 20000;

        /// <summary>
        /// Error for a GameObject that could not be resolved, carrying the near-miss names that
        /// do exist. <paramref name="paramName"/> is the tool's own parameter name so the echoed
        /// value tells the agent which argument to change.
        /// </summary>
        public static object NotFound(string paramName, string value, string findMethod = null)
        {
            var candidates = SuggestTargets(value);
            var idOccupant = DescribeIdOccupant(value);
            return Response.Error(NotFoundCode,
                NotFoundData(paramName, value, findMethod, candidates, idOccupant),
                NotFoundHint(value, candidates.Count, idOccupant));
        }

        /// <summary>Same as <see cref="NotFound"/> for call sites that must return a JSON string.</summary>
        public static string NotFoundText(string paramName, string value, string findMethod = null)
        {
            var candidates = SuggestTargets(value);
            var idOccupant = DescribeIdOccupant(value);
            return ToolResultFormatter.Error(NotFoundCode,
                NotFoundData(paramName, value, findMethod, candidates, idOccupant),
                NotFoundHint(value, candidates.Count, idOccupant));
        }

        private static Dictionary<string, object> NotFoundData(string paramName, string value,
            string findMethod, List<string> candidates, string idOccupant)
        {
            var data = new Dictionary<string, object>
            {
                [string.IsNullOrEmpty(paramName) ? "target" : paramName] = value
            };
            if (!string.IsNullOrEmpty(findMethod))
                data["find_method"] = findMethod;
            if (!string.IsNullOrEmpty(idOccupant))
                data["id_refers_to"] = idOccupant;
            if (candidates.Count > 0)
                data["candidates"] = candidates;
            return data;
        }

        /// <summary>
        /// What the id actually resolves to right now, or null when the value is not a live id.
        /// Ids are reassigned across domain reloads and scene loads, so a cached id can land on an
        /// unrelated object; naming the current occupant is what makes that visible to the caller.
        /// </summary>
        private static string DescribeIdOccupant(string value)
        {
            var obj = ObjectIdCodec.ToObject(value);
            if (obj == null)
                return null;

            var go = obj as GameObject ?? (obj as Component)?.gameObject;
            var path = go != null ? GetGameObjectPath(go) : null;
            return string.IsNullOrEmpty(path) || path == obj.name
                ? $"{obj.GetType().Name} '{obj.name}'"
                : $"{obj.GetType().Name} '{obj.name}' at {path}";
        }

        private static string NotFoundHint(string value, int candidateCount, string idOccupant)
        {
            if (!string.IsNullOrEmpty(idOccupant))
                return $"That id resolves to {idOccupant}, which is not the GameObject this call asked for. " +
                       "Instance ids are reassigned on every domain reload (script compile, entering play mode), " +
                       "so re-read get_hierarchy or find_game_objects for current ids.";

            // Instance ids are reassigned by every domain reload, so a stale id is the one failure
            // an agent cannot diagnose from the name alone.
            if (!string.IsNullOrEmpty(value) && long.TryParse(value, out _))
                return "Instance ids change on every domain reload (script compile, entering play mode). " +
                       "Re-read get_hierarchy or find_game_objects for current ids.";

            if (candidateCount > 0)
                return "Nothing matched exactly. 'candidates' lists existing objects with similar paths; " +
                       "name matching is case-sensitive.";

            return "No similar object exists either. Call get_hierarchy to see the scene, or " +
                   "find_game_objects with find_method=by_component/by_tag to search another way.";
        }

        /// <summary>
        /// Existing objects whose name (or path, when the query looks like one) is closest to a
        /// query that resolved to nothing. Ranked exact-but-wrong-case, prefix, substring, then
        /// small edit distance.
        /// </summary>
        internal static List<string> SuggestTargets(string query)
        {
            var suggestions = new List<string>();
            if (string.IsNullOrEmpty(query) || long.TryParse(query, out _))
                return suggestions;

            var matchPath = query.Contains('/');
            var scored = new List<KeyValuePair<int, string>>();
            var scanned = 0;

            foreach (var go in EnumerateSearchPool(null, true))
            {
                if (++scanned > MaxCandidateScan)
                    break;

                var path = GetGameObjectPath(go);
                var score = ScoreCandidate(query, matchPath ? path : go.name);
                if (score >= 0)
                    scored.Add(new KeyValuePair<int, string>(score, path));
            }

            return scored
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .Distinct()
                .Take(MaxCandidates)
                .ToList();
        }

        private static int ScoreCandidate(string query, string subject)
        {
            if (string.IsNullOrEmpty(subject))
                return -1;
            // Name lookup is case-sensitive, so a case-only mismatch reaches here and is the
            // single most useful thing to surface.
            if (string.Equals(query, subject, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (subject.StartsWith(query, StringComparison.OrdinalIgnoreCase) ||
                query.StartsWith(subject, StringComparison.OrdinalIgnoreCase))
                return 1;
            if (subject.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                query.IndexOf(subject, StringComparison.OrdinalIgnoreCase) >= 0)
                return 2;
            if (Math.Abs(subject.Length - query.Length) > MaxEditDistance)
                return -1;

            var distance = EditDistance(query.ToLowerInvariant(), subject.ToLowerInvariant());
            return distance <= MaxEditDistance ? 2 + distance : -1;
        }

        internal static int EditDistance(string a, string b)
        {
            if (a == b) return 0;
            if (a.Length == 0) return b.Length;
            if (b.Length == 0) return a.Length;

            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];
            for (var j = 0; j <= b.Length; j++)
                previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (var j = 1; j <= b.Length; j++)
                {
                    var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
                }
                Array.Copy(current, previous, current.Length);
            }
            return previous[b.Length];
        }

        /// <summary>
        /// Look up a Component by its instance id. Returns null if missing or if the id refers to
        /// something that isn't a Component.
        /// </summary>
        public static Component FindComponentById(string instanceId)
        {
            return ObjectIdCodec.ToObject(instanceId) as Component;
        }

        /// <summary>
        /// Build a "/Foo/Bar/Baz" path for a GameObject relative to its scene root.
        /// </summary>
        public static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return string.Empty;
            var t = go.transform;
            var parts = new List<string> { t.name };
            while (t.parent != null)
            {
                t = t.parent;
                parts.Add(t.name);
            }
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
