// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.IO;

namespace KitWright.Editor.Tools.Helpers
{
    // Write tools must never leave a half-written .cs behind: write a sibling temp file first and
    // swap it in, so a failure mid-write loses the new content instead of the file on disk.
    // Adapted from CoplayDev/unity-mcp, MCPForUnity/Editor/Tools/ManageScript.cs (MIT).
    internal static class AtomicFile
    {
        internal static void WriteAllText(string fullPath, string content)
        {
            // Unique name created with CreateNew: a fixed "<file>.tmp" would overwrite a file of that
            // name the project already owns, and two writes to the same path would share one temp.
            var tempPath = fullPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            try
            {
                // Outside the IOException catch below: a write that fails half way must not reach
                // File.Copy, which would put those partial bytes over the file on disk.
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                    writer.Write(content);

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
    }
}
