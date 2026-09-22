// Copyright (C) KitWright. All rights reserved.

using NUnit.Framework;
using UnityEngine;
using static KitWright.Editor.Tests.ToolCall;
#if KITWRIGHT_ANIMATION
using UnityEngine.Animations;
#endif

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// The three leftovers that each add or remove something on a live object: the LOD group builder,
    /// the constraint adder, and the NavMesh wipe. Grouped because each needs one GameObject and a
    /// component to read back.
    /// </summary>
    public sealed class LodConstraintAndNavMeshToolsTests
    {
        private const string Subject = "KwLodSubject";
        private const string Source = "KwLodSource";

        private GameObject subject;
        private GameObject source;

        [SetUp]
        public void CreateSubject()
        {
            subject = new GameObject(Subject);

            var child = new GameObject("KwLodChild");
            child.transform.SetParent(subject.transform, false);
            child.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            child.AddComponent<MeshRenderer>();

            source = new GameObject(Source);
        }

        [TearDown]
        public void DestroySubject()
        {
            if (subject != null)
                Object.DestroyImmediate(subject);
            if (source != null)
                Object.DestroyImmediate(source);

            subject = null;
            source = null;
        }

        [Test]
        public void CreateLodGroupPutsTheChildRenderersOnLod0AndReconfiguresInPlace()
        {
            var made = Ok("create_lod_group", "target", Subject, "levels", "3");

            Assert.AreEqual(3, (int)made["data"]["levels"]);
            Assert.AreEqual(1, (int)made["data"]["lod0RendererCount"], "The child renderer belongs to LOD0.");

            var group = subject.GetComponent<LODGroup>();
            Assert.IsNotNull(group, "create_lod_group should have added the component itself.");
            Assert.AreEqual(3, group.GetLODs().Length);
            Assert.AreEqual(1, group.GetLODs()[0].renderers.Length);

            // Called twice, an agent must get the same component back reconfigured, not a second one.
            Ok("create_lod_group", "target", Subject, "levels", "2");
            Assert.AreEqual(1, subject.GetComponents<LODGroup>().Length);
            Assert.AreEqual(2, subject.GetComponent<LODGroup>().GetLODs().Length);

            Assert.AreEqual(8, (int)Ok("create_lod_group", "target", Subject, "levels", "99")["data"]["levels"],
                "A level count past the maximum clamps instead of failing.");
            Assert.AreEqual("GAME_OBJECT_NOT_FOUND", Code("create_lod_group", "target", "KwNothingCalledThis"));
        }

#if KITWRIGHT_ANIMATION
        [Test]
        public void AddConstraintBindsItsSourceAndLeavesNothingBehindWhenTheSourceIsMissing()
        {
            var added = Ok("add_constraint", "target", Subject, "type", "lookat", "source", Source);

            var constraint = subject.GetComponent<LookAtConstraint>();
            Assert.IsNotNull(constraint, "add_constraint should have added the constraint component.");
            Assert.AreEqual(1, constraint.sourceCount);
            Assert.AreEqual(source.transform, constraint.GetSource(0).sourceTransform);
            Assert.IsTrue(constraint.constraintActive, "activate defaults to true.");
            Assert.IsTrue(constraint.locked);
            Assert.AreEqual("LookAt", (string)added["data"]["type"]);

            Assert.AreEqual("INVALID_CONSTRAINT_TYPE", Code("add_constraint", "target", Subject, "type", "wobble"));

            // The source used to be resolved after the component was added, so a typo left a dead
            // constraint on the object next to an answer that said the call had failed.
            Assert.AreEqual("SOURCE_NOT_FOUND",
                Code("add_constraint", "target", Source, "type", "position", "source", "KwNothingCalledThis"));
            Assert.IsNull(source.GetComponent<PositionConstraint>(),
                "A refused add_constraint must not leave a constraint behind.");
        }
#endif

#if KITWRIGHT_AI
        [Test]
        public void ClearingTheSceneNavMeshLeavesNothingForTheReaderToFind()
        {
            if ((bool)Ok("get_nav_mesh_info")["data"]["hasNavMesh"])
                Assert.Ignore("The open scene has a baked NavMesh, and throwing away someone's bake is not this test's job.");

            Ok("clear_nav_mesh");
            Assert.IsFalse((bool)Ok("get_nav_mesh_info")["data"]["hasNavMesh"]);
        }
#endif
    }
}
