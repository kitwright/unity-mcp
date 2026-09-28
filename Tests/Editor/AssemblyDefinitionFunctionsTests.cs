// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KitWright.Editor.Services;
using KitWright.Editor.Tools.Builtins;
using NUnit.Framework;
using static KitWright.Editor.Tests.ToolCall;

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

        [Test]
        public void CreateAssemblyDef_RefusesAPathThatLeavesAssetsBeforeMakingAFolder()
        {
            var escape = "KitWrightEscape_" + Guid.NewGuid().ToString("N");
            var outside = Path.GetFullPath(Path.Combine(ApplicationPaths.ProjectRoot, "..", escape));

            try
            {
                Assert.AreEqual("INVALID_PATH", Code("create_assembly_def", "path", "Assets/../../" + escape + "/Escape.asmdef"));
                Assert.IsFalse(Directory.Exists(outside), "create_assembly_def made a folder outside the project before refusing.");
                Assert.AreEqual("INVALID_PATH", Code("create_assembly_def", "path", Path.Combine(Path.GetTempPath(), escape, "Escape.asmdef")));
            }
            finally
            {
                if (Directory.Exists(outside))
                    Directory.Delete(outside, true);
            }
        }

        [Test]
        public void AsmdefEditTools_RefuseAFileOutsideTheProjectAndLeaveItUntouched()
        {
            var folder = Path.Combine(Path.GetTempPath(), "KitWrightEscape_" + Guid.NewGuid().ToString("N"));
            var outside = Path.Combine(folder, "Escape.asmdef");
            const string original = "{ \"name\": \"Escape\", \"references\": [] }";
            Directory.CreateDirectory(folder);
            File.WriteAllText(outside, original);

            try
            {
                foreach (var call in new[]
                         {
                             new[] { "get_assembly_def_info", "path", outside },
                             new[] { "add_assembly_references", "path", outside, "references", "GUID:abc" },
                             new[] { "remove_assembly_references", "path", outside, "references", "GUID:abc" },
                             new[] { "set_assembly_platforms", "path", outside, "include_platforms", "Editor" },
                             new[] { "update_assembly_def_settings", "path", outside, "name", "Renamed" },
                         })
                    Assert.AreEqual("INVALID_PATH", Code(call[0], call.Skip(1).ToArray()), call[0]);

                Assert.AreEqual(original, File.ReadAllText(outside));
                Assert.AreEqual("INVALID_PATH",
                    Code("get_assembly_def_info", "path", "Assets/../../" + Path.GetFileName(folder) + "/Escape.asmdef"));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        // Temp/ is inside the project root but never imported, so reading from it reloads nothing.
        // Pins that the edit tools stop at the project root rather than Assets/, which keeps an
        // embedded package's asmdef under Packages/ reachable.
        [Test]
        public void GetAssemblyDefInfo_ReadsAnAsmdefInsideTheProjectButOutsideAssets()
        {
            var folder = "Temp/__KitWrightAsmdefTests_" + Guid.NewGuid().ToString("N");
            var absolute = Path.Combine(ApplicationPaths.ProjectRoot, folder);
            Directory.CreateDirectory(absolute);
            File.WriteAllText(Path.Combine(absolute, "Inside.asmdef"), "{ \"name\": \"Inside\" }");

            try
            {
                Assert.AreEqual("Inside", (string)Ok("get_assembly_def_info", "path", folder + "/Inside.asmdef")["data"]["name"]);
            }
            finally
            {
                Directory.Delete(absolute, true);
            }
        }
    }
}
