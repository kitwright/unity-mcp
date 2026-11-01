// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace KitWright.Editor.Tests
{
    public sealed class LightingNotesTests
    {
        [Test]
        public void AnAmbientColorOutsideFlatModeIsSaidToBeStoredNotShown()
        {
            var notes = LightingFunctions.EffectNotes(true, AmbientMode.Skybox, false, FogMode.Exponential);

            StringAssert.Contains("only shows with ambient_mode Flat", notes);
            Assert.IsEmpty(LightingFunctions.EffectNotes(true, AmbientMode.Flat, false, FogMode.Exponential));
            Assert.IsEmpty(LightingFunctions.EffectNotes(false, AmbientMode.Skybox, false, FogMode.Linear));
        }

        [Test]
        public void AFogDensityUnderLinearFogIsSaidToBeIgnored()
        {
            StringAssert.Contains("Linear fog ignores it", LightingFunctions.EffectNotes(false, AmbientMode.Flat, true, FogMode.Linear));
            Assert.IsEmpty(LightingFunctions.EffectNotes(false, AmbientMode.Flat, true, FogMode.ExponentialSquared));
        }

        [Test]
        public void AFogColorOrDensityWhileFogIsOffIsSaidToShowNothing()
        {
            StringAssert.Contains("fog is off", LightingFunctions.EffectNotes(false, AmbientMode.Flat, true, FogMode.Exponential, fogOn: false));
            StringAssert.Contains("fog is off", LightingFunctions.EffectNotes(false, AmbientMode.Flat, false, FogMode.Exponential, fogColorSet: true, fogOn: false));
            StringAssert.Contains("until fog=true", LightingFunctions.EffectNotes(false, AmbientMode.Flat, true, FogMode.Exponential, true, false));

            Assert.IsEmpty(LightingFunctions.EffectNotes(false, AmbientMode.Flat, true, FogMode.Exponential, true, true));
            Assert.IsEmpty(LightingFunctions.EffectNotes(false, AmbientMode.Flat, false, FogMode.Exponential, false, false),
                "no fog value was set, so there is nothing to say");
        }
    }
}
