// Copyright (C) KitWright. All rights reserved.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The camera, audio, NavMesh, time and Addressables writers. Each module's reader had a test and
    /// its writers had none. They are grouped here because each needs only a GameObject and a value to
    /// read back, and because the module-gated ones have to be checked the same way whether the package
    /// is installed or not: a tool that answers "not installed" is working, a tool that throws is not.
    /// </summary>
    public sealed class SceneAndModuleToolsTests
    {
        private const string Subject = "KwModuleSubject";

        private float previousTimeScale;
#if KITWRIGHT_AUDIO
        private float previousMasterVolume;
#endif

        [SetUp]
        public void RememberGlobals()
        {
            previousTimeScale = Time.timeScale;
#if KITWRIGHT_AUDIO
            previousMasterVolume = AudioListener.volume;
#endif

            // A NavMeshAgent dropped into a scene with nothing baked logs an error of its own, and a
            // second AudioListener logs a warning. Neither is the tool failing, and the assertions here
            // read returned values rather than the console.
            LogAssert.ignoreFailingMessages = true;
        }

        [TearDown]
        public void RestoreGlobals()
        {
            LogAssert.ignoreFailingMessages = false;
            Time.timeScale = previousTimeScale;
#if KITWRIGHT_AUDIO
            AudioListener.volume = previousMasterVolume;
            Call("stop_clip_preview");
#endif

            var leftover = GameObject.Find(Subject);
            if (leftover != null)
                Object.DestroyImmediate(leftover);
        }

        // A tool from an optional module either does the thing or says the module is missing. Both are
        // the tool working; a crash code is not, and that is what this distinguishes.
        private static void AnsweredOrSaidNotInstalled(string tool, params string[] pairs)
        {
            var answer = Call(tool, pairs);
            var code = (string)answer["code"];

            Assert.IsFalse(
                code == "FUNCTION_FAILED" || code == "FUNCTION_INVOKE_ERROR",
                $"{tool} threw out of its own body: {answer}");
        }

        private static GameObject CreateSubject()
        {
            var subject = new GameObject(Subject);
            return subject;
        }

        [Test]
        public void CameraProjectionSettingsAndCullingMaskAllLandOnTheCamera()
        {
            var subject = CreateSubject();
            var camera = subject.AddComponent<Camera>();

            Ok("set_camera_projection", "projection", "orthographic", "size", "7.5", "game_object_name", Subject);
            Assert.IsTrue(camera.orthographic);
            Assert.AreEqual(7.5f, camera.orthographicSize, 0.001f);

            Ok("set_camera_projection", "projection", "perspective", "size", "42", "game_object_name", Subject);
            Assert.IsFalse(camera.orthographic);
            Assert.AreEqual(42f, camera.fieldOfView, 0.001f);
            Refused("set_camera_projection", "projection", "isometric", "game_object_name", Subject);

            Ok("set_camera_settings",
                "game_object_name", Subject, "near", "0.05", "far", "500",
                "background_color", "0,0,1,1", "clear_flags", "SolidColor");
            Assert.AreEqual(0.05f, camera.nearClipPlane, 0.001f);
            Assert.AreEqual(500f, camera.farClipPlane, 0.001f);
            Assert.AreEqual(CameraClearFlags.SolidColor, camera.clearFlags);
            Assert.AreEqual(Color.blue, camera.backgroundColor);

            Ok("set_camera_culling_mask", "layers", "Default", "game_object_name", Subject);
            Assert.AreEqual(1 << 0, camera.cullingMask, "'set' replaces the mask with exactly the layers named.");

            Ok("set_camera_culling_mask", "layers", "UI", "game_object_name", Subject, "mode", "add");
            Assert.AreEqual((1 << 0) | (1 << 5), camera.cullingMask);

            Ok("set_camera_culling_mask", "layers", "Default", "game_object_name", Subject, "mode", "remove");
            Assert.AreEqual(1 << 5, camera.cullingMask);
        }

#if KITWRIGHT_AUDIO
        [Test]
        public void AudioSourceAndListenerAreAddedWithTheValuesAskedFor()
        {
            var subject = CreateSubject();

            Ok("add_audio_source", "target", Subject, "volume", "0.25", "pitch", "1.5", "loop", "true");
            var source = subject.GetComponent<AudioSource>();
            Assert.IsNotNull(source, "add_audio_source should have added the component.");
            Assert.AreEqual(0.25f, source.volume, 0.001f);
            Assert.AreEqual(1.5f, source.pitch, 0.001f);
            Assert.IsTrue(source.loop);

            Ok("get_audio_source_info", "target", Subject);

            // The scene already has a listener on its camera, so a second one has to be asked for.
            var listener = Call("add_audio_listener", "target", Subject);
            if ((bool)listener["success"] != true)
                Ok("add_audio_listener", "target", Subject, "allow_multiple", "true");

            Assert.IsNotNull(subject.GetComponent<AudioListener>());
        }

        // The clip and the mixer group used to be resolved after Undo.AddComponent, so a path typo
        // answered CLIP_NOT_FOUND with a bare AudioSource left on an object that had none - a change
        // the caller was told had failed.
        [Test]
        public void AnAudioSourceRefusedForItsClipIsNotAddedAtAll()
        {
            var subject = CreateSubject();

            Assert.AreEqual("CLIP_NOT_FOUND",
                Code("add_audio_source", "target", Subject, "clip", "Assets/__KitWrightNoSuchClip.wav"));
            Assert.IsNull(subject.GetComponent<AudioSource>(),
                "A refused add must not leave the component behind.");

            Assert.AreEqual("MIXER_GROUP_NOT_FOUND",
                Code("add_audio_source", "target", Subject, "mixer_group", "Assets/__KitWrightNoSuchMixer.mixer/Master"));
            Assert.IsNull(subject.GetComponent<AudioSource>());

            // The retry a caller would actually make still works.
            Ok("add_audio_source", "target", Subject, "volume", "0.5");
            Assert.IsNotNull(subject.GetComponent<AudioSource>());
        }

        [Test]
        public void GlobalAudioSetsTheMasterVolumeAndThePreviewRefusesAClipThatIsNotThere()
        {
            Ok("set_global_audio", "volume", "0.3");
            Assert.AreEqual(0.3f, AudioListener.volume, 0.001f);

            Refused("play_clip_preview", "clip", "Assets/__KitWrightNoSuchClip.wav");

            // Stopping when nothing is playing is a no-op, not a failure: an agent that lost track of
            // what it started must be able to say "stop" without handling an error.
            Ok("stop_clip_preview");
        }
#endif

#if KITWRIGHT_AI
        [Test]
        public void NavMeshAgentsAndObstaclesAreAddedAndADestinationNeedsAnAgent()
        {
            var subject = CreateSubject();

            AnsweredOrSaidNotInstalled("add_nav_mesh_agent", "target", Subject, "speed", "6", "radius", "0.4");
            var agent = subject.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent == null)
                Assert.Ignore("The AI module is not installed, so there is no agent to check.");

            Assert.AreEqual(6f, agent.speed, 0.001f);
            Assert.AreEqual(0.4f, agent.radius, 0.001f);

            var obstacle = CreateSubject();
            obstacle.name = Subject + "Obstacle";
            try
            {
                Ok("add_nav_mesh_obstacle", "target", obstacle.name, "carving", "true");
                Assert.IsTrue(obstacle.GetComponent<UnityEngine.AI.NavMeshObstacle>().carving);

                // No baked NavMesh in this scene, so the destination cannot be reached - but the tool
                // has to say which of the two it is, not throw.
                AnsweredOrSaidNotInstalled("set_agent_destination", "target", Subject, "destination", "1,0,1");
                Refused("set_agent_destination", "target", obstacle.name, "destination", "1,0,1");
            }
            finally
            {
                Object.DestroyImmediate(obstacle);
            }
        }
#endif

        [Test]
        public void TimeScaleIsWrittenAndTheFrameStepperNeedsPlayMode()
        {
            Ok("set_time_scale", "scale", "0.25");
            Assert.AreEqual(0.25f, Time.timeScale, 0.001f);

            Ok("set_time_scale", "scale", "1");
            Assert.AreEqual(1f, Time.timeScale, 0.001f);

            // A scale is a float, so the invoker refuses a value that is not one instead of leaving the
            // game running at zero.
            Refused("set_time_scale", "scale", "fast");

            // The same code the input and NavMesh tools answer with. These two used to say
            // NOT_IN_PLAY_MODE instead, so a client branching on the code had to know both.
            Assert.AreEqual("PLAY_MODE_REQUIRED", Code("set_paused", "paused", "true"));
            Assert.AreEqual("PLAY_MODE_REQUIRED", Code("step_frame"));
        }

        [Test]
        public void ModifyBuildScenesRefusesASceneThatIsNotThere()
        {
            Refused("modify_build_scenes", "action", "add", "scene_path", "Assets/__KitWrightNoSuchScene.unity");
            Refused("modify_build_scenes", "action", "sideways", "scene_path", "Assets/__KitWrightNoSuchScene.unity");

            // Deliberately no success path: adding a scene rewrites EditorBuildSettings, which is the
            // project's own build order, and putting it back is not something a test should be trusted
            // with. get_build_settings covers the read.
            Ok("get_build_settings");
        }

        [Test]
        public void TheAddressableToolsAnswerWhetherOrNotThePackageIsInstalled()
        {
            const string path = "Assets/__KitWrightNoSuchAsset.prefab";

            AnsweredOrSaidNotInstalled("mark_addressable", "path", path);
            AnsweredOrSaidNotInstalled("set_addressable_address", "path", path, "address", "kw/test");
            AnsweredOrSaidNotInstalled("set_addressable_label", "path", path, "label", "kw", "add", "true");
            AnsweredOrSaidNotInstalled("unmark_addressable", "path", path);
        }
    }
}
