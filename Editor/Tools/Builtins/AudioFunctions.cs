// Copyright (C) KitWright. All rights reserved.

// com.unity.modules.audio is optional; without it these tools disappear instead of breaking the build.
#if KITWRIGHT_AUDIO
using System;
using System.Collections.Generic;
using System.Reflection;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Audio;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Audio")]
    internal static class AudioFunctions
    {
        [Description("Add and configure an AudioSource on a GameObject. Only the parameters you pass are applied. clip/mixer_group accept an asset path.")]
        public static object AddAudioSource(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("AudioClip asset path (e.g. 'Assets/Sfx/hit.wav')", Required = false)] string clip = null,
            [ToolParam("Mixer asset path (its Master group), or '<mixer asset path>/GroupName' (e.g. 'Assets/Audio/Main.mixer/SFX'; a nested group as 'Master/Music')", Required = false)] string mixer_group = null,
            [ToolParam("Volume 0..1", Required = false)] float? volume = null,
            [ToolParam("Pitch (0.1..3 typical)", Required = false)] float? pitch = null,
            [ToolParam("Loop the clip", Required = false)] bool? loop = null,
            [ToolParam("Play automatically when the scene starts", Required = false)] bool? play_on_awake = null,
            [ToolParam("Spatial blend 0 (2D) .. 1 (3D)", Required = false)] float? spatial_blend = null,
            [ToolParam("Min distance for 3D attenuation", Required = false)] float? min_distance = null,
            [ToolParam("Max distance for 3D attenuation", Required = false)] float? max_distance = null)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            // `== null`, never `??`: in the editor GetComponent hands back a stub for a missing
            // component so that dereferencing it reports "there is no X attached" instead of a bare
            // NullReferenceException. Unity's == operator calls that stub null; ?? compares references
            // and does not, so the component was never added and the first write below threw.
            // Both resolved before the component is added: a typo in either left a bare AudioSource
            // on an object that had none, on a call whose answer said it had failed.
            AudioClip loadedClip = null;
            if (clip != null)
            {
                loadedClip = AssetDatabase.LoadAssetAtPath<AudioClip>(clip);
                if (loadedClip == null) return Response.Error("CLIP_NOT_FOUND", new { clip });
            }

            AudioMixerGroup group = null;
            if (mixer_group != null)
            {
                group = LoadMixerGroup(mixer_group, out var groupError);
                if (group == null)
                    return Response.Error(groupError, new { mixer_group },
                        groupError == "MIXER_GROUP_AMBIGUOUS" ? "Several groups match; use the full group path, e.g. 'Assets/Audio/Main.mixer/Master/Music'." : null);
            }

            var src = go.GetComponent<AudioSource>();
            if (src != null && loadedClip == null && group == null && !volume.HasValue && !pitch.HasValue && !loop.HasValue
                && !play_on_awake.HasValue && !spatial_blend.HasValue && !min_distance.HasValue && !max_distance.HasValue)
                return Response.Success($"'{go.name}' already has an AudioSource and no setting was passed; nothing changed.", Describe(src));

            if (src == null)
                src = Undo.AddComponent<AudioSource>(go);
            Undo.RecordObject(src, "Configure AudioSource");

            if (loadedClip != null) src.clip = loadedClip;
            if (group != null) src.outputAudioMixerGroup = group;
            if (volume.HasValue) src.volume = Mathf.Clamp01(volume.Value);
            if (pitch.HasValue) src.pitch = pitch.Value;
            if (loop.HasValue) src.loop = loop.Value;
            if (play_on_awake.HasValue) src.playOnAwake = play_on_awake.Value;
            if (spatial_blend.HasValue) src.spatialBlend = Mathf.Clamp01(spatial_blend.Value);
            if (min_distance.HasValue) src.minDistance = Mathf.Max(0f, min_distance.Value);
            if (max_distance.HasValue) src.maxDistance = Mathf.Max(0f, max_distance.Value);

            EditorUtility.SetDirty(src);
            return Response.Success($"AudioSource configured on '{go.name}'.", Describe(src));
        }

        [Description("Set global audio settings: master volume (AudioListener.volume) and/or pause all audio (AudioListener.pause).")]
        public static object SetGlobalAudio(
            [ToolParam("Master volume 0..1", Required = false)] float? volume = null,
            [ToolParam("Pause/unpause all audio", Required = false)] bool? pause = null)
        {
            if (!volume.HasValue && !pause.HasValue)
                return Response.Error("MISSING_PARAM", new { expected = "volume and/or pause" }, "Pass volume (0..1) and/or pause (true/false).");

            if (volume.HasValue) AudioListener.volume = Mathf.Clamp01(volume.Value);
            if (pause.HasValue) AudioListener.pause = pause.Value;
            return Response.Success("Global audio updated.", new { volume = AudioListener.volume, pause = AudioListener.pause });
        }

        [Description("Add an AudioListener to a GameObject (usually the main camera). Fails if the same scene (or prefab stage) already has one on another object unless allow_multiple is true.")]
        public static object AddAudioListener(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target,
            [ToolParam("Add even if another AudioListener already exists in the same scene", Required = false)] bool allow_multiple = false)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);

            if (go.GetComponent<AudioListener>() != null)
                return Response.Success($"'{go.name}' already has an AudioListener.");

            string inAnotherScene = null;
            foreach (var existing in ObjectsHelper.FindObjectsByTypeUnsorted<AudioListener>(FindObjectsInactive.Exclude))
            {
                if (existing.gameObject.scene != go.scene)
                {
                    if (inAnotherScene == null) inAnotherScene = existing.gameObject.name;
                }
                else if (!allow_multiple)
                {
                    return Response.Error("LISTENER_EXISTS", new { existingOn = existing.gameObject.name, hint = "pass allow_multiple=true to add anyway" });
                }
            }

            Undo.AddComponent<AudioListener>(go);
            return Response.Success($"AudioListener added to '{go.name}'." + (inAnotherScene == null ? string.Empty :
                $" Another loaded scene already has one (on '{inAnotherScene}'): Unity warns about two audio listeners and only one is heard."));
        }

        [Description("Get the AudioSource settings on a GameObject.")]
        [ReadOnlyTool]
        public static object GetAudioSourceInfo(
            [ToolParam("GameObject name, hierarchy path, or instance ID")] string target)
        {
            var go = ObjectsHelper.FindTarget(target);
            if (go == null) return ObjectsHelper.NotFound("target", target);
            var src = go.GetComponent<AudioSource>();
            if (src == null) return Response.Error("NO_AUDIO_SOURCE", new { target });
            return Response.Success($"AudioSource on '{go.name}'.", Describe(src));
        }

        [Description("Preview-play an AudioClip in the editor without entering Play Mode (uses the internal AudioUtil API). Pass an AudioClip asset path.")]
        public static object PlayClipPreview(
            [ToolParam("AudioClip asset path (e.g. 'Assets/Sfx/hit.wav')")] string clip,
            [ToolParam("Loop the preview", Required = false)] bool loop = false)
        {
            var loaded = AssetDatabase.LoadAssetAtPath<AudioClip>(clip);
            if (loaded == null) return Response.Error("CLIP_NOT_FOUND", new { clip });

            if (!TryInvokeAudioUtil("PlayPreviewClip", new object[] { loaded, 0, loop },
                    new[] { typeof(AudioClip), typeof(int), typeof(bool) }, out var error))
                return Response.Error("AUDIO_PREVIEW_UNAVAILABLE", new { message = error });

            return Response.Success($"Previewing '{clip}' ({loaded.length:F2}s).");
        }

        [Description("Stop all editor audio clip previews, including ones started from the Inspector, not only those from play_clip_preview.")]
        public static object StopClipPreview()
        {
            if (!TryInvokeAudioUtil("StopAllPreviewClips", Array.Empty<object>(), Type.EmptyTypes, out var error)
                && !TryInvokeAudioUtil("StopAllClips", Array.Empty<object>(), Type.EmptyTypes, out error))
                return Response.Error("AUDIO_PREVIEW_UNAVAILABLE", new { message = error });
            return Response.Success("Stopped editor audio previews.");
        }

        private static object Describe(AudioSource src) => new
        {
            clip = src.clip != null ? AssetDatabase.GetAssetPath(src.clip) : null,
            clipName = src.clip != null ? src.clip.name : null,
            volume = src.volume,
            pitch = src.pitch,
            loop = src.loop,
            playOnAwake = src.playOnAwake,
            spatialBlend = src.spatialBlend,
            minDistance = src.minDistance,
            maxDistance = src.maxDistance,
            mixerGroup = src.outputAudioMixerGroup != null ? src.outputAudioMixerGroup.name : null
        };

        private static AudioMixerGroup LoadMixerGroup(string spec, out string error)
        {
            error = "MIXER_GROUP_NOT_FOUND";
            SplitMixerSpec(spec, out var assetPath, out var groupName);

            var mixer = AssetDatabase.LoadAssetAtPath<AudioMixer>(assetPath);
            if (mixer == null) return null;

            var all = mixer.FindMatchingGroups(string.Empty);
            if (all == null || all.Length == 0) return null;

            var byPath = PickMixerGroupByPath(GroupPaths(all), groupName);
            if (byPath >= 0) return all[byPath];
            if (byPath == AmbiguousGroup)
            {
                error = "MIXER_GROUP_AMBIGUOUS";
                return null;
            }

            var groups = mixer.FindMatchingGroups(groupName ?? string.Empty);
            if (groups == null) return null;

            var pick = PickMixerGroup(Array.ConvertAll(groups, g => g.name), groupName);
            if (pick == AmbiguousGroup) error = "MIXER_GROUP_AMBIGUOUS";
            return pick >= 0 ? groups[pick] : null;
        }

        // 'Master/Music/Music' for every group, read from the groups' own child lists: AudioMixerGroup has no
        // parent, and FindMatchingGroups matches substrings of the path, so 'Master/Music' also returns the
        // nested 'Master/Music/Music' and a name alone cannot tell them apart.
        internal static string[] GroupPaths(AudioMixerGroup[] groups)
        {
            var parents = new Dictionary<AudioMixerGroup, AudioMixerGroup>();
            foreach (var group in groups)
            {
                using (var serialized = new SerializedObject(group))
                {
                    var children = serialized.FindProperty("m_Children");
                    if (children == null) continue;
                    for (var i = 0; i < children.arraySize; i++)
                    {
                        if (children.GetArrayElementAtIndex(i).objectReferenceValue is AudioMixerGroup child)
                            parents[child] = group;
                    }
                }
            }

            var paths = new string[groups.Length];
            for (var i = 0; i < groups.Length; i++)
            {
                var path = groups[i].name;
                var current = groups[i];
                for (var depth = 0; depth < 64 && parents.TryGetValue(current, out var parent); depth++)
                {
                    path = parent.name + "/" + path;
                    current = parent;
                }
                paths[i] = path;
            }
            return paths;
        }

        // An exact full path wins, then a path that ends with the given one ('Music' or 'Music/Music'). Index into
        // paths, -1 for none, AmbiguousGroup when several fit.
        internal static int PickMixerGroupByPath(string[] paths, string groupName)
        {
            if (paths.Length == 0) return -1;
            if (string.IsNullOrEmpty(groupName)) return 0;

            int exact = -1, exactCount = 0, tail = -1, tailCount = 0;
            for (var i = 0; i < paths.Length; i++)
            {
                if (paths[i] == groupName)
                {
                    exact = i;
                    exactCount++;
                }
                else if (paths[i].EndsWith("/" + groupName, StringComparison.Ordinal))
                {
                    tail = i;
                    tailCount++;
                }
            }

            if (exactCount > 0) return exactCount == 1 ? exact : AmbiguousGroup;
            if (tailCount > 0) return tailCount == 1 ? tail : AmbiguousGroup;
            return -1;
        }

        // The asset path itself contains slashes ('Assets/Audio/Main.mixer'), so the group part can only
        // start after the '.mixer' that ends the path.
        internal static void SplitMixerSpec(string spec, out string assetPath, out string groupName)
        {
            assetPath = spec;
            groupName = null;
            for (int i = spec.IndexOf(".mixer", StringComparison.OrdinalIgnoreCase); i >= 0;
                 i = spec.IndexOf(".mixer", i + 1, StringComparison.OrdinalIgnoreCase))
            {
                int end = i + ".mixer".Length;
                if (end == spec.Length) return;
                if (spec[end] != '/') continue;

                assetPath = spec.Substring(0, end);
                groupName = spec.Substring(end + 1);
                if (groupName.Length == 0) groupName = null;
                return;
            }
        }

        internal const int AmbiguousGroup = -2;

        // FindMatchingGroups matches by substring of the group path, so 'Music' also returns 'UIMusic'.
        // Returns an index into names, -1 for none, AmbiguousGroup when several fit and none is exact.
        internal static int PickMixerGroup(string[] names, string groupName)
        {
            if (names.Length == 0) return -1;
            if (string.IsNullOrEmpty(groupName)) return 0;

            var leaf = groupName.Substring(groupName.LastIndexOf('/') + 1);
            int exact = -1, exactCount = 0;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] != leaf) continue;
                exact = i;
                exactCount++;
            }

            if (exactCount == 1) return exact;
            if (exactCount == 0 && names.Length == 1) return 0;
            return AmbiguousGroup;
        }

        private static bool TryInvokeAudioUtil(string method, object[] args, Type[] signature, out string error)
        {
            error = null;
            var audioUtil = Type.GetType("UnityEditor.AudioUtil, UnityEditor");
            if (audioUtil == null) { error = "UnityEditor.AudioUtil not found"; return false; }

            var mi = audioUtil.GetMethod(method, BindingFlags.Public | BindingFlags.Static, null, signature, null);
            if (mi == null) { error = $"AudioUtil.{method} signature not found (Unity version mismatch)"; return false; }

            mi.Invoke(null, args);
            return true;
        }
    }
}
#endif
