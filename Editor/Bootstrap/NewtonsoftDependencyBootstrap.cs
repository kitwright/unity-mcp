// Copyright (C) KitWright. All rights reserved.

using System;
using System.Linq;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace KitWright.Editor.Bootstrap
{
    /// <summary>
    /// Recovers the install when Newtonsoft Json is missing.
    ///
    /// Importing from the Asset Store shows a "This Unity Package has Package Manager
    /// dependencies" dialog, and choosing Skip leaves the project without
    /// com.unity.nuget.newtonsoft-json. Every KitWright.Editor script then fails to
    /// compile, so nothing inside that assembly can report the cause.
    ///
    /// This lives in its own assembly with no references, so it still compiles and
    /// still runs while the main assembly is broken.
    /// </summary>
    [InitializeOnLoad]
    internal static class NewtonsoftDependencyBootstrap
    {
        private const string PackageId = "com.unity.nuget.newtonsoft-json";
        private const string PackageVersion = "3.2.1";
        private const string InstallFailedKey = "KitWright.MCP.Bootstrap.NewtonsoftInstallFailed";

        private static AddRequest _addRequest;
        private static ListRequest _listRequest;

        static NewtonsoftDependencyBootstrap()
        {
            if (Application.isBatchMode || NewtonsoftIsPresent())
                return;

            // The package list is only queryable once the editor is idle, and asking on
            // the first domain load of a fresh import races the import itself.
            EditorApplication.delayCall += BeginCheck;
        }

        private static bool NewtonsoftIsPresent()
        {
            // Type.GetType with an assembly-qualified name would need a hardcoded
            // assembly version, so scan the loaded assemblies instead.
            return AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetType("Newtonsoft.Json.JsonConvert", false) != null);
        }

        private static void BeginCheck()
        {
            if (_listRequest != null || _addRequest != null)
                return;

            _listRequest = Client.List(offlineMode: true, includeIndirectDependencies: true);
            EditorApplication.update += PollList;
        }

        private static void PollList()
        {
            if (_listRequest == null || !_listRequest.IsCompleted)
                return;

            EditorApplication.update -= PollList;

            var installed = _listRequest.Status == StatusCode.Success &&
                            _listRequest.Result.Any(p => p.name == PackageId);
            _listRequest = null;

            if (installed)
                return;

            Install();
        }

        // package.json already declares this dependency, so pulling it in is resolving the
        // manifest rather than a choice to put to the user.
        private static void Install()
        {
            if (SessionState.GetBool(InstallFailedKey, false))
                return;

            Debug.Log(
                $"[KitWright MCP] {PackageId} is missing, so KitWright scripts cannot compile. " +
                $"Installing {PackageId}@{PackageVersion} from the Unity registry.");

            _addRequest = Client.Add($"{PackageId}@{PackageVersion}");
            EditorApplication.update += PollAdd;
        }

        private static void PollAdd()
        {
            if (_addRequest == null || !_addRequest.IsCompleted)
                return;

            EditorApplication.update -= PollAdd;
            var request = _addRequest;
            _addRequest = null;

            if (request.Status == StatusCode.Success)
            {
                Debug.Log($"[KitWright MCP] Installed {request.Result.packageId}. Recompiling.");
                return;
            }

            SessionState.SetBool(InstallFailedKey, true);
            Debug.LogError(
                $"[KitWright MCP] Could not install {PackageId}: {request.Error?.message}\n" +
                $"Install it manually from Window > Package Manager (Add package by name: {PackageId}).");
        }
    }
}
