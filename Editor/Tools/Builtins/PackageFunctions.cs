// Copyright (C) KitWright. Licensed under MIT.
using System;
using System.Linq;
using System.Threading.Tasks;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Services;
using KitWright.Editor.Tools;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Package")]
    internal static class PackageFunctions
    {
        [Description("Install a Unity package by name. A git URL, tarball or local folder needs allow_remote_source: " +
                     "it is written into manifest.json, so it outlives the session and lands in version control.")]
        public static async Task<string> InstallPackage(
            [ToolParam("Package identifier (e.g. 'com.unity.textmeshpro', 'com.unity.cinemachine')")] string package_id,
            [ToolParam("Allow a git URL, tarball or local path as the source", Required = false)] bool allow_remote_source = false)
        {
            if (!allow_remote_source && IsRemoteSource(package_id))
                return ToolResultFormatter.Error("REMOTE_PACKAGE_SOURCE", new { package_id },
                    "This installs from outside the package registry and writes that source into " +
                    "manifest.json, where it survives restarts and goes into version control. " +
                    "Pass allow_remote_source=true if that is what you mean.");

            return await Outcome(Client.Add(package_id), $"installation of '{package_id}'");
        }

        // A package that cannot be resolved only fails after a registry round trip, so reading the status
        // right after Client.Add reported every call as started. A domain reload during the wait
        // interrupts the call; get_reload_recovery_status says how it ended.
        private static async Task<string> Outcome(Request request, string what)
        {
            lastRequest = request;
            lastRequestWhat = what;

            for (var i = 0; i < 80 && !request.IsCompleted; i++)
                await Task.Delay(250);

            if (!request.IsCompleted)
                return $"The {what} is still running after 20s. Check Package Manager for status.";

            if (request.Status == StatusCode.Failure)
                return ToolResultFormatter.Error("PACKAGE_REQUEST_FAILED",
                    new { request = what, message = request.Error?.message, error_code = request.Error?.errorCode.ToString() });

            return $"Finished the {what}.";
        }

        /// <summary>
        /// Whether Client.Add would resolve this as something other than a registry package. A registry
        /// id is a reverse-DNS name with an optional @version; everything else - a git URL, a tarball,
        /// a file: path - fetches code Unity then compiles and runs.
        /// </summary>
        internal static bool IsRemoteSource(string packageId)
        {
            var id = (packageId ?? string.Empty).Trim();
            if (id.Length == 0)
                return false;

            foreach (var scheme in new[] { "http://", "https://", "git://", "git@", "ssh://", "file:" })
                if (id.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                    return true;

            // A bare path, absolute or relative, is a local folder or tarball rather than an id.
            return id.Contains("/") || id.Contains("\\") || id.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase);
        }

        [Description("Remove a Unity package")]
        public static async Task<string> RemovePackage(
            [ToolParam("Package name to remove")] string package_name)
        {
            var name = (package_name ?? string.Empty).Trim().Split('@')[0];
            if (name.Length == 0)
                return ToolResultFormatter.Error("INVALID_PARAM", new { param = "package_name", provided = package_name });

            if (string.Equals(name, "com.kitwright.unity.mcp", StringComparison.OrdinalIgnoreCase))
                return ToolResultFormatter.Error("PACKAGE_PROTECTED", new { package_name },
                    "That package runs this server; removing it ends the connection mid-call. Remove it from the Package Manager window if you mean to.");

            return await Outcome(Client.Remove(name), $"removal of '{name}'");
        }

        private static Request lastRequest;
        private static string lastRequestWhat;
        internal static void Track(Request request, string what)
        {
            lastRequest = request;
            lastRequestWhat = what;
        }

        private static DateTime? resolveRequestedUtc;
        private static DateTime? resolveCompletedUtc;

        [InitializeOnLoadMethod]
        private static void TrackResolveCompletion() =>
            Events.registeredPackages += _ =>
            {
                if (resolveRequestedUtc != null)
                    resolveCompletedUtc = DateTime.UtcNow;
            };

        [Description("Resolve and refresh every package in Packages/manifest.json (Client.Resolve): re-fetch and re-link. Returns at once; " +
                     "package_status reports it. It may reload the domain, and it needs the registries in the manifest: an internal " +
                     "registry that sits behind a VPN makes it hang for over a minute and then fail. Unity already resolves when " +
                     "manifest.json changes, so call it only to pick up a change Unity did not notice.")]
        public static object PackageResolve()
        {
            resolveRequestedUtc = DateTime.UtcNow;
            resolveCompletedUtc = null;
            lastRequest = null;
            lastRequestWhat = "resolve";

            Client.Resolve();
            return Response.Success("Started resolving the packages. Poll package_status.", new { requestedUtc = resolveRequestedUtc.Value.ToString("o") });
        }

        [Description("Status of the last install_package or remove_package: idle, in_progress, completed or failed, with the installed " +
                     "package or the error. Use it when one of them answered that it was still running after 20s. A domain reload " +
                     "forgets the request, and installing or removing usually causes one: then it reads idle, and list_packages " +
                     "shows whether the manifest changed.")]
        [ReadOnlyTool]
        public static object PackageStatus()
        {
            if (lastRequest == null && lastRequestWhat == "resolve" && resolveRequestedUtc != null)
            {
                var done = resolveCompletedUtc != null;
                return Response.Success($"The last package request (resolve) is {(done ? "completed" : "in_progress")}.", new
                {
                    status = done ? "completed" : "in_progress",
                    request = "resolve",
                    requestedUtc = resolveRequestedUtc.Value.ToString("o"),
                    completedUtc = resolveCompletedUtc?.ToString("o")
                });
            }

            if (lastRequest == null)
                return Response.Success("No package request is tracked.", new { status = "idle" });

            var status = !lastRequest.IsCompleted ? "in_progress"
                : lastRequest.Status == StatusCode.Failure ? "failed"
                : "completed";

            return Response.Success($"The last package request ({lastRequestWhat}) is {status}.", new
            {
                status,
                request = lastRequestWhat,
                package = (lastRequest as AddRequest)?.Result?.packageId,
                error = lastRequest.Error?.message
            });
        }

        [Description("Search the package registry. A package name (e.g. 'com.unity.timeline') returns that package; an empty " +
                     "query lists every available package, at most 200. offline searches the local cache only. Waits up to 60s " +
                     "for the registry, which may need a VPN or network access.")]
        [ReadOnlyTool]
        public static async Task<object> PackageSearch(
            [ToolParam("Package name to search for. Omit to list all available packages.", Required = false)] string query = "",
            [ToolParam("Search the local cache only. Default false.", Required = false)] bool offline = false)
        {
            var name = (query ?? string.Empty).Trim();
            var request = name.Length == 0 ? Client.SearchAll(offline) : Client.Search(name, offline);

            for (var i = 0; i < 240 && !request.IsCompleted; i++)
                await Task.Delay(250);

            if (!request.IsCompleted)
                return Response.Error("PACKAGE_REQUEST_TIMEOUT", new { query = name },
                    "The registry did not answer within 60s. Try offline=true, or check the network connection.");

            if (request.Status == StatusCode.Failure)
                return Response.Error("PACKAGE_REQUEST_FAILED",
                    new { query = name, message = request.Error?.message, error_code = request.Error?.errorCode.ToString() });

            const int limit = 200;
            var packages = request.Result;
            var listed = packages.Take(limit).Select(p => new
            {
                name = p.name,
                displayName = p.displayName,
                version = p.version,
                description = p.description != null && p.description.Length > 200 ? p.description.Substring(0, 200) : p.description
            }).ToArray();

            return Response.Success($"{packages.Length} package(s) found.",
                new { total = packages.Length, truncated = packages.Length > limit, packages = listed });
        }

        [Description("List the packages the project depends on directly: the whole of Packages/manifest.json. " +
                     "Transitive dependencies and built-in modules are not in that file.")]
        [ReadOnlyTool]
        public static string ListPackages() =>
            ListManifest(System.IO.Path.Combine(ApplicationPaths.ProjectRoot, "Packages", "manifest.json"));

        internal static string ListManifest(string manifestPath) =>
            System.IO.File.Exists(manifestPath)
                ? $"Package manifest:\n{System.IO.File.ReadAllText(manifestPath)}"
                : ToolResultFormatter.Error("MANIFEST_NOT_FOUND", new { path = "Packages/manifest.json" });
    }
}
