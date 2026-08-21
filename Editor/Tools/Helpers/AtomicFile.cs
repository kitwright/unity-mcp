// Copyright (C) KitWright. Licensed under MIT.

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
            var tempPath = fullPath + ".tmp";

            File.WriteAllText(tempPath, content);
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
                File.Delete(tempPath);
            }
        }
    }
}
