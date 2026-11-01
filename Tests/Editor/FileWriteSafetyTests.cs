// Copyright (C) KitWright. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using KitWright.Editor.MCP.Server;
using KitWright.Editor.Tools.Helpers;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace KitWright.Editor.Tests
{
    public sealed class FileWriteSafetyTests
    {
        private static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        private string _root;

        // Under Temp/, not Assets/: nothing here needs an import, and a link created inside Assets
        // would be picked up by the asset database.
        [SetUp]
        public void SetUp()
        {
            var project = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            _root = Path.GetFullPath(Path.Combine(project, "Temp", "__KitWrightFileWriteSafetyTests", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_root, true); }
            catch { }
        }

        [Test]
        public void WriteAllText_ContentWithItsOwnBom_WritesOneBomOverAFileThatAlreadyHasOne()
        {
            var path = Path.Combine(_root, "Bom.txt");
            File.WriteAllBytes(path, Bom.Concat(new[] { (byte)'a' }).ToArray());

            AtomicFile.WriteAllText(path, "﻿b");

            CollectionAssert.AreEqual(Bom.Concat(new[] { (byte)'b' }).ToArray(), File.ReadAllBytes(path));
        }

        [Test]
        public void WriteAllText_ContentWithItsOwnBom_KeepsItInANewFile()
        {
            var path = Path.Combine(_root, "New.txt");

            AtomicFile.WriteAllText(path, "﻿b");

            CollectionAssert.AreEqual(Bom.Concat(new[] { (byte)'b' }).ToArray(), File.ReadAllBytes(path));
        }

        [Test]
        public void WriteAllBytes_WritesExactlyTheBytesOverAnExistingFileAndLeavesNoSibling()
        {
            var path = Path.Combine(_root, "Image.png");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            var bytes = new byte[] { 0xEF, 0xBB, 0xBF, 0, 255, 10, 13 };

            AtomicFile.WriteAllBytes(path, bytes);

            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
            Assert.AreEqual(new[] { path }, Directory.GetFiles(_root), "the swap left a sibling behind");

            var fresh = Path.Combine(_root, "Fresh.png");
            AtomicFile.WriteAllBytes(fresh, bytes);

            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(fresh));
        }

        [Test]
        public void WriteAllText_ThroughASymlink_WritesTheTargetAndKeepsTheLink()
        {
            var target = Path.Combine(_root, "target.txt");
            var link = Path.Combine(_root, "link.txt");
            File.WriteAllText(target, "old");
            if (!TryLink(link, target, false))
                Assert.Ignore("This account cannot create symlinks here.");

            AtomicFile.WriteAllText(link, "new");

            Assert.AreEqual("new", File.ReadAllText(target), "the repo-side copy went stale");
            Assert.AreNotEqual(0, (int)(File.GetAttributes(link) & FileAttributes.ReparsePoint),
                "the link was replaced by a regular file");
            Assert.AreEqual(2, Directory.GetFiles(_root).Length, "the swap left a sibling behind");
        }

        [Test]
        public void WriteAllText_ToAHardLinkedFile_UpdatesTheOtherNameToo()
        {
            var first = Path.Combine(_root, "first.txt");
            var second = Path.Combine(_root, "second.txt");
            File.WriteAllText(first, "old content that is longer than the new one");
            if (!TryHardLink(second, first))
                Assert.Ignore("A hard link is only created here on Windows, and it could not be created.");

            AtomicFile.WriteAllText(first, "new");

            Assert.AreEqual("new", File.ReadAllText(second), "the swap left the other name with the old content");
            Assert.AreEqual("new", File.ReadAllText(first));
            Assert.AreEqual(2, Directory.GetFiles(_root).Length, "a temp file was left behind");
        }

        [Test]
        public void WriteAllBytes_ToAHardLinkedFile_UpdatesTheOtherNameToo()
        {
            var first = Path.Combine(_root, "first.bin");
            var second = Path.Combine(_root, "second.bin");
            File.WriteAllBytes(first, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            if (!TryHardLink(second, first))
                Assert.Ignore("A hard link is only created here on Windows, and it could not be created.");

            AtomicFile.WriteAllBytes(second, new byte[] { 9, 8 });

            CollectionAssert.AreEqual(new byte[] { 9, 8 }, File.ReadAllBytes(first));
            CollectionAssert.AreEqual(new byte[] { 9, 8 }, File.ReadAllBytes(second));
        }

        [Test]
        public void WriteAllText_ToAHardLinkedFile_KeepsTheBomTheFileHad()
        {
            var first = Path.Combine(_root, "first.txt");
            var second = Path.Combine(_root, "second.txt");
            File.WriteAllBytes(first, Bom.Concat(new[] { (byte)'a' }).ToArray());
            if (!TryHardLink(second, first))
                Assert.Ignore("A hard link is only created here on Windows, and it could not be created.");

            AtomicFile.WriteAllText(second, "b");

            var expected = Bom.Concat(new[] { (byte)'b' }).ToArray();
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(first));
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(second));
        }

        // The Unix branch of HasMultipleHardLinks runs the system stat tool; this drives that same process
        // and parsing chain with whatever GNU stat the host has (a Windows machine with Git for Windows has one).
        [Test]
        public void LinkCountFromStat_ReadsTheCountOfARealHardLinkedFile()
        {
            var stat = Path.DirectorySeparatorChar == '\\'
                ? new[] { @"C:\Program Files\Git\usr\bin\stat.exe", @"C:\Program Files (x86)\Git\usr\bin\stat.exe" }.FirstOrDefault(File.Exists)
                : (File.Exists("/usr/bin/stat") ? "/usr/bin/stat" : null);
            if (stat == null)
                Assert.Ignore("No GNU stat on this host.");

            var args = Path.DirectorySeparatorChar != '\\' && RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "-f %l -- " : "-c %h -- ";
            var first = Path.Combine(_root, "one name.txt");
            var second = Path.Combine(_root, "two names.txt");
            File.WriteAllText(first, "x");

            Assert.AreEqual(1, AtomicFile.LinkCountFromStat(stat, args + "\"" + first + "\""));
            Assert.AreEqual(1, AtomicFile.LinkCountFromStat(stat, args + "\"" + Path.Combine(_root, "missing.txt") + "\""),
                "a stat that fails prints nothing on stdout, which counts as one link");

            if (!TryHardLink(second, first))
                Assert.Ignore("This host cannot create a hard link here.");

            Assert.AreEqual(2, AtomicFile.LinkCountFromStat(stat, args + "\"" + first + "\""));
            Assert.AreEqual(2, AtomicFile.LinkCountFromStat(stat, args + "\"" + second + "\""));
        }

        [Test]
        public void HasMultipleHardLinks_CountsTheLinksAndTreatsAMissingFileAsOne()
        {
            var first = Path.Combine(_root, "first.txt");
            var second = Path.Combine(_root, "second.txt");
            File.WriteAllText(first, "x");

            Assert.IsFalse(AtomicFile.HasMultipleHardLinks(first), "a file with one name has one link");
            Assert.IsFalse(AtomicFile.HasMultipleHardLinks(Path.Combine(_root, "missing.txt")));
            if (!TryHardLink(second, first))
                Assert.Ignore("A hard link is only created here on Windows, and it could not be created.");

            Assert.IsTrue(AtomicFile.HasMultipleHardLinks(first));
            Assert.IsTrue(AtomicFile.HasMultipleHardLinks(second));
        }

        [TestCase("2\n", 2)]
        [TestCase(" 3 ", 3)]
        [TestCase("1", 1)]
        [TestCase("", 1)]
        [TestCase(null, 1)]
        [TestCase("garbage", 1)]
        [TestCase("0\n", 1)]
        [TestCase("99999999999\n", 1)]
        public void ParseLinkCount_ReadsTheFirstIntegerAndAnythingElseIsOneLink(string stdout, int expected)
        {
            Assert.AreEqual(expected, AtomicFile.ParseLinkCount(stdout));
        }

        [Test]
        public void WriteAllText_IntoAJunctionedFolder_LandsInTheTargetFolder()
        {
            if (Path.DirectorySeparatorChar != '\\')
                Assert.Ignore("Junctions are a Windows feature.");

            var real = Path.Combine(_root, "real");
            var junction = Path.Combine(_root, "junction");
            Directory.CreateDirectory(real);
            if (!TryLink(junction, real, true))
                Assert.Ignore("A junction could not be created here.");

            try
            {
                AtomicFile.WriteAllText(Path.Combine(junction, "a.txt"), "x");

                Assert.AreEqual("x", File.ReadAllText(Path.Combine(real, "a.txt")));
                StringAssert.AreEqualIgnoringCase(Path.Combine(real, "a.txt"), AtomicFile.FinalPathOnWindows(Path.Combine(junction, "a.txt")),
                    "the link resolver did not return the real path");
                Assert.AreEqual(1, Directory.GetFiles(real).Length, "the swap left a sibling behind");
            }
            finally
            {
                try { Directory.Delete(junction); }
                catch { }
            }
        }

        // The temp name is 13 characters longer than the file's, so a path a few characters under the
        // limit used to fail where the direct write it replaced succeeded.
        [Test]
        public void WriteAllText_ToAPathJustUnderTheLimit_StillWrites()
        {
            var directory = Path.Combine(_root, new string('d', 40));
            Directory.CreateDirectory(directory);
            var pad = 252 - (directory.Length + 1 + ".txt".Length);
            if (pad < 1 || pad > 200)
                Assert.Ignore("The project path leaves no room for a 252 character file path.");

            var path = Path.Combine(directory, new string('p', pad) + ".txt");

            AtomicFile.WriteAllText(path, "x");
            AtomicFile.WriteAllText(path, "y");

            Assert.AreEqual("y", File.ReadAllText(path));
            Assert.AreEqual(1, Directory.GetFiles(directory).Length, "a temp file was left behind");
        }

        [Test]
        public void RewriteJson_KeepsTheBomOfTheConfigItRepairs()
        {
            var path = Path.Combine(_root, "config.json");
            File.WriteAllBytes(path, Bom.Concat(System.Text.Encoding.UTF8.GetBytes(
                "{\"mcpServers\":{\"kitwright\":{\"url\":\"http://127.0.0.1:8766/\"}}}")).ToArray());

            Assert.IsTrue(MCPClientConfigAutoRewrite.RewriteJson(path, "mcpServers", "kitwright", "http://127.0.0.1:8765/"));

            CollectionAssert.AreEqual(Bom, File.ReadAllBytes(path).Take(3).ToArray());
            StringAssert.Contains("8765", File.ReadAllText(path));
            Assert.AreEqual(1, Directory.GetFiles(_root).Length, "the swap left a sibling behind");
        }

        [Test]
        public void WriteIfUnchanged_LeavesAFileThatChangedSinceItWasRead()
        {
            var path = Path.Combine(_root, "config.json");
            File.WriteAllText(path, "{\"changedByTheClient\":true}");

            LogAssert.Expect(LogType.Warning, new Regex("left .* alone"));
            Assert.IsFalse(MCPClientConfigAutoRewrite.WriteIfUnchanged(path, "{\"whatWeRead\":true}", "{\"ours\":true}"));
            Assert.AreEqual("{\"changedByTheClient\":true}", File.ReadAllText(path), "the client's change was overwritten");

            Assert.IsTrue(MCPClientConfigAutoRewrite.WriteIfUnchanged(path, "{\"changedByTheClient\":true}", "{\"ours\":true}"));
            Assert.AreEqual("{\"ours\":true}", File.ReadAllText(path));
        }

        [Test]
        public void FileWriters_RouteEveryWriteThroughAtomicFile()
        {
            foreach (var relative in new[]
                     {
                         "Editor/MCP/Server/MCPClientConfigAutoRewrite.cs",
                         "Editor/Tools/Builtins/ShaderFunctions.cs",
                         "Editor/Tools/Builtins/TextureFunctions.cs",
                         "Editor/Tools/Builtins/ProfilerFunctions.cs",
                     })
            {
                var file = Path.Combine(OptionalModuleGuardTests.PackageRoot(), relative);
                Assert.IsTrue(File.Exists(file), "Missing source file: " + file);

                Assert.IsFalse(Regex.IsMatch(File.ReadAllText(file), @"(?<!Atomic)File\.WriteAll(Text|Bytes)"),
                    relative + " writes directly instead of through AtomicFile.");
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

        // Needs NTFS but no privilege. Not Windows: the stat tool the writer runs there is not run by a
        // test that makes a link, so only ParseLinkCount is covered on macOS/Linux.
        private static bool TryHardLink(string link, string existing)
        {
            try
            {
                if (Path.DirectorySeparatorChar != '\\')
                {
                    using (var ln = Process.Start(new ProcessStartInfo("/bin/ln", $"\"{existing}\" \"{link}\"") { UseShellExecute = false, CreateNoWindow = true }))
                        return ln.WaitForExit(5000) && ln.ExitCode == 0 && File.Exists(link);
                }

                return CreateHardLinkW(link, existing, IntPtr.Zero) && File.Exists(link);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Both OSes can refuse: Windows needs the symlink privilege (or Developer Mode) for a file link.
        private static bool TryLink(string link, string target, bool junction)
        {
            try
            {
                var info = Path.DirectorySeparatorChar == '\\'
                    ? new ProcessStartInfo("cmd.exe", $"/c mklink {(junction ? "/J " : string.Empty)}\"{link}\" \"{target}\"")
                    : new ProcessStartInfo("/bin/ln", $"-s \"{target}\" \"{link}\"");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;

                using (var process = Process.Start(info))
                {
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0 && (File.Exists(link) || Directory.Exists(link));
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
