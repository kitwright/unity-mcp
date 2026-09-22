// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_INPUTSYSTEM
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine.InputSystem;

namespace KitWright.Editor.Tests
{
    public sealed class InputActionAssetFunctionsTests
    {
        [Test]
        public void IsInputActionsPath_AcceptsExtension()
        {
            Assert.IsTrue(InputActionAssetFunctions.IsInputActionsPath("Assets/Input/X.inputactions"));
            Assert.IsTrue(InputActionAssetFunctions.IsInputActionsPath("Assets/X.INPUTACTIONS"));
        }

        [Test]
        public void IsInputActionsPath_RejectsOther()
        {
            Assert.IsFalse(InputActionAssetFunctions.IsInputActionsPath("Assets/X.asset"));
            Assert.IsFalse(InputActionAssetFunctions.IsInputActionsPath(null));
            Assert.IsFalse(InputActionAssetFunctions.IsInputActionsPath(""));
        }

        [Test]
        public void ResolveActionType_KnownTypes()
        {
            Assert.AreEqual(InputActionType.Value, InputActionAssetFunctions.ResolveActionType("value"));
            Assert.AreEqual(InputActionType.PassThrough, InputActionAssetFunctions.ResolveActionType("passthrough"));
            Assert.AreEqual(InputActionType.Button, InputActionAssetFunctions.ResolveActionType("button"));
        }

        [Test]
        public void ResolveActionType_UnknownDefaultsButton()
        {
            Assert.AreEqual(InputActionType.Button, InputActionAssetFunctions.ResolveActionType("wobble"));
            Assert.AreEqual(InputActionType.Button, InputActionAssetFunctions.ResolveActionType(null));
        }

        [Test]
        public void ParseCompositeParts_ParsesPairs()
        {
            var parts = InputActionAssetFunctions.ParseCompositeParts("Up=<Keyboard>/w;Down=<Keyboard>/s");
            Assert.AreEqual(2, parts.Count);
            Assert.AreEqual("Up", parts[0].Key);
            Assert.AreEqual("<Keyboard>/w", parts[0].Value);
            Assert.AreEqual("Down", parts[1].Key);
        }

        [Test]
        public void ParseCompositeParts_SkipsMalformed()
        {
            var parts = InputActionAssetFunctions.ParseCompositeParts("Up=<Keyboard>/w;garbage;=nokey;noval=");
            Assert.AreEqual(1, parts.Count);
            Assert.AreEqual("Up", parts[0].Key);
        }

        [Test]
        public void ParseCompositeParts_NullOrEmpty()
        {
            Assert.AreEqual(0, InputActionAssetFunctions.ParseCompositeParts(null).Count);
            Assert.AreEqual(0, InputActionAssetFunctions.ParseCompositeParts("   ").Count);
        }
    }
}
#endif
