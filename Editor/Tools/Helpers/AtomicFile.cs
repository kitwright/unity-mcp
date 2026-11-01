// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace KitWright.Editor.Tools.Helpers
{
    // Write tools must never leave a half-written .cs behind: write a sibling temp file first and
    // swap it in, so a failure mid-write loses the new content instead of the file on disk.
    // A file with other hard links is written in place instead: the swap gives the file new contents
    // and the other names keep the old ones.
    // Adapted from CoplayDev/unity-mcp, MCPForUnity/Editor/Tools/ManageScript.cs (MIT).
    internal static class AtomicFile
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, uint length, uint flags);

        [DllImport("libc")]
        private static extern IntPtr realpath(string path, byte[] resolved);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(IntPtr file, out ByHandleFileInformation information);

        // FILETIME is two uints rather than a ulong: a ulong would be aligned to 8 and shift every later field.
        [StructLayout(LayoutKind.Sequential)]
        private struct ByHandleFileInformation
        {
            public uint FileAttributes;
            public uint CreationTimeLow;
            public uint CreationTimeHigh;
            public uint LastAccessTimeLow;
            public uint LastAccessTimeHigh;
            public uint LastWriteTimeLow;
            public uint LastWriteTimeHigh;
            public uint VolumeSerialNumber;
            public uint FileSizeHigh;
            public uint FileSizeLow;
            public uint NumberOfLinks;
            public uint FileIndexHigh;
            public uint FileIndexLow;
        }

        internal static void WriteAllText(string fullPath, string content)
        {
            // A BOM the file already had is kept; content that starts with U+FEFF brings its own, and
            // adding the preamble on top of it wrote two.
            var carriesBom = content != null && content.Length > 0 && content[0] == '\uFEFF';
            var encoding = new UTF8Encoding(HasUtf8Bom(fullPath) && !carriesBom);

            Write(fullPath, stream =>
            {
                using (var writer = new StreamWriter(stream, encoding, 1024, true))
                    writer.Write(content);
            });
        }

        internal static void WriteAllBytes(string fullPath, byte[] bytes)
        {
            Write(fullPath, stream => stream.Write(bytes, 0, bytes.Length));
        }

        private static void Write(string fullPath, Action<Stream> fill)
        {
            fullPath = ResolveLink(fullPath);

            if (HasMultipleHardLinks(fullPath))
            {
                WriteInPlace(fullPath, fill);
                return;
            }

            // Unique name created with CreateNew: a fixed "<file>.tmp" would overwrite a file of that
            // name the project already owns, and two writes to the same path would share one temp.
            var tempPath = fullPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            FileStream temp;
            try
            {
                temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex) when (ex is PathTooLongException || ex is DirectoryNotFoundException)
            {
                // The temp name is 13 characters longer than the file's, so a path a few characters
                // under the limit has none. Write in place, as the callers did before.
                using (var direct = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    fill(direct);
                return;
            }

            try
            {
                // Outside the IOException catch below: a write that fails half way must not reach
                // File.Copy, which would put those partial bytes over the file on disk.
                using (temp)
                    fill(temp);

                try
                {
                    if (File.Exists(fullPath))
                        File.Replace(tempPath, fullPath, null);
                    else
                        File.Move(tempPath, fullPath);
                }
                catch (IOException)
                {
                    File.Copy(tempPath, fullPath, true);
                }
            }
            finally
            {
                // Only ever this call's own temp, and only if it outlived the swap.
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
        }

        // Built in memory first, so a fill that throws leaves the file as it was; only the copy into the
        // file itself is not atomic.
        private static void WriteInPlace(string fullPath, Action<Stream> fill)
        {
            byte[] bytes;
            using (var buffer = new MemoryStream())
            {
                fill(buffer);
                bytes = buffer.ToArray();
            }

            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.SetLength(bytes.Length);
                stream.Flush(true);
            }
        }

        // Windows asks the file handle. stat's struct layout and libc export differ per OS and CPU and
        // Mono.Unix is not available, so macOS/Linux run the platform's own stat tool instead of a
        // P/Invoke. Any failure reads as one link.
        internal static bool HasMultipleHardLinks(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                return Path.DirectorySeparatorChar == '\\' ? LinkCountOnWindows(path) > 1 : LinkCountOnUnix(path) > 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static uint LinkCountOnWindows(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return GetFileInformationByHandle(stream.SafeFileHandle.DangerousGetHandle(), out var information)
                    ? information.NumberOfLinks
                    : 0;
        }

        private static int LinkCountOnUnix(string path)
        {
            var mac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            if (!mac && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return 1;

            // Argument-string parsers disagree on backslashes, so a path with a quote or a backslash
            // stays swapped rather than risk another file's count.
            if (path.IndexOfAny(new[] { '"', '\\' }) >= 0)
                return 1;

            // Absolute on macOS: a GNU stat ahead of it on PATH reads -f as a filesystem query.
            return LinkCountFromStat(mac ? "/usr/bin/stat" : "stat", (mac ? "-f %l" : "-c %h") + " -- \"" + path + "\"");
        }

        // Runs a stat tool and reads the link count it prints; 1 when it fails, prints nothing or hangs.
        internal static int LinkCountFromStat(string executable, string arguments)
        {
            var start = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using (var stat = Process.Start(start))
            {
                if (stat.WaitForExit(2000))
                    return ParseLinkCount(stat.StandardOutput.ReadToEnd());

                stat.Kill();
                return 1;
            }
        }

        internal static int ParseLinkCount(string stdout)
        {
            var match = Regex.Match(stdout ?? string.Empty, "^\\s*([0-9]+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var count) && count > 0 ? count : 1;
        }

        // File.Replace and File.Move put a regular file where a symlink was, so a dotfile linked into
        // a repo stopped being a link and the repo's copy went stale. Mono has no ResolveLinkTarget;
        // any failure here leaves the path as given.
        private static string ResolveLink(string path)
        {
            try
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(path);
                }
                catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
                {
                    return path;
                }

                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    return path;

                return Path.DirectorySeparatorChar == '\\' ? FinalPathOnWindows(path) : RealPathOnUnix(path);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[KitWright] '{path}' is a link that could not be followed, so this write replaces the link with a regular file: {ex.Message}");
                return path;
            }
        }

        internal static string FinalPathOnWindows(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buffer = new StringBuilder(1024);
                var handle = stream.SafeFileHandle.DangerousGetHandle();
                var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length >= buffer.Capacity)
                {
                    buffer = new StringBuilder((int)length + 1);
                    length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                }

                if (length == 0 || length >= buffer.Capacity)
                    throw new IOException("GetFinalPathNameByHandle failed");

                var final = buffer.ToString();
                if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
                    return @"\\" + final.Substring(8);

                return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final.Substring(4) : final;
            }
        }

        private static string RealPathOnUnix(string path)
        {
            var buffer = new byte[4096];
            return realpath(path, buffer) == IntPtr.Zero
                ? path
                : Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
        }

        // ReadAllText drops the BOM, so a rewrite would otherwise strip it from every file that has one.
        private static bool HasUtf8Bom(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return false;

                using (var stream = File.OpenRead(path))
                {
                    var head = new byte[3];
                    return stream.Read(head, 0, 3) == 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
                }
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
