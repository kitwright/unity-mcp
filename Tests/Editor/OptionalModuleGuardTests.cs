// Copyright (C) KitWright. Licensed under MIT.

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using KitWright.Editor.Threading;
using KitWright.Editor.Tools.Scripting;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace KitWright.Editor.Tests
{
    /// <summary>
    /// Source scan (no compilation needed): a project that removed an optional engine module must
    /// still compile KitWright.Editor, so every reference to a module type has to sit inside the
    /// matching versionDefine block.
    /// </summary>
    public sealed class OptionalModuleGuardTests
    {
        // (define, package that defines it, regex matching a type from that module)
        private static readonly (string Define, string Package, string Pattern)[] Modules =
        {
            ("KITWRIGHT_PHYSICS", "com.unity.modules.physics",
                @"\bPhysics\.|\b\w*Collider\b|\bRigidbody\b|\bRaycastHit\b"),
            ("KITWRIGHT_PHYSICS2D", "com.unity.modules.physics2d",
                @"Physics2D\.|\b\w*Collider2D\b|\bRigidbody2D\b"),
            ("KITWRIGHT_AI", "com.unity.modules.ai",
                @"\bNavMesh\w*\b|UnityEngine\.AI|UnityEditor\.AI"),
            ("KITWRIGHT_TERRAIN", "com.unity.modules.terrain",
                @"\bTerrain\b|\bTerrainData\b|\bTerrainLayer\b|\bTreePrototype\b|\bTreeInstance\b"),
            ("KITWRIGHT_PARTICLES", "com.unity.modules.particlesystem",
                @"\bParticleSystem\b"),
            ("KITWRIGHT_AUDIO", "com.unity.modules.audio",
                @"\bAudioSource\b|\bAudioClip\b|\bAudioListener\b|\bAudioImporter\b|\bAudioMixer\w*\b|\bAudioClipLoadType\b|\bAudioCompressionFormat\b"),
            ("KITWRIGHT_ANIMATION", "com.unity.modules.animation",
                @"\bAnimator\b|\bAnimatorController\w*\b|\bAnimationClip\b|\bAnimatorOverrideController\b|\bRuntimeAnimatorController\b|\bAnimationUtility\b|\bAnimationMode\b"),
        };

        // Scanned files exempted by name. A match here is either not a module reference at all, or
        // it is one that still needs its #if - keep the reason, and shrink this list, never grow it.
        private static readonly HashSet<string> Exempt = new HashSet<string>
        {
            "NavMeshFunctionsTests.cs",    // fixture name only; the body exercises ValueConverter
            "HierarchyFunctionsTests.cs",  // owed #if KITWRIGHT_PHYSICS (BoxCollider)
            "ReflectionFunctionsTests.cs", // owed #if KITWRIGHT_PHYSICS (typeof(Rigidbody), RaycastHit)
            "ToolSmokeTests.cs",           // owed #if KITWRIGHT_PHYSICS / _PHYSICS2D / _PARTICLES
        };

        // Modules referenced with no KITWRIGHT_* guard, so stripping one is a hard compile break
        // (CS1069/CS0103) - and it is one assembly, so that kills all ~266 tools at once.
        private static readonly (string Module, string Site)[] RequiredModules =
        {
            ("com.unity.modules.imageconversion", "ScreenshotFunctions.cs EncodeToPNG"),
            ("com.unity.modules.unitywebrequest", "UpdateChecker.cs UnityWebRequest"),
            ("com.unity.modules.uielements", "Editor/UI/* UnityEngine.UIElements"),
        };

        [Test]
        public void PackageManifest_DeclaresRequiredModulesAndOmitsGuardedOnes()
        {
            var manifest = File.ReadAllText(Path.Combine(PackageRoot(), "package.json"));

            foreach (var (module, site) in RequiredModules)
                Assert.That(manifest, Does.Contain("\"" + module + "\""), module + " undeclared; used by " + site);

            foreach (var (_, package, _) in Modules)
                Assert.That(manifest, Does.Not.Contain("\"" + package + "\""),
                    package + " is behind a KITWRIGHT_* versionDefine; declaring it would force the " +
                    "module on every consumer and make the define always true.");
        }

        [Test]
        public void Asmdef_DefinesOneVersionDefinePerOptionalModule()
        {
            // Both assemblies: the tests reference the guarded tools, so they need the same defines
            // or the test assembly is the thing that fails to compile on a stripped project.
            foreach (var asmdef in new[] { EditorAsmdefPath(), TestsAsmdefPath() })
            {
                var defines = ReadVersionDefines(asmdef);
                foreach (var (define, package, _) in Modules)
                {
                    Assert.IsTrue(defines.TryGetValue(define, out var declared),
                        define + " is missing from " + asmdef + " versionDefines.");
                    Assert.AreEqual(package, declared,
                        define + " must be gated on " + package + " in " + asmdef + ".");
                }
            }
        }

        [Test]
        public void EveryOptionalModuleReference_SitsInsideItsVersionDefine()
        {
            var defines = ReadVersionDefines(EditorAsmdefPath());
            var violations = new List<string>();

            foreach (var root in new[] { EditorSourceRoot(), TestsSourceRoot() })
                foreach (var file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
                    if (!Exempt.Contains(Path.GetFileName(file)))
                        CollectViolations(file, violations);

            Assert.IsEmpty(violations,
                "Unguarded optional-module references (wrap them in the matching #if, see versionDefines: "
                + string.Join(", ", defines.Keys) + "):\n" + string.Join("\n", violations));
        }

        private static void CollectViolations(string file, List<string> violations)
        {
            var source = File.ReadAllText(file);
            var lines = source.Split('\n');
            // Strings, chars and comments blanked, offsets and line breaks kept, so masked[i] is
            // line i with only real code left on it.
            var masked = CSharpMemberEditor.Mask(source).Split('\n');
            var conditions = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("#if "))
                {
                    conditions.Add(trimmed.Substring(4).Trim());
                    continue;
                }
                if (trimmed.StartsWith("#elif "))
                {
                    if (conditions.Count > 0) conditions[conditions.Count - 1] = trimmed.Substring(6).Trim();
                    continue;
                }
                if (trimmed.StartsWith("#else"))
                {
                    if (conditions.Count > 0)
                        conditions[conditions.Count - 1] = "!(" + conditions[conditions.Count - 1] + ")";
                    continue;
                }
                if (trimmed.StartsWith("#endif"))
                {
                    if (conditions.Count > 0) conditions.RemoveAt(conditions.Count - 1);
                    continue;
                }

                foreach (var (define, _, pattern) in Modules)
                {
                    if (!Regex.IsMatch(masked[i], pattern)) continue;
                    if (IsGuardedBy(conditions, define)) continue;
                    violations.Add($"{Path.GetFileName(file)}:{i + 1} needs #if {define} -- {trimmed}");
                }
            }
        }

        // A negated condition (an #else branch, or #if !DEFINE) does not count as a guard.
        private static bool IsGuardedBy(List<string> conditions, string define)
        {
            foreach (var condition in conditions)
                if (condition.Contains(define) && !condition.Contains("!"))
                    return true;
            return false;
        }

        private static Dictionary<string, string> ReadVersionDefines(string path)
        {
            Assert.IsTrue(File.Exists(path), "Could not locate an asmdef (looked at " + path + ").");

            var map = new Dictionary<string, string>();
            var entries = JObject.Parse(File.ReadAllText(path))["versionDefines"] as JArray;
            if (entries != null)
                foreach (var entry in entries)
                    map[(string)entry["define"]] = (string)entry["name"];
            return map;
        }

        internal static string PackageRoot()
        {
            var package = PackageInfo.FindForAssembly(typeof(EditorThreadHelper).Assembly);
            var root = package != null && !string.IsNullOrEmpty(package.resolvedPath)
                ? package.resolvedPath
                : Path.GetFullPath("Packages/com.kitwright.unity.mcp");

            Assert.IsTrue(Directory.Exists(root), "Could not locate our package root (looked at " + root + ").");
            return root;
        }

        private static string EditorSourceRoot() => Path.Combine(PackageRoot(), "Editor");

        private static string TestsSourceRoot() => Path.Combine(PackageRoot(), "Tests");

        private static string EditorAsmdefPath() => Path.Combine(EditorSourceRoot(), "KitWright.Editor.asmdef");

        private static string TestsAsmdefPath() =>
            Path.Combine(TestsSourceRoot(), "Editor", "KitWright.Editor.Tests.asmdef");
    }
}
