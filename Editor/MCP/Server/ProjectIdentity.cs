// Copyright (C) KitWright. All rights reserved.

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace KitWright.Editor.MCP.Server
{
    internal static class ProjectIdentity
    {
        public const string IdentityVersion = "project-path-sha256-v1";

        public static string FromProjectPath(string projectPath)
        {
            var normalized = NormalizeProjectPath(projectPath);
            if (string.IsNullOrEmpty(normalized))
                return string.Empty;

            using (var sha256 = SHA256.Create())
            {
                var bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }

        // Short, human-readable tail of the identity. Two projects can share a productName
        // (a clone, or the same branch checked out twice), so config entry names need this to
        // stay distinct — the full hash is too long to read in a client's config file.
        public const int PinLength = 8;

        public static string PinFromProjectPath(string projectPath)
        {
            var identity = FromProjectPath(projectPath);
            return identity.Length >= PinLength ? identity.Substring(0, PinLength) : identity;
        }

        // Spreads the per-project default port over 100 slots. Every project used to default to one
        // shared port, so which project ended up on it depended on which editor started first and a
        // client config written yesterday could reach a sibling project today. Deriving the offset
        // from the path keeps a project on the same port across restarts. Two projects can still
        // collide here — that is what the transport's fall-forward scan is for.
        //
        // Slots are 10 apart because that scan probes basePort..basePort+9: adjacent defaults would
        // let one collision walk onto the next project's reserved port and push that one along too,
        // which is the order-dependence this exists to remove.
        public static int PortOffsetFromProjectPath(string projectPath)
        {
            var pin = PinFromProjectPath(projectPath);
            if (pin.Length < HashSlice)
                return 0;

            return (int)(Convert.ToUInt32(pin.Substring(0, HashSlice), 16) % 100) * 10;
        }

        // Widest hex slice ToUInt32 can take; PinLength is free to grow without overflowing here.
        private const int HashSlice = 8;

        private static string NormalizeProjectPath(string projectPath)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
                return string.Empty;

            var fullPath = Path.GetFullPath(projectPath)
                .Replace('\\', '/')
                .TrimEnd('/');

            return fullPath.ToLowerInvariant();
        }
    }
}
