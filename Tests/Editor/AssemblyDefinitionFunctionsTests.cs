// Copyright (C) KitWright. Licensed under MIT.

using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;

namespace KitWright.Editor.Tests
{
    public sealed class AssemblyDefinitionFunctionsTests
    {
        [Test]
        public void SplitCsv_EmptyReturnsEmpty()
        {
            Assert.IsEmpty(AssemblyDefinitionFunctions.SplitCsv(null));
            Assert.IsEmpty(AssemblyDefinitionFunctions.SplitCsv("   "));
        }

        [Test]
        public void SplitCsv_TrimsAndDropsBlanks()
        {
            var parts = AssemblyDefinitionFunctions.SplitCsv(" A , B ,, C ");
            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, parts);
        }

        [Test]
        public void ResolveReferences_GuidTokenPassesThrough()
        {
            var result = AssemblyDefinitionFunctions.ResolveReferences(new[] { "GUID:abc123" }).ToList();
            CollectionAssert.AreEqual(new[] { "GUID:abc123" }, result);
        }

        [Test]
        public void ResolveReferences_UnknownAssemblyKeptAsPlainName()
        {
            var result = AssemblyDefinitionFunctions.ResolveReferences(new[] { "Definitely.Not.An.Assembly" }).ToList();
            CollectionAssert.AreEqual(new[] { "Definitely.Not.An.Assembly" }, result);
        }

        // Source assert rather than an end-to-end write: CreateAssemblyDef only accepts paths under
        // Assets/ and calls AssetDatabase.ImportAsset, so writing a real .asmdef reloads the domain
        // mid-run - which cost three reloads, seven EDITOR_BUSY failures and 14 minutes when tried.
        // AtomicFileTests covers the swap itself; what needs pinning here is that the write tools
        // still route through it.
        [Test]
        public void WriteTools_RouteEveryWriteThroughAtomicFile()
        {
            foreach (var relative in new[]
                     {
                         "Editor/Tools/Builtins/AssemblyDefinitionFunctions.cs",
                         "Editor/Tools/Builtins/CodeFunctions.cs",
                         "Editor/Tools/Builtins/FileFunctions.cs",
                     })
            {
                var file = Path.Combine(OptionalModuleGuardTests.PackageRoot(), relative);
                Assert.IsTrue(File.Exists(file), "Missing source file: " + file);

                // Negative lookbehind, or the pattern matches AtomicFile.WriteAllText itself.
                Assert.IsFalse(Regex.IsMatch(File.ReadAllText(file), @"(?<!Atomic)File\.WriteAllText"),
                    relative + " writes directly instead of through AtomicFile.WriteAllText.");
            }
        }
    }
}
