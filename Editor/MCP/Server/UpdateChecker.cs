// Copyright (C) KitWright. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using KitWright.Editor.Services;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityPackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;
using UnityEngine.Networking;

namespace KitWright.Editor.MCP.Server
{
    internal static class UpdateChecker
    {
        private const string PackageName = "com.kitwright.unity.mcp";
        private const string PackageRoot = "Packages/com.kitwright.unity.mcp";
        private const string DefaultAssetRoot = "Assets/unity-mcp";
        private const string GitRepositoryUrl = "https://github.com/kitwright/unity-mcp.git";
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/kitwright/unity-mcp/releases/latest";
        private const string DefaultReleasesUrl = "https://github.com/kitwright/unity-mcp/releases";
        private const string ScriptRootSuffix = "/Editor/MCP/Server/MCPServerService.cs";
        private const double AutoCheckIntervalHours = 6d;
        private const string PrefsPrefix = "KitWright.MCP.UpdateChecker";

        private static bool _stateLoaded;
        private static bool _isChecking;
        private static bool _isUpdating;
        private static bool _updateStarted;
        private static float _progress;
        private static string _statusMessage;
        private static string _latestVersion;
        private static string _latestReleaseUrl;
        private static string _installDescription;
        private static GitHubReleaseResponse _latestRelease;
        private static InstallContext _latestInstallContext;

        public static event Action StateChanged;

        public static UpdateStateSnapshot CurrentState
        {
            get
            {
                EnsureStateLoaded();
                var currentVersion = PackageVersion.Current;
                var hasUpdate = HasNewerCachedVersion(currentVersion);
                return new UpdateStateSnapshot(
                    currentVersion,
                    hasUpdate ? _latestVersion : string.Empty,
                    hasUpdate ? _latestReleaseUrl : string.Empty,
                    hasUpdate ? _installDescription : string.Empty,
                    _statusMessage,
                    hasUpdate,
                    _isChecking,
                    _isUpdating,
                    _updateStarted,
                    _progress);
            }
        }

        public static void MaybeCheckForUpdatesInBackground()
        {
            if (Application.isBatchMode)
                return;

            EnsureStateLoaded();
            NotifyStateChanged();

            if (_isChecking || _isUpdating || !IsAutoCheckDue())
                return;

            _ = CheckForUpdatesAsync();
        }

        public static async void UpdateToLatestFromWindow()
        {
            await UpdateLatestKnownAsync();
        }

        private static async Task CheckForUpdatesAsync()
        {
            if (Application.isBatchMode || _isChecking || _isUpdating)
                return;

            EnsureStateLoaded();
            _isChecking = true;
            _updateStarted = false;
            _statusMessage = "Checking for updates...";
            _progress = 0.25f;
            NotifyStateChanged();

            try
            {
                var installContext = ResolveInstallContext();
                RecordAutoCheckAttempt();
                var latestRelease = await FetchLatestReleaseAsync(false);
                if (latestRelease == null)
                {
                    _statusMessage = "Update check failed.";
                    _progress = 0f;
                    NotifyStateChanged();
                    return;
                }

                var latestVersion = NormalizeVersion(latestRelease.tag_name);
                if (ParseComparableVersion(latestVersion) > ParseComparableVersion(PackageVersion.Current))
                {
                    StoreAvailableUpdate(latestRelease, latestVersion, installContext);
                    _statusMessage = $"Version {latestVersion} is available.";
                }
                else
                {
                    ClearCachedUpdate(false);
                    _statusMessage = string.Empty;
                }

                _progress = 0f;
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                _statusMessage = $"Update check failed: {ex.Message}";
                _progress = 0f;
                NotifyStateChanged();
            }
            finally
            {
                _isChecking = false;
                if (!_isUpdating && !HasNewerCachedVersion(PackageVersion.Current))
                    _statusMessage = string.Empty;
                NotifyStateChanged();
            }
        }

        private static async Task UpdateLatestKnownAsync()
        {
            if (Application.isBatchMode || _isUpdating)
                return;

            EnsureStateLoaded();

            var latestRelease = _latestRelease;
            var latestVersion = _latestVersion;
            var installContext = _latestInstallContext;

            if (latestRelease == null || string.IsNullOrEmpty(latestVersion) ||
                !HasNewerCachedVersion(PackageVersion.Current))
            {
                _statusMessage = "Preparing update...";
                _progress = 0.1f;
                NotifyStateChanged();
                EditorUtility.DisplayProgressBar("KitWright MCP", "Preparing update...", _progress);

                latestRelease = await FetchLatestReleaseAsync(true);
                if (latestRelease == null)
                {
                    _statusMessage = "Update failed: could not fetch latest release.";
                    _progress = 0f;
                    NotifyStateChanged();
                    EditorUtility.ClearProgressBar();
                    return;
                }

                latestVersion = NormalizeVersion(latestRelease.tag_name);
                installContext = ResolveInstallContext();
                StoreAvailableUpdate(latestRelease, latestVersion, installContext);
            }

            if (ParseComparableVersion(latestVersion).CompareTo(ParseComparableVersion(PackageVersion.Current)) <= 0)
            {
                ClearCachedUpdate(false);
                _statusMessage = "You are up to date.";
                _progress = 0f;
                NotifyStateChanged();
                EditorUtility.ClearProgressBar();
                return;
            }

            await RunUpdateAsync(installContext, latestRelease, latestVersion);
        }

        private static async Task RunUpdateAsync(InstallContext installContext, GitHubReleaseResponse latestRelease, string latestVersion)
        {
            _isUpdating = true;
            _updateStarted = false;
            SetUpdateProgress($"Updating to version {latestVersion}...", 0.15f);

            try
            {
                await UpdateToLatestAsync(installContext, latestRelease, latestVersion);
                _updateStarted = true;
                _statusMessage = $"Update to version {latestVersion} started. Unity may recompile and reload.";
                _progress = 1f;
                NotifyStateChanged();
            }
            catch (Exception ex)
            {
                _updateStarted = false;
                _statusMessage = $"Update failed: {ex.Message}";
                _progress = 0f;
                Debug.LogError($"[KitWright MCP] Update failed: {ex}");
                NotifyStateChanged();
                EditorUtility.DisplayDialog("KitWright MCP", $"Update failed:\n{ex.Message}", "OK");
            }
            finally
            {
                _isUpdating = false;
                NotifyStateChanged();
                EditorUtility.ClearProgressBar();
            }
        }

        private static async Task UpdateToLatestAsync(InstallContext installContext, GitHubReleaseResponse latestRelease, string latestVersion)
        {
            switch (installContext.Mode)
            {
                case InstallMode.GitPackage:
                    StartGitPackageUpdate(latestVersion);
                    return;
                default:
                    await DownloadAndImportUnityPackageAsync(installContext, latestRelease, latestVersion);
                    return;
            }
        }

        private static void StartGitPackageUpdate(string latestVersion)
        {
            var gitReference = $"{GitRepositoryUrl}#v{NormalizeVersion(latestVersion)}";
            SetUpdateProgress("Starting Unity Package Manager update...", 0.8f);
            UnityEditor.PackageManager.Client.Add(gitReference);

            EditorUtility.ClearProgressBar();
            SetUpdateProgress("Unity Package Manager update started. Unity may recompile and reload.", 1f);
            EditorUtility.DisplayDialog(
                "KitWright MCP",
                $"Git update started.\n\nSource: {gitReference}\n\nUnity Package Manager will re-fetch the package. The editor may recompile and reload during the update.",
                "OK");
        }

        private static async Task DownloadAndImportUnityPackageAsync(
            InstallContext installContext,
            GitHubReleaseResponse latestRelease,
            string latestVersion)
        {
            var primaryAsset = latestRelease.GetPrimaryAsset();
            if (primaryAsset == null || string.IsNullOrEmpty(primaryAsset.browser_download_url))
            {
                throw new InvalidOperationException("The latest release does not contain a downloadable .unitypackage asset.");
            }

            var tempDirectory = Path.Combine(Path.GetTempPath(), "KitWrightMCP");
            Directory.CreateDirectory(tempDirectory);

            var safeFileName = SanitizeFileName(string.IsNullOrEmpty(primaryAsset.name)
                ? $"KitWrightMCP-v{NormalizeVersion(latestVersion)}.unitypackage"
                : primaryAsset.name);
            var tempPackagePath = Path.Combine(tempDirectory, safeFileName);
            var filteredPackagePath = Path.Combine(
                tempDirectory,
                Path.GetFileNameWithoutExtension(safeFileName) + ".filtered.unitypackage");

            try
            {
                await DownloadFileAsync(primaryAsset.browser_download_url, tempPackagePath, primaryAsset.name);

                SetUpdateProgress("Preparing safe import package...", 0.91f);
                var filterSummary = CreateFilteredUnityPackage(
                    tempPackagePath,
                    filteredPackagePath,
                    installContext.RootPath);
                Debug.Log($"[KitWright MCP] {filterSummary}");

                SetUpdateProgress($"Importing {safeFileName}...", 0.95f);
                EditorUtility.ClearProgressBar();
                ImportUnityPackage(filteredPackagePath, true);

                SetUpdateProgress($"Import window opened for {safeFileName}.", 1f);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                TryDeleteFile(tempPackagePath);
            }
        }

        private static void ImportUnityPackage(string packagePath, bool interactive)
        {
#if UNITY_6000_6_OR_NEWER
            UnityEditor.AssetPackage.Package.Import(packagePath, interactive);
#else
            AssetDatabase.ImportPackage(packagePath, interactive);
#endif
        }

        private static string CreateFilteredUnityPackage(string sourcePath, string destinationPath, string installRootPath)
        {
            var importRoot = NormalizePackagePath(string.IsNullOrWhiteSpace(installRootPath)
                ? DefaultAssetRoot
                : installRootPath);
            if (string.IsNullOrEmpty(importRoot) || !importRoot.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                importRoot = DefaultAssetRoot;

            var entries = ReadUnityPackageEntries(sourcePath);
            var packagePathnames = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.IsPathname)
                    packagePathnames[entry.PackageFolder] = ReadPathname(entry);
            }

            var allowedFolders = new HashSet<string>(StringComparer.Ordinal);
            var importedCount = 0;
            var skippedCount = 0;
            foreach (var kvp in packagePathnames)
            {
                if (IsSafeUnityPackagePath(kvp.Value, importRoot))
                {
                    allowedFolders.Add(kvp.Key);
                    importedCount++;
                }
                else
                {
                    skippedCount++;
                }
            }

            if (importedCount == 0)
                throw new InvalidOperationException($"The downloaded package does not contain any assets under '{importRoot}'.");

            using (var fileStream = File.Create(destinationPath))
            using (var gzipStream = new GZipStream(fileStream, System.IO.Compression.CompressionLevel.Optimal))
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (!entry.IsRoot && !allowedFolders.Contains(entry.PackageFolder))
                        continue;

                    gzipStream.Write(entry.Header, 0, entry.Header.Length);
                    if (entry.PayloadWithPadding.Length > 0)
                        gzipStream.Write(entry.PayloadWithPadding, 0, entry.PayloadWithPadding.Length);
                }

                gzipStream.Write(new byte[1024], 0, 1024);
            }

            return $"Prepared safe unitypackage import for '{importRoot}': {importedCount} plugin entries included, {skippedCount} non-plugin entries skipped.";
        }

        private static List<UnityPackageEntry> ReadUnityPackageEntries(string packagePath)
        {
            var entries = new List<UnityPackageEntry>();
            using (var fileStream = File.OpenRead(packagePath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            {
                while (true)
                {
                    var header = ReadExact(gzipStream, 512);
                    if (header == null || IsZeroBlock(header))
                        break;

                    var size = ParseTarSize(header);
                    if (size > int.MaxValue)
                        throw new InvalidOperationException("The update package contains an entry that is too large to import safely.");

                    var paddingSize = (512 - (size % 512)) % 512;
                    var payload = ReadExact(gzipStream, checked((int)(size + paddingSize))) ?? Array.Empty<byte>();
                    var name = ReadTarName(header);
                    var normalizedName = NormalizeTarEntryName(name);
                    entries.Add(new UnityPackageEntry(header, payload, checked((int)size), normalizedName));
                }
            }

            return entries;
        }

        private static byte[] ReadExact(Stream stream, int count)
        {
            var buffer = new byte[count];
            var offset = 0;
            while (offset < count)
            {
                var read = stream.Read(buffer, offset, count - offset);
                if (read <= 0)
                    return offset == 0 ? null : throw new EndOfStreamException("Unexpected end of unitypackage archive.");

                offset += read;
            }

            return buffer;
        }

        private static bool IsZeroBlock(byte[] block)
        {
            for (int i = 0; i < block.Length; i++)
            {
                if (block[i] != 0)
                    return false;
            }

            return true;
        }

        private static long ParseTarSize(byte[] header)
        {
            long value = 0;
            for (int i = 124; i < 136 && i < header.Length; i++)
            {
                var b = header[i];
                if (b == 0 || b == 32)
                    continue;

                if (b < '0' || b > '7')
                    break;

                value = (value << 3) + (b - '0');
            }

            return value;
        }

        private static string ReadTarName(byte[] header)
        {
            var length = 0;
            while (length < 100 && length < header.Length && header[length] != 0)
                length++;

            return Encoding.UTF8.GetString(header, 0, length);
        }

        private static string NormalizeTarEntryName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            var normalized = name.Replace('\\', '/');
            if (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized.Substring(2);

            return normalized.Trim('/');
        }

        private static string ReadPathname(UnityPackageEntry entry)
        {
            var textLength = entry.PayloadLength;
            while (textLength > 0 && entry.PayloadWithPadding[textLength - 1] == 0)
                textLength--;

            return NormalizePackagePath(Encoding.UTF8.GetString(entry.PayloadWithPadding, 0, textLength).Trim());
        }

        private static string NormalizePackagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            return path.Replace('\\', '/').Trim().Trim('/');
        }

        private static bool IsSafeUnityPackagePath(string packagePath, string importRoot)
        {
            var normalizedPath = NormalizePackagePath(packagePath);
            var normalizedRoot = NormalizePackagePath(importRoot);

            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task DownloadFileAsync(string url, string destinationPath, string assetName)
        {
            using (var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET))
            {
                request.timeout = 60;
                request.SetRequestHeader("User-Agent", "KitWright-Unity-MCP");
                request.downloadHandler = new DownloadHandlerFile(destinationPath);

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    var progress = request.downloadProgress;
                    if (progress < 0f)
                        progress = 0f;

                    SetUpdateProgress(
                        $"Downloading {assetName}...",
                        Mathf.Clamp01(0.55f + progress * 0.35f));
                    await Task.Delay(50);
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException($"Failed to download update package: {request.error}");
                }
            }
        }

        // logFailures stays off for the 6-hourly background check: no published release
        // yet, a rate-limited GitHub API or an offline machine would otherwise drop
        // warnings into the console of someone who never asked for an update check.
        private static async Task<GitHubReleaseResponse> FetchLatestReleaseAsync(bool logFailures)
        {
            using (var request = UnityWebRequest.Get(LatestReleaseApiUrl))
            {
                request.timeout = 15;
                request.SetRequestHeader("User-Agent", "KitWright-Unity-MCP");
                request.SetRequestHeader("Accept", "application/vnd.github+json");

                var operation = request.SendWebRequest();
                while (!operation.isDone)
                    await Task.Delay(50);

                if (request.result != UnityWebRequest.Result.Success)
                {
                    if (logFailures)
                        Debug.LogWarning($"[KitWright MCP] Update check failed: {request.error}");
                    return null;
                }

                return JsonUtility.FromJson<GitHubReleaseResponse>(request.downloadHandler.text);
            }
        }

        private static void SetUpdateProgress(string message, float progress)
        {
            _statusMessage = message;
            _progress = Mathf.Clamp01(progress);
            NotifyStateChanged();
            EditorUtility.DisplayProgressBar("KitWright MCP", message, _progress);
        }

        private static void EnsureStateLoaded()
        {
            if (_stateLoaded)
                return;

            _stateLoaded = true;
            _latestVersion = EditorPrefs.GetString(BuildPrefsKey("LatestVersion"), string.Empty);
            _latestReleaseUrl = EditorPrefs.GetString(BuildPrefsKey("LatestReleaseUrl"), string.Empty);
            _installDescription = EditorPrefs.GetString(BuildPrefsKey("InstallDescription"), string.Empty);

            if (!HasNewerCachedVersion(PackageVersion.Current))
                ClearCachedUpdate(false);
        }

        private static bool IsAutoCheckDue()
        {
            var ticksString = EditorPrefs.GetString(BuildPrefsKey("LastAutoCheckUtcTicks"), string.Empty);
            if (!long.TryParse(ticksString, out var ticks))
                return true;

            var lastCheckUtc = new DateTime(ticks, DateTimeKind.Utc);
            return DateTime.UtcNow - lastCheckUtc >= TimeSpan.FromHours(AutoCheckIntervalHours);
        }

        private static void RecordAutoCheckAttempt()
        {
            EditorPrefs.SetString(BuildPrefsKey("LastAutoCheckUtcTicks"), DateTime.UtcNow.Ticks.ToString());
        }

        private static void StoreAvailableUpdate(
            GitHubReleaseResponse latestRelease,
            string latestVersion,
            InstallContext installContext)
        {
            _latestRelease = latestRelease;
            _latestInstallContext = installContext;
            _latestVersion = NormalizeVersion(latestVersion);
            _latestReleaseUrl = string.IsNullOrEmpty(latestRelease?.html_url)
                ? DefaultReleasesUrl
                : latestRelease.html_url;
            _installDescription = installContext.Description;

            EditorPrefs.SetString(BuildPrefsKey("LatestVersion"), _latestVersion);
            EditorPrefs.SetString(BuildPrefsKey("LatestReleaseUrl"), _latestReleaseUrl);
            EditorPrefs.SetString(BuildPrefsKey("InstallDescription"), _installDescription);
        }

        private static void ClearCachedUpdate(bool notify = true)
        {
            _latestVersion = string.Empty;
            _latestReleaseUrl = string.Empty;
            _installDescription = string.Empty;
            _latestRelease = null;
            _latestInstallContext = default;
            _updateStarted = false;

            EditorPrefs.DeleteKey(BuildPrefsKey("LatestVersion"));
            EditorPrefs.DeleteKey(BuildPrefsKey("LatestReleaseUrl"));
            EditorPrefs.DeleteKey(BuildPrefsKey("InstallDescription"));

            if (notify)
                NotifyStateChanged();
        }

        private static bool HasNewerCachedVersion(string currentVersion)
        {
            if (string.IsNullOrEmpty(_latestVersion))
                return false;

            return ParseComparableVersion(_latestVersion) > ParseComparableVersion(currentVersion);
        }

        private static string BuildPrefsKey(string suffix)
        {
            return $"{PrefsPrefix}.{GetProjectHash()}.{suffix}";
        }

        private static string GetProjectHash()
        {
            unchecked
            {
                uint hash = 2166136261;
                var value = Application.dataPath ?? string.Empty;
                for (int i = 0; i < value.Length; i++)
                    hash = (hash ^ value[i]) * 16777619;

                return hash.ToString("x8");
            }
        }

        private static void NotifyStateChanged()
        {
            StateChanged?.Invoke();
        }

        private static InstallContext ResolveInstallContext()
        {
            var packageInfo = UnityPackageInfo.FindForAssetPath(PackageRoot);
            if (packageInfo != null &&
                string.Equals(packageInfo.name, PackageName, StringComparison.OrdinalIgnoreCase))
            {
                if (packageInfo.source == PackageSource.Git)
                {
                    return new InstallContext(
                        InstallMode.GitPackage,
                        packageInfo.assetPath,
                        "Git package");
                }

                return new InstallContext(
                    InstallMode.UnsupportedPackage,
                    packageInfo.assetPath,
                    $"UPM package ({packageInfo.source})");
            }

            var installedRootPath = FindInstalledRootPath();
            if (!string.IsNullOrEmpty(installedRootPath) &&
                installedRootPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return new InstallContext(
                    InstallMode.UnityPackageImport,
                    installedRootPath,
                    "unitypackage import");
            }

            if (AssetDatabase.IsValidFolder(DefaultAssetRoot))
            {
                return new InstallContext(
                    InstallMode.UnityPackageImport,
                    DefaultAssetRoot,
                    "unitypackage import");
            }

            return new InstallContext(InstallMode.Unknown, string.Empty, "unknown");
        }

        private static string FindInstalledRootPath()
        {
            var guids = AssetDatabase.FindAssets("MCPServerService t:MonoScript");
            for (int i = 0; i < guids.Length; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (string.IsNullOrEmpty(path) ||
                    !path.EndsWith(ScriptRootSuffix, StringComparison.OrdinalIgnoreCase) ||
                    path.Length <= ScriptRootSuffix.Length)
                {
                    continue;
                }

                return path.Substring(0, path.Length - ScriptRootSuffix.Length);
            }

            return null;
        }

        private static string NormalizeVersion(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return "0.0.0";

            return version.Trim().TrimStart('v', 'V');
        }

        private static string BuildUpdateActionMessage(InstallContext installContext)
        {
            switch (installContext.Mode)
            {
                case InstallMode.GitPackage:
                    return "A newer version is available.\nChoosing Update Now will re-pull the package from Git through Unity Package Manager.";
                default:
                    return "A newer version is available.\nChoosing Update Now will download the latest unitypackage and open Unity's import window.";
            }
        }

        internal static Version ParseComparableVersion(string version)
        {
            // Version.TryParse reads the numeric core only, so a -rc / +build suffix is cut first.
            var core = NormalizeVersion(version).Split('-', '+')[0];
            return Version.TryParse(core, out var parsed) ? parsed : new Version(0, 0, 0);
        }

        private static string SanitizeFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return "KitWrightMCP.unitypackage";

            var sanitized = fileName;
            var invalidCharacters = Path.GetInvalidFileNameChars();
            for (int i = 0; i < invalidCharacters.Length; i++)
            {
                sanitized = sanitized.Replace(invalidCharacters[i], '_');
            }

            return sanitized;
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best-effort temp file cleanup.
            }
        }

        public sealed class UpdateStateSnapshot
        {
            public readonly string CurrentVersion;
            public readonly string LatestVersion;
            public readonly string LatestReleaseUrl;
            public readonly string InstallDescription;
            public readonly string StatusMessage;
            public readonly bool HasUpdateAvailable;
            public readonly bool IsChecking;
            public readonly bool IsUpdating;
            public readonly bool UpdateStarted;
            public readonly float Progress;

            public UpdateStateSnapshot(
                string currentVersion,
                string latestVersion,
                string latestReleaseUrl,
                string installDescription,
                string statusMessage,
                bool hasUpdateAvailable,
                bool isChecking,
                bool isUpdating,
                bool updateStarted,
                float progress)
            {
                CurrentVersion = currentVersion;
                LatestVersion = latestVersion;
                LatestReleaseUrl = latestReleaseUrl;
                InstallDescription = installDescription;
                StatusMessage = statusMessage;
                HasUpdateAvailable = hasUpdateAvailable;
                IsChecking = isChecking;
                IsUpdating = isUpdating;
                UpdateStarted = updateStarted;
                Progress = progress;
            }
        }

        private sealed class UnityPackageEntry
        {
            public readonly byte[] Header;
            public readonly byte[] PayloadWithPadding;
            public readonly int PayloadLength;
            public readonly string Name;
            public readonly string PackageFolder;

            public bool IsRoot => string.IsNullOrEmpty(Name);
            public bool IsPathname => !string.IsNullOrEmpty(PackageFolder) &&
                                      Name.EndsWith("/pathname", StringComparison.Ordinal);

            public UnityPackageEntry(byte[] header, byte[] payloadWithPadding, int payloadLength, string name)
            {
                Header = header;
                PayloadWithPadding = payloadWithPadding;
                PayloadLength = payloadLength;
                Name = name ?? string.Empty;
                PackageFolder = ResolvePackageFolder(Name);
            }

            private static string ResolvePackageFolder(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return string.Empty;

                var slashIndex = name.IndexOf('/');
                return slashIndex < 0 ? name : name.Substring(0, slashIndex);
            }
        }

        [Serializable]
        private sealed class GitHubReleaseResponse
        {
            public string tag_name;
            public string html_url;
            public string published_at;
            public GitHubReleaseAsset[] assets;

            public GitHubReleaseAsset GetPrimaryAsset()
            {
                if (assets == null)
                    return null;

                for (int i = 0; i < assets.Length; i++)
                {
                    var asset = assets[i];
                    if (asset == null ||
                        string.IsNullOrEmpty(asset.browser_download_url) ||
                        string.IsNullOrEmpty(asset.name))
                    {
                        continue;
                    }

                    if (asset.name.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase))
                        return asset;
                }

                return null;
            }
        }

        [Serializable]
        private sealed class GitHubReleaseAsset
        {
            public string name;
            public string browser_download_url;
        }

        private enum InstallMode
        {
            Unknown,
            GitPackage,
            UnityPackageImport,
            UnsupportedPackage
        }

        private readonly struct InstallContext
        {
            public readonly InstallMode Mode;
            public readonly string RootPath;
            public readonly string Description;

            public InstallContext(InstallMode mode, string rootPath, string description)
            {
                Mode = mode;
                RootPath = rootPath;
                Description = string.IsNullOrEmpty(rootPath)
                    ? description
                    : $"{description} ({rootPath})";
            }
        }

    }
}
