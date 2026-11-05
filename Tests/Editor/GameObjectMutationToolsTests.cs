// Copyright (C) KitWright. Licensed under MIT.

using NUnit.Framework;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The GameObject tools that change something, called the way a client calls them - every argument
    /// a string, through FunctionInvoker. Creating and deleting were covered; renaming, duplicating,
    /// activating, tagging and taking a component back off were not, and those are the ones an agent
    /// reaches for while repairing a scene it half-built.
    /// </summary>
    public sealed class GameObjectMutationToolsTests
    {
        private const string Subject = "KwMutationSubject";
        private const string Copy = "KwMutationCopy";
        private const string Renamed = "KwMutationRenamed";
        private const string Orphan = "KwOrphanCandidate";

        private GameObject subject;

        [SetUp]
        public void CreateSubject()
        {
            subject = new GameObject(Subject);
            // A Camera rather than a collider: it has an `enabled` flag to toggle and lives in the core
            // module, so this file does not need a #if for a physics package it is not testing.
            subject.AddComponent<Camera>();
        }

        [TearDown]
        public void DestroyEverythingCreated()
        {
            foreach (var name in new[] { Subject, Copy, Renamed, Orphan })
            {
                var leftover = GameObject.Find(name);
                if (leftover != null)
                    Object.DestroyImmediate(leftover);
            }

            subject = null;
        }

        [Test]
        public void RenameChangesTheNameAndSaysWhenTheTargetIsNotThere()
        {
            Ok("rename_game_object", "target", Subject, "new_name", Renamed);
            Assert.AreEqual(Renamed, subject.name);
            Assert.IsNull(GameObject.Find(Subject));

            Refused("rename_game_object", "target", "KwNothingCalledThis", "new_name", "whatever");
        }

        [Test]
        public void DuplicateMakesASecondObjectCarryingTheSameComponents()
        {
            Ok("duplicate_game_object", "target", Subject, "new_name", Copy);

            var copy = GameObject.Find(Copy);
            Assert.IsNotNull(copy, "The duplicate should be in the scene under the name asked for.");
            Assert.AreNotSame(subject, copy);
            Assert.IsNotNull(copy.GetComponent<Camera>(), "A duplicate carries the original's components.");
        }

        [Test]
        public void SetActiveTurnsTheObjectOffAndBackOn()
        {
            Ok("set_active", "target", Subject, "active", "false");
            Assert.IsFalse(subject.activeSelf);

            Ok("set_active", "target", Subject, "active", "true");
            Assert.IsTrue(subject.activeSelf);

            // This used to read anything that was not "true" or "1" as false, so a client sending
            // "True" deactivated the object and was told it worked.
            Refused("set_active", "target", Subject, "active", "maybe");
            Assert.IsTrue(subject.activeSelf);
        }

        [Test]
        public void SetTagAndLayerWritesBothAndRefusesATagThatDoesNotExist()
        {
            Ok("set_tag_and_layer", "target", Subject, "tag", "EditorOnly", "layer", "Ignore Raycast");
            Assert.AreEqual("EditorOnly", subject.tag);
            Assert.AreEqual(LayerMask.NameToLayer("Ignore Raycast"), subject.layer);

            // A call that applies nothing is a refusal, with the reason in data.warnings.
            var layerBefore = subject.layer;
            var refused = Refused("set_tag_and_layer", "target", Subject, "layer", "KwLayerNobodyDefined");

            StringAssert.Contains("KwLayerNobodyDefined", refused["data"]["warnings"].ToString());
            Assert.AreEqual(layerBefore, subject.layer, "An undefined layer must not be applied.");
        }

        [Test]
        public void SetComponentPropertiesEnabledFlipsTheComponentWithoutTouchingTheObject()
        {
            Ok("set_component_properties", "target", Subject, "component", "Camera", "properties", "{\"enabled\": false}");
            Assert.IsFalse(subject.GetComponent<Camera>().enabled);
            Assert.IsTrue(subject.activeSelf, "Disabling a component is not deactivating the object.");

            Ok("set_component_properties", "target", Subject, "component", "Camera", "properties", "{\"enabled\": true}");
            Assert.IsTrue(subject.GetComponent<Camera>().enabled);

            Refused("set_component_properties", "target", Subject, "component", "Rigidbody", "properties", "{\"enabled\": true}");
        }

        [Test]
        public void AddComponentTakesAScriptPathAndRefusesOneThatIsNotThere()
        {
            Assert.AreEqual("SCRIPT_NOT_FOUND",
                Code("add_component", "target", Subject, "component_type", "Assets/KwNoSuchScript.cs"));
        }

        [Test]
        public void CreateGameObjectMakesAPrimitiveWhereItIsAskedFor()
        {
            var made = Ok("create_game_object", "name", Orphan, "primitive", "Cube", "parent", Subject,
                "position", "1,2,3", "scale", "2,2,2");

            var cube = GameObject.Find(Orphan);
            Assert.IsNotNull(cube);
            Assert.AreSame(subject.transform, cube.transform.parent);
            Assert.AreEqual(new Vector3(1f, 2f, 3f), cube.transform.position);
            Assert.AreEqual(new Vector3(2f, 2f, 2f), cube.transform.localScale);
            Assert.IsNotNull(cube.GetComponent<MeshFilter>());
            StringAssert.Contains("MeshRenderer", made["data"]["components"].ToString());

            Assert.AreEqual("UNKNOWN_PRIMITIVE", Code("create_game_object", "name", Copy, "primitive", "Teapot"));
            Assert.IsNull(GameObject.Find(Copy), "A refused create must not leave the object in the scene.");
        }

        [Test]
        public void RemoveComponentTakesItOffAndSaysWhenThereIsNothingToRemove()
        {
            Ok("remove_component", "target", Subject, "component_type", "Camera");
            Assert.IsNull(subject.GetComponent<Camera>());

            Refused("remove_component", "target", Subject, "component_type", "Camera");
            Refused("remove_component", "target", Subject, "component_type", "Transform");
        }

        // The object used to be created before the parent was looked up, so a name nobody owns came
        // back as PARENT_NOT_FOUND with an orphan left at the scene root.
        [Test]
        public void CreateGameObjectRefusedForItsParentLeavesNoOrphanBehind()
        {
            Assert.AreEqual("PARENT_NOT_FOUND",
                Code("create_game_object", "name", Orphan, "parent", "KwParentNobodyCreated"));

            Assert.IsNull(GameObject.Find(Orphan), "A refused create must not leave the object in the scene.");
        }
    }
}
