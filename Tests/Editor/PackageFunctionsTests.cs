// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class PackageFunctionsTests
    {
        [Test]
        public void ARegistryIdIsNotARemoteSource()
        {
            Assert.IsFalse(PackageFunctions.IsRemoteSource("com.unity.textmeshpro"));
            Assert.IsFalse(PackageFunctions.IsRemoteSource("com.unity.cinemachine@2.9.7"));
            Assert.IsFalse(PackageFunctions.IsRemoteSource(""));
            Assert.IsFalse(PackageFunctions.IsRemoteSource(null));
        }

        [Test]
        public void AnythingUnityWouldFetchAndCompileIsARemoteSource()
        {
            // Client.Add writes this straight into manifest.json, so it survives restarts and goes
            // into version control - that persistence is why it asks rather than just doing it.
            Assert.IsTrue(PackageFunctions.IsRemoteSource("https://github.com/x/y.git"));
            Assert.IsTrue(PackageFunctions.IsRemoteSource("git@github.com:x/y.git"));
            Assert.IsTrue(PackageFunctions.IsRemoteSource("ssh://git@host/x.git"));
            Assert.IsTrue(PackageFunctions.IsRemoteSource("file:../LocalPackage"));
            Assert.IsTrue(PackageFunctions.IsRemoteSource("../SomeFolder"));
            Assert.IsTrue(PackageFunctions.IsRemoteSource(@"C:\pkgs\thing.tgz"));
        }

        [Test]
        public void InstallRefusesARemoteSourceUntilItIsAskedFor()
        {
            var refused = PackageFunctions.InstallPackage("https://github.com/x/y.git");

            StringAssert.Contains("REMOTE_PACKAGE_SOURCE", refused);
        }
    }
}
