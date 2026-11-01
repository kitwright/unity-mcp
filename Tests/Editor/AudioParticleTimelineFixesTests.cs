// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KitWright.Editor.Tools.Builtins;
using KitWright.Editor.Tools.Helpers;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class AudioParticleTimelineFixesTests
    {
        private readonly List<GameObject> _made = new List<GameObject>();
        private string _folder;
        private float _masterVolume;
        private bool _paused;

        [SetUp]
        public void RememberGlobals()
        {
#if KITWRIGHT_AUDIO
            _masterVolume = AudioListener.volume;
            _paused = AudioListener.pause;
#endif
        }

        [TearDown]
        public void Cleanup()
        {
#if KITWRIGHT_AUDIO
            AudioListener.volume = _masterVolume;
            AudioListener.pause = _paused;
#endif
            foreach (var go in _made)
            {
                if (go != null)
                    UnityEngine.Object.DestroyImmediate(go);
            }
            _made.Clear();

            if (_folder != null && AssetDatabase.IsValidFolder(_folder))
                AssetDatabase.DeleteAsset(_folder);
            _folder = null;
        }

        private static string Unique(string stem) => stem + "_" + Guid.NewGuid().ToString("N");

        private GameObject Make(string name, params Type[] components)
        {
            var go = new GameObject(name, components);
            _made.Add(go);
            return go;
        }

#if KITWRIGHT_AUDIO
        [TestCase("Assets/Audio/Main.mixer", "Assets/Audio/Main.mixer", null)]
        [TestCase("Assets/Audio/Main.mixer/SFX", "Assets/Audio/Main.mixer", "SFX")]
        [TestCase("Assets/Audio/Main.mixer/Master/Music", "Assets/Audio/Main.mixer", "Master/Music")]
        [TestCase("Assets/Audio/Main.MIXER/SFX", "Assets/Audio/Main.MIXER", "SFX")]
        [TestCase("Assets/Audio/Main.mixer/", "Assets/Audio/Main.mixer", null)]
        [TestCase("Packages/com.kw.audio/Main.mixer/SFX", "Packages/com.kw.audio/Main.mixer", "SFX")]
        [TestCase("Assets/a.mixers/b.mixer/SFX", "Assets/a.mixers/b.mixer", "SFX")]
        [TestCase("Assets/Audio/Main", "Assets/Audio/Main", null)]
        public void SplitMixerSpec_FindsTheEndOfTheAssetPathNotTheFirstSlash(string spec, string path, string group)
        {
            AudioFunctions.SplitMixerSpec(spec, out var assetPath, out var groupName);
            Assert.AreEqual(path, assetPath);
            Assert.AreEqual(group, groupName);
        }

        [Test]
        public void PickMixerGroup_PrefersTheExactNameAndRefusesAnAmbiguousOne()
        {
            Assert.AreEqual(1, AudioFunctions.PickMixerGroup(new[] { "Master", "Music", "UIMusic" }, "Music"));
            Assert.AreEqual(1, AudioFunctions.PickMixerGroup(new[] { "UIMusic", "Music" }, "Music"));
            Assert.AreEqual(1, AudioFunctions.PickMixerGroup(new[] { "Master", "SFX", "Footsteps" }, "Master/SFX"));
            Assert.AreEqual(0, AudioFunctions.PickMixerGroup(new[] { "Master" }, null));
            Assert.AreEqual(0, AudioFunctions.PickMixerGroup(new[] { "Music" }, "Mus"));
            Assert.AreEqual(-1, AudioFunctions.PickMixerGroup(new string[0], "Music"));
            Assert.AreEqual(AudioFunctions.AmbiguousGroup, AudioFunctions.PickMixerGroup(new[] { "Master", "Music", "UIMusic" }, "Mus"));
            Assert.AreEqual(AudioFunctions.AmbiguousGroup, AudioFunctions.PickMixerGroup(new[] { "Music", "Music" }, "Music"));
        }

        [Test]
        public void PickMixerGroupByPath_PrefersTheFullPathAndRefusesWhatStaysAmbiguous()
        {
            var paths = new[] { "Master", "Master/Music", "Master/Music/Music", "Master/SFX" };

            Assert.AreEqual(0, AudioFunctions.PickMixerGroupByPath(paths, null));
            Assert.AreEqual(0, AudioFunctions.PickMixerGroupByPath(paths, "Master"));
            Assert.AreEqual(1, AudioFunctions.PickMixerGroupByPath(paths, "Master/Music"));
            Assert.AreEqual(2, AudioFunctions.PickMixerGroupByPath(paths, "Master/Music/Music"));
            Assert.AreEqual(2, AudioFunctions.PickMixerGroupByPath(paths, "Music/Music"));
            Assert.AreEqual(3, AudioFunctions.PickMixerGroupByPath(paths, "SFX"));
            Assert.AreEqual(AudioFunctions.AmbiguousGroup, AudioFunctions.PickMixerGroupByPath(paths, "Music"));
            Assert.AreEqual(-1, AudioFunctions.PickMixerGroupByPath(paths, "Mus"));
            Assert.AreEqual(-1, AudioFunctions.PickMixerGroupByPath(new string[0], "Music"));
        }

        [Test]
        public void AddAudioSource_ResolvesNestedGroupsThatShareAName_ByTheirFullPath()
        {
            var mixerPath = CreateMixerAsset(nestedMusicGroups: true);
            var mixer = AssetDatabase.LoadAssetAtPath<UnityEngine.Audio.AudioMixer>(mixerPath);
            var groups = mixer.FindMatchingGroups(string.Empty);
            var paths = AudioFunctions.GroupPaths(groups);
            CollectionAssert.AreEquivalent(new[] { "Master", "Master/Music", "Master/Music/Music" }, paths);

            var subject = Make(Unique("KwNestedMixerSubject"));

            Ok("add_audio_source", "target", subject.name, "mixer_group", mixerPath + "/Master/Music");
            Assert.AreSame(groups[Array.IndexOf(paths, "Master/Music")], subject.GetComponent<AudioSource>().outputAudioMixerGroup);

            UnityEngine.Object.DestroyImmediate(subject.GetComponent<AudioSource>());
            Ok("add_audio_source", "target", subject.name, "mixer_group", mixerPath + "/Master/Music/Music");
            Assert.AreSame(groups[Array.IndexOf(paths, "Master/Music/Music")], subject.GetComponent<AudioSource>().outputAudioMixerGroup);

            UnityEngine.Object.DestroyImmediate(subject.GetComponent<AudioSource>());
            Assert.AreEqual("MIXER_GROUP_AMBIGUOUS",
                Code("add_audio_source", "target", subject.name, "mixer_group", mixerPath + "/Music"));
            Assert.IsNull(subject.GetComponent<AudioSource>(), "A refused mixer group must not leave the component behind.");
        }

        [Test]
        public void AddAudioSource_ResolvesAMixerGroupFromARealMixerAssetPath()
        {
            var mixerPath = CreateMixerAsset();
            var subject = Make(Unique("KwMixerSubject"));

            Ok("add_audio_source", "target", subject.name, "mixer_group", mixerPath);
            var source = subject.GetComponent<AudioSource>();
            Assert.IsNotNull(source);
            Assert.AreEqual("Master", source.outputAudioMixerGroup.name);

            UnityEngine.Object.DestroyImmediate(source);
            Ok("add_audio_source", "target", subject.name, "mixer_group", mixerPath + "/Master");
            Assert.AreEqual("Master", subject.GetComponent<AudioSource>().outputAudioMixerGroup.name);

            UnityEngine.Object.DestroyImmediate(subject.GetComponent<AudioSource>());
            Assert.AreEqual("MIXER_GROUP_NOT_FOUND",
                Code("add_audio_source", "target", subject.name, "mixer_group", mixerPath + "/NoSuchGroup"));
            Assert.IsNull(subject.GetComponent<AudioSource>(), "A refused mixer group must not leave the component behind.");
        }

        private string CreateMixerAsset(bool nestedMusicGroups = false)
        {
            _folder = "Assets/" + Unique("__KitWrightAudioParticleTimelineTests");
            AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));

            var controller = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.Audio.AudioMixerController"))
                .FirstOrDefault(t => t != null);
            var create = controller?.GetMethod("CreateMixerControllerAtPath",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
            if (create == null)
                Assert.Ignore("AudioMixerController.CreateMixerControllerAtPath is not available in this Unity version.");

            var path = _folder + "/Test.mixer";
            try
            {
                var controllerInstance = create.Invoke(null, new object[] { path });
                if (nestedMusicGroups)
                    AddNestedMusicGroups(controller, controllerInstance);
            }
            catch (TargetInvocationException ex)
            {
                Assert.Ignore($"Could not create a test mixer: {ex.InnerException?.Message}");
            }

            AssetDatabase.ImportAsset(path);
            return path;
        }

        // Master/Music and Master/Music/Music: FindMatchingGroups("Master/Music") returns both.
        private static void AddNestedMusicGroups(Type controllerType, object controller)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var master = controllerType.GetProperty("masterGroup", flags)?.GetValue(controller);
            var createGroup = controllerType.GetMethod("CreateNewGroup", flags);
            var addChild = controllerType.GetMethod("AddChildToParent", flags);
            if (master == null || createGroup == null || addChild == null)
                Assert.Ignore("The AudioMixerController group API is not available in this Unity version.");

            var music = createGroup.Invoke(controller, new object[] { "Music", false });
            addChild.Invoke(controller, new[] { music, master });
            var inner = createGroup.Invoke(controller, new object[] { "Music", false });
            addChild.Invoke(controller, new[] { inner, music });
            EditorUtility.SetDirty((UnityEngine.Object)controller);
            AssetDatabase.SaveAssets();
        }

        [Test]
        public void AddAudioSource_OnAnObjectThatAlreadyHasOneSaysNothingChanged()
        {
            var subject = Make(Unique("KwSourceSubject"));

            Ok("add_audio_source", "target", subject.name);
            var again = Ok("add_audio_source", "target", subject.name);
            StringAssert.Contains("nothing changed", (string)again["message"]);

            var configured = Ok("add_audio_source", "target", subject.name, "volume", "0.4");
            StringAssert.Contains("configured", (string)configured["message"]);
            Assert.AreEqual(0.4f, subject.GetComponent<AudioSource>().volume, 0.001f);
        }

        [Test]
        public void SetGlobalAudio_WithNothingToSetIsRefusedAndChangesNothing()
        {
            AudioListener.volume = 0.6f;

            Assert.AreEqual("MISSING_PARAM", Code("set_global_audio"));
            Assert.AreEqual(0.6f, AudioListener.volume, 0.001f);

            Ok("set_global_audio", "volume", "0.2");
            Assert.AreEqual(0.2f, AudioListener.volume, 0.001f);
        }

        [Test]
        public void AddAudioListener_AnObjectThatAlreadyHasOneAnswersAlreadyPresentWhateverElseIsLoaded()
        {
            var first = Make(Unique("KwListenerA"), typeof(AudioListener));
            Make(Unique("KwListenerB"), typeof(AudioListener));

            for (var i = 0; i < 5; i++)
            {
                var answer = Ok("add_audio_listener", "target", first.name);
                StringAssert.Contains("already has", (string)answer["message"]);
            }
        }

        [Test]
        public void AddAudioListener_OnlyConsidersListenersInTheTargetsOwnScene()
        {
            var originalSetup = EditorSceneManager.GetSceneManagerSetup();
            var canRestore = originalSetup.Length > 0 &&
                             originalSetup.All(scene => !string.IsNullOrEmpty(scene.path) && System.IO.File.Exists(scene.path));
            if (!Application.isBatchMode && !canRestore)
                Assert.Ignore("Skipping: the interactive editor has unsaved untitled scenes, and replacing them is not ours to do.");
            HierarchyFunctionsTests.SkipIfAnySceneDirty();

            _folder = "Assets/__KitWrightAudioSceneTests_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", _folder.Substring("Assets/".Length));

            try
            {
                var primary = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                Assert.IsTrue(EditorSceneManager.SaveScene(primary, _folder + "/Primary.unity"));
                Make(Unique("KwListenerHolder"), typeof(AudioListener));

                var extra = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
                SceneManager.SetActiveScene(primary);

                var first = Make(Unique("KwListenerFirst"));
                SceneManager.MoveGameObjectToScene(first, extra);
                var second = Make(Unique("KwListenerSecond"));
                SceneManager.MoveGameObjectToScene(second, extra);

                var added = Ok("add_audio_listener", "target", ObjectIdCodec.GetSerializableId(first));
                Assert.IsNotNull(first.GetComponent<AudioListener>(),
                    "A listener in another scene must not block this scene's first one.");
                StringAssert.Contains("Another loaded scene already has one", (string)added["message"]);

                Assert.AreEqual("LISTENER_EXISTS", Code("add_audio_listener", "target", ObjectIdCodec.GetSerializableId(second)));
                Assert.IsNull(second.GetComponent<AudioListener>());
            }
            finally
            {
                HierarchyFunctionsTests.SettleDirtyScenes(_folder);
                if (canRestore)
                    EditorSceneManager.RestoreSceneManagerSetup(originalSetup);
                else if (Application.isBatchMode)
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
        }
#endif

#if KITWRIGHT_PARTICLES
        private GameObject MakeContainer(out GameObject fire, out GameObject smoke)
        {
            var root = Make(Unique("KwFxRoot"));
            fire = Make("KwFire", typeof(ParticleSystem));
            smoke = Make("KwSmoke", typeof(ParticleSystem));
            var embers = Make("KwEmbers", typeof(ParticleSystem));
            fire.transform.SetParent(root.transform);
            smoke.transform.SetParent(root.transform);
            embers.transform.SetParent(smoke.transform);
            return root;
        }

        [Test]
        public void ParticleControl_OnAContainerActsOnEveryTopLevelSystem()
        {
            var root = MakeContainer(out var fire, out var smoke);

            var answer = Ok("particle_control", "target", root.name, "action", "simulate", "time", "0.3");

            Assert.AreEqual(0.3f, fire.GetComponent<ParticleSystem>().time, 0.05f);
            Assert.AreEqual(0.3f, smoke.GetComponent<ParticleSystem>().time, 0.05f);

            var data = answer["data"];
            Assert.AreEqual(2, ((JArray)data["systems"]).Count, "Embers is nested under Smoke, so it is not a top-level system.");
            Assert.AreEqual(root.name, (string)data["gameObject"]);
            Assert.IsTrue(JToken.DeepEquals(JToken.FromObject(ObjectIdCodec.GetSerializableId(root)), data["instanceId"]),
                "instanceId must identify the same object as gameObject.");
        }

        [Test]
        public void ParticleControl_ASystemOnTheTargetItselfIsTheOnlyRoot()
        {
            var root = Make(Unique("KwFxOwn"), typeof(ParticleSystem));
            var child = Make("KwFxOwnChild", typeof(ParticleSystem));
            child.transform.SetParent(root.transform);

            var answer = Ok("particle_control", "target", root.name, "action", "simulate", "time", "0.2");

            Assert.AreEqual(1, ((JArray)answer["data"]["systems"]).Count);
        }

        [Test]
        public void ParticleControl_RestartRewindsASystemLeftPausedBySimulate()
        {
            var go = Make(Unique("KwFxRestart"), typeof(ParticleSystem));
            var ps = go.GetComponent<ParticleSystem>();

            Ok("particle_control", "target", go.name, "action", "simulate", "time", "0.5");
            Assume.That(ps.time, Is.GreaterThan(0.25f), "Simulate did not leave the system past t=0.25.");

            Ok("particle_control", "target", go.name, "action", "restart");

            Assert.Less(ps.time, 0.25f);
        }

        [TestCase("-5")]
        [TestCase("120")]
        [TestCase("60.5")]
        public void ParticleControl_ATimeOutsideZeroToSixtyIsRefusedNotClamped(string time)
        {
            var go = Make(Unique("KwFxRange"), typeof(ParticleSystem));
            var ps = go.GetComponent<ParticleSystem>();
            Ok("particle_control", "target", go.name, "action", "simulate", "time", "0.2");
            var before = ps.time;

            Assert.AreEqual("INVALID_PARAM", Code("particle_control", "target", go.name, "action", "simulate", "time", time));
            Assert.AreEqual(before, ps.time, 0.001f, "A refused simulate must not move the system.");
        }

        [Test]
        public void ParticleControl_TheEndsOfTheRangeAreAccepted()
        {
            var go = Make(Unique("KwFxEnds"), typeof(ParticleSystem));

            Ok("particle_control", "target", go.name, "action", "simulate", "time", "0");
            Ok("particle_control", "target", go.name, "action", "simulate", "time", "60");
        }

        [Test]
        public void ParticleControl_OnAContainerSkipsInactiveSystemsAndNamesThem()
        {
            var root = MakeContainer(out var fire, out _);
            fire.SetActive(false);

            var answer = Ok("particle_control", "target", root.name, "action", "simulate", "time", "0.3");

            var data = answer["data"];
            CollectionAssert.AreEqual(new[] { "KwSmoke" }, ((JArray)data["systems"]).Select(s => (string)s["name"]));
            CollectionAssert.AreEqual(new[] { "KwFire" }, ((JArray)data["inactiveSkipped"]).Select(n => (string)n));
            StringAssert.Contains("1 inactive", (string)answer["message"]);
        }

        [Test]
        public void ParticleControl_AContainerWhoseSystemsAreAllInactiveSaysSoInsteadOfActingOnThem()
        {
            var root = Make(Unique("KwFxAllOff"));
            var child = Make("KwFxOff", typeof(ParticleSystem));
            child.transform.SetParent(root.transform);
            child.SetActive(false);

            var answer = Refused("particle_control", "target", root.name);

            Assert.AreEqual("PARTICLE_SYSTEM_NOT_FOUND", (string)answer["code"]);
            StringAssert.Contains("inactive", (string)answer["data"]["hint"]);
            Assert.AreEqual("KwFxOff", (string)answer["data"]["inactiveSkipped"][0]);
        }
#endif

        private static Type FindPlayableDirectorType() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEngine.Playables.PlayableDirector"))
                .FirstOrDefault(t => t != null);

        [Test]
        public void DirectorEvaluate_ADirectorWithNoPlayableAssetIsRefused()
        {
            var type = FindPlayableDirectorType();
            if (type == null)
                Assert.Ignore("The director module is not available.");

            var go = Make(Unique("KwDirector"), type);

            var answer = Refused("director_evaluate", "target", go.name, "time", "1");

            Assert.AreEqual("NO_PLAYABLE_ASSET", (string)answer["code"]);
        }

        [Test]
        public void DirectorEvaluate_NotFoundHintDoesNotBlameTheTimelinePackage()
        {
            var go = Make(Unique("KwNoDirector"));

            var answer = Refused("director_evaluate", "target", go.name);

            Assert.AreEqual("PLAYABLE_DIRECTOR_NOT_FOUND", (string)answer["code"]);
            Assert.That((string)answer["data"]["hint"], Does.Not.Contain("com.unity.timeline"));
        }

        [TestCase("1e300")]
        [TestCase("-5")]
        [TestCase("1000001")]
        public void DirectorEvaluate_ATimeOutsideZeroToAMillionIsRefusedBeforeAnythingElseIsChecked(string time)
        {
            var type = FindPlayableDirectorType();
            if (type == null)
                Assert.Ignore("The director module is not available.");

            var go = Make(Unique("KwDirectorTime"), type);

            var answer = Refused("director_evaluate", "target", go.name, "time", time);

            Assert.AreEqual("INVALID_PARAM", (string)answer["code"]);
            Assert.AreEqual("time", (string)answer["data"]["param"]);
        }

        [TestCase("0")]
        [TestCase("1000000")]
        public void DirectorEvaluate_TheEndsOfTheTimeRangePassTheCheck(string time)
        {
            var type = FindPlayableDirectorType();
            if (type == null)
                Assert.Ignore("The director module is not available.");

            var go = Make(Unique("KwDirectorEnds"), type);

            Assert.AreEqual("NO_PLAYABLE_ASSET", Code("director_evaluate", "target", go.name, "time", time));
        }
    }
}
