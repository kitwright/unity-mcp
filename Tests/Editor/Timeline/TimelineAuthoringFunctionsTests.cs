// Copyright (C) KitWright. Licensed under MIT.

#if KITWRIGHT_TIMELINE
using System.IO;
using KitWright.Editor.Tools.Builtins;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Timeline;

namespace KitWright.Editor.Tests
{
    public sealed class TimelineAuthoringFunctionsTests
    {
        private const string Folder = "Assets/__KwTimelineTests";
        private const string TimelinePath = Folder + "/Cut.playable";
        private const string ClipPath = Folder + "/Move.anim";

        [SetUp]
        public void SetUp() => Directory.CreateDirectory(Folder);

        [TearDown]
        public void TearDown()
        {
            AssetDatabase.DeleteAsset(Folder);
            AssetDatabase.Refresh();
        }

        private static JObject Json(object result) => JObject.FromObject(result);

        private static JObject Success(object result)
        {
            var json = Json(result);
            Assert.IsTrue(json.Value<bool>("success"), json.ToString());
            return json;
        }

        private static void Error(object result, string code)
        {
            var json = Json(result);
            Assert.IsFalse(json.Value<bool>("success"), json.ToString());
            Assert.AreEqual(code, json.Value<string>("code"));
        }

        [Test]
        public void CreateTimeline_SetsTheFrameRate_AndRefusesAnExistingAssetOrAWrongExtension()
        {
            Success(TimelineAuthoringFunctions.CreateTimeline(TimelinePath, 30));

            Assert.AreEqual(30d, AssetDatabase.LoadAssetAtPath<TimelineAsset>(TimelinePath).editorSettings.frameRate, 0.0001);
            Error(TimelineAuthoringFunctions.CreateTimeline(TimelinePath), "TIMELINE_EXISTS");
            Error(TimelineAuthoringFunctions.CreateTimeline(Folder + "/Cut.asset"), "INVALID_PATH");
        }

#if KITWRIGHT_ANIMATION
        [Test]
        public void AddTimelineClip_PutsTheAnimationClipOnItsTrack_AndGetTimelineReadsItBack()
        {
            var animation = new AnimationClip { name = "Move" };
            AssetDatabase.CreateAsset(animation, ClipPath);
            Success(TimelineAuthoringFunctions.CreateTimeline(TimelinePath));
            Success(TimelineAuthoringFunctions.AddTimelineTrack(TimelinePath, "Group", "Actors"));
            Success(TimelineAuthoringFunctions.AddTimelineTrack(TimelinePath, "Animation", "Hero", "Actors"));

            Success(TimelineAuthoringFunctions.AddTimelineClip(TimelinePath, "Hero", 1, 2, ClipPath));

            var group = Success(TimelineAuthoringFunctions.GetTimeline(TimelinePath))["data"]["tracks"][0];
            Assert.AreEqual("Actors", (string)group["name"]);
            var clip = group["children"][0]["clips"][0];
            Assert.AreEqual(1d, (double)clip["start"], 0.0001);
            Assert.AreEqual(2d, (double)clip["duration"], 0.0001);
            Assert.AreEqual(ClipPath, (string)clip["source"]);
        }
#endif

        [Test]
        public void AddTimelineClip_RefusesAnAnimationTrackWithoutAClip_AndAGroupTrackAtAll()
        {
            Success(TimelineAuthoringFunctions.CreateTimeline(TimelinePath));
            Success(TimelineAuthoringFunctions.AddTimelineTrack(TimelinePath, "Animation", "Hero"));
            Success(TimelineAuthoringFunctions.AddTimelineTrack(TimelinePath, "Group", "Actors"));

            Error(TimelineAuthoringFunctions.AddTimelineClip(TimelinePath, "Hero", 0), "ASSET_REQUIRED");
            Error(TimelineAuthoringFunctions.AddTimelineClip(TimelinePath, "Actors", 0), "CLIP_NOT_SUPPORTED");
            Error(TimelineAuthoringFunctions.AddTimelineClip(TimelinePath, "Nobody", 0), "TRACK_NOT_FOUND");
        }
    }
}
#endif
