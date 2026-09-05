// Copyright (C) KitWright. Licensed under MIT.

using System;
using UnityEditor;
using UnityEditor.PackageManager;

namespace KitWright.Editor.MCP.Server
{
    /// Adds a package and reports the outcome once the request settles. Shared by the Integrations
    /// cards and the Tool Exposure switches, which both offer to install what a tool is missing.
    internal static class PackageInstaller
    {
        public static void Install(string packageId, Action<bool, string> onCompleted)
        {
            var request = Client.Add(packageId);

            EditorApplication.CallbackFunction poll = null;
            poll = () =>
            {
                if (!request.IsCompleted)
                    return;

                EditorApplication.update -= poll;
                onCompleted(request.Status == StatusCode.Success, request.Error?.message);
            };
            EditorApplication.update += poll;
        }
    }
}
