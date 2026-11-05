// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KitWright.Editor.Tools.Builtins
{
    /// <summary>
    /// Authoring for TimelineAsset, in its own assembly so the package builds without com.unity.timeline.
    /// director_evaluate (TimelineFunctions) samples a bound Timeline; these create and read the asset itself.
    /// </summary>
    [ToolProvider("Timeline")]
    internal static class TimelineAuthoringFunctions
    {
        private const string MarkerTrackType = "Marker";

        private static readonly Dictionary<string, Type> TrackTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["Animation"] = typeof(AnimationTrack),
            ["Audio"] = typeof(AudioTrack),
            ["Activation"] = typeof(ActivationTrack),
            ["Control"] = typeof(ControlTrack),
            ["Playable"] = typeof(PlayableTrack),
            ["Signal"] = typeof(SignalTrack),
            ["Group"] = typeof(GroupTrack)
        };

        [Description("Create a TimelineAsset (.playable). Refuses to replace an existing asset unless overwrite=true. " +
                     "Add tracks with add_timeline_track and clips with add_timeline_clip.")]
        public static object CreateTimeline(
            [ToolParam("Asset path ending in .playable, under Assets/")] string timeline_path,
            [ToolParam("Timeline frame rate, above 0 up to 1000. Default 60.", Required = false)] double frame_rate = 60,
            [ToolParam("Replace a timeline that already exists at that path", Required = false)] bool overwrite = false)
        {
            if (!AssetFunctions.TryResolveUnderAssetsOrPackages(timeline_path, out var assetPath, out var absolutePath)
                || !assetPath.EndsWith(".playable", StringComparison.OrdinalIgnoreCase))
                return Response.Error("INVALID_PATH", new { timeline_path }, "timeline_path must be a .playable asset under Assets/.");

            if (!(frame_rate > 0 && frame_rate <= 1000))
                return Response.Error("INVALID_PARAM", new { param = "frame_rate", provided = frame_rate }, "frame_rate must be above 0 and at most 1000.");

            if (File.Exists(absolutePath) && !overwrite)
                return Response.Error("TIMELINE_EXISTS", new { timeline_path = assetPath },
                    "An asset is already at this path. Pass overwrite=true to replace it.");

            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath));

            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            timeline.editorSettings.frameRate = frame_rate;
            AssetDatabase.CreateAsset(timeline, assetPath);
            AssetDatabase.SaveAssets();

            return Response.Success($"Created '{assetPath}'.", new { timeline_path = assetPath, frameRate = timeline.editorSettings.frameRate });
        }

        [Description("Add a track to a TimelineAsset: Animation, Audio, Activation, Control, Playable, Signal, Marker or Group, " +
                     "optionally nested under a Group (or Animation) track named by parent_track. The Marker track is the " +
                     "timeline's single marker track and takes no parent.")]
        public static object AddTimelineTrack(
            [ToolParam("Path to the .playable asset")] string timeline_path,
            [ToolParam("Animation, Audio, Activation, Control, Playable, Signal, Marker or Group")] string track_type,
            [ToolParam("Track name (default: the track type's name)", Required = false)] string name = null,
            [ToolParam("Name of an existing track to nest the new one under", Required = false)] string parent_track = null)
        {
            var timeline = Load(timeline_path, out var error);
            if (timeline == null) return error;

            var isMarker = string.Equals(track_type, MarkerTrackType, StringComparison.OrdinalIgnoreCase);
            if (!isMarker && !TrackTypes.ContainsKey(track_type ?? string.Empty))
                return Response.Error("UNKNOWN_TRACK_TYPE", new { track_type, valid = TrackTypes.Keys.Concat(new[] { MarkerTrackType }).ToArray() });

            TrackAsset parent = null;
            if (!string.IsNullOrWhiteSpace(parent_track))
            {
                if (isMarker)
                    return Response.Error("INVALID_PARAM", new { param = "parent_track" }, "The marker track cannot be nested.");

                parent = FindTrack(timeline, parent_track, "parent_track", out error);
                if (parent == null) return error;
            }

            if (isMarker)
            {
                if (timeline.markerTrack != null)
                    return Response.Error("MARKER_TRACK_EXISTS", new { timeline_path });

                timeline.CreateMarkerTrack();
                Save(timeline);
                return Response.Success($"Added the marker track to '{timeline_path}'.", new { timeline_path, track_type = MarkerTrackType });
            }

            var type = TrackTypes[track_type];
            var trackName = string.IsNullOrWhiteSpace(name) ? ObjectNames.NicifyVariableName(type.Name) : name;

            TrackAsset track;
            try
            {
                track = timeline.CreateTrack(type, parent, trackName);
            }
            catch (InvalidOperationException ex)
            {
                return Response.Error("TRACK_NOT_ALLOWED", new { track_type, parent_track, message = ex.Message });
            }

            Save(timeline);
            return Response.Success($"Added {type.Name} '{track.name}' to '{timeline_path}'.",
                new { timeline_path, track = track.name, track_type = type.Name, parent = parent != null ? parent.name : null });
        }

        [Description("Add a clip to a named track of a TimelineAsset. An Animation track needs asset_path to an AnimationClip, " +
                     "an Audio track an AudioClip; Activation, Control and Playable tracks get their default clip. Signal, " +
                     "Marker and Group tracks hold no clips. duration 0 keeps the length Unity picks for the clip.")]
        public static object AddTimelineClip(
            [ToolParam("Path to the .playable asset")] string timeline_path,
            [ToolParam("Name of the target track")] string track,
            [ToolParam("Clip start time in seconds, 0 or more")] double start,
            [ToolParam("Clip duration in seconds, 0 or more. 0 keeps the default.", Required = false)] double duration = 0,
            [ToolParam("AnimationClip or AudioClip asset path, for Animation and Audio tracks", Required = false)] string asset_path = null)
        {
            if (!IsFiniteFrom(start, 0) || !IsFiniteFrom(duration, 0))
                return Response.Error("INVALID_PARAM", new { start, duration }, "start and duration must be finite numbers of 0 or more.");

            var timeline = Load(timeline_path, out var error);
            if (timeline == null) return error;

            var target = FindTrack(timeline, track, nameof(track), out error);
            if (target == null) return error;

            if (!HoldsClips(target))
                return Response.Error("CLIP_NOT_SUPPORTED", new { track = target.name, track_type = target.GetType().Name },
                    "This track type holds no clips.");

            UnityEngine.Object source = null;
            if (NeedsSourceAsset(target))
            {
                if (string.IsNullOrWhiteSpace(asset_path))
                    return Response.Error("ASSET_REQUIRED", new { track = target.name, track_type = target.GetType().Name },
                        "An Animation track needs an AnimationClip and an Audio track an AudioClip in asset_path.");

                source = LoadSource(target, asset_path);
                if (source == null)
                    return Response.Error("ASSET_NOT_FOUND", new { asset_path, track_type = target.GetType().Name });
            }
            else if (!string.IsNullOrWhiteSpace(asset_path))
            {
                return Response.Error("INVALID_PARAM", new { param = "asset_path", track_type = target.GetType().Name },
                    "asset_path only applies to Animation and Audio tracks.");
            }

            TimelineClip clip;
            try
            {
                clip = CreateClip(target, source);
            }
            catch (InvalidOperationException ex)
            {
                return Response.Error("CLIP_NOT_SUPPORTED", new { track = target.name, track_type = target.GetType().Name, message = ex.Message });
            }

            if (clip == null)
                return Response.Error("CLIP_NOT_SUPPORTED", new { track = target.name, track_type = target.GetType().Name },
                    "Unity created no clip on this track.");

            clip.start = start;
            if (duration > 0)
                clip.duration = duration;

            Save(timeline);
            return Response.Success($"Added clip '{clip.displayName}' to '{target.name}'.", new
            {
                timeline_path,
                track = target.name,
                clip = clip.displayName,
                start = clip.start,
                duration = clip.duration,
                end = clip.end
            });
        }

        [Description("Read a TimelineAsset's structure: frame rate, duration and its tracks with their clips, markers " +
                     "and nested child tracks.")]
        [ReadOnlyTool]
        public static object GetTimeline(
            [ToolParam("Path to the .playable asset")] string timeline_path)
        {
            var timeline = Load(timeline_path, out var error);
            if (timeline == null) return error;

            var tracks = timeline.GetRootTracks().Select(Describe).ToArray();
            return Response.Success($"Read '{timeline_path}' ({AllTracks(timeline).Count()} track(s)).", new
            {
                timeline_path,
                frameRate = timeline.editorSettings.frameRate,
                duration = timeline.duration,
                markerTrack = timeline.markerTrack != null ? new { markers = timeline.markerTrack.GetMarkers().Count() } : null,
                tracks
            });
        }

        private static TimelineAsset Load(string path, out object error)
        {
            error = null;
            var timeline = AssetDatabase.LoadAssetAtPath<TimelineAsset>(path);
            if (timeline == null)
                error = Response.Error("TIMELINE_NOT_FOUND", new { timeline_path = path });
            return timeline;
        }

        private static void Save(TimelineAsset timeline)
        {
            EditorUtility.SetDirty(timeline);
            AssetDatabase.SaveAssets();
        }

        private static IEnumerable<TrackAsset> AllTracks(TimelineAsset timeline) =>
            timeline.GetRootTracks().SelectMany(Flatten);

        private static IEnumerable<TrackAsset> Flatten(TrackAsset track) =>
            new[] { track }.Concat(track.GetChildTracks().SelectMany(Flatten));

        private static TrackAsset FindTrack(TimelineAsset timeline, string name, string param, out object error)
        {
            error = null;
            var matches = AllTracks(timeline).Where(t => t.name == name).ToArray();
            if (matches.Length == 1)
                return matches[0];

            error = matches.Length == 0
                ? Response.Error("TRACK_NOT_FOUND", new { param, name, available = AllTracks(timeline).Select(t => t.name).ToArray() })
                : Response.Error("AMBIGUOUS_TRACK", new { param, name, count = matches.Length }, "Several tracks share that name; rename one with the Timeline window.");
            return null;
        }

        private static object Describe(TrackAsset track) => new
        {
            name = track.name,
            type = track.GetType().Name,
            muted = track.muted,
            locked = track.locked,
            clips = track.GetClips().Select(c => new
            {
                name = c.displayName,
                start = c.start,
                end = c.end,
                duration = c.duration,
                assetType = c.asset != null ? c.asset.GetType().Name : null,
                source = SourcePath(c)
            }).ToArray(),
            markers = track.GetMarkers().Count(),
            children = track.GetChildTracks().Select(Describe).ToArray()
        };

        // TrackAsset itself carries a clip type (TrackAsset), which a group inherits: asking it for a default clip logs an error.
        private static bool HoldsClips(TrackAsset track) =>
            track.GetType().GetCustomAttributes(typeof(TrackClipTypeAttribute), true).Cast<TrackClipTypeAttribute>()
                .Any(a => a.inspectedType != null && !a.inspectedType.IsAbstract && a.inspectedType != typeof(TrackAsset));

        private static bool NeedsSourceAsset(TrackAsset track) => track is AnimationTrack || track is AudioTrack;

        private static UnityEngine.Object LoadSource(TrackAsset track, string assetPath)
        {
#if KITWRIGHT_ANIMATION
            if (track is AnimationTrack)
                return AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
#endif
#if KITWRIGHT_AUDIO
            if (track is AudioTrack)
                return AssetDatabase.LoadAssetAtPath<AudioClip>(assetPath);
#endif
            return null;
        }

        private static TimelineClip CreateClip(TrackAsset track, UnityEngine.Object source)
        {
#if KITWRIGHT_ANIMATION
            if (source is AnimationClip animation)
            {
                var clip = track.CreateClip<AnimationPlayableAsset>();
                ((AnimationPlayableAsset)clip.asset).clip = animation;
                clip.displayName = animation.name;
                return clip;
            }
#endif
#if KITWRIGHT_AUDIO
            if (source is AudioClip audio)
            {
                var clip = track.CreateClip<AudioPlayableAsset>();
                ((AudioPlayableAsset)clip.asset).clip = audio;
                clip.displayName = audio.name;
                return clip;
            }
#endif
            return track.CreateDefaultClip();
        }

        private static string SourcePath(TimelineClip clip)
        {
            UnityEngine.Object source = null;
#if KITWRIGHT_ANIMATION
            source = (clip.asset as AnimationPlayableAsset)?.clip;
#endif
#if KITWRIGHT_AUDIO
            if (source == null)
                source = (clip.asset as AudioPlayableAsset)?.clip;
#endif
            return source != null ? AssetDatabase.GetAssetPath(source) : null;
        }

        private static bool IsFiniteFrom(double value, double minimum) =>
            !double.IsNaN(value) && !double.IsInfinity(value) && value >= minimum;
    }
}
