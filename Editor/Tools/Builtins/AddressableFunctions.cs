// Copyright (C) KitWright. All rights reserved.

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
#if KITWRIGHT_ADDRESSABLES
using System.Linq;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
#endif

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("Addressable")]
    [RequiresPackage("com.unity.addressables")]
    internal static class AddressableFunctions
    {
        private const string NoPackageHint =
            "This project has no Addressables package. Install 'com.unity.addressables' and open " +
            "Window > Asset Management > Addressables > Groups once to create the settings asset, then retry.";

#if !KITWRIGHT_ADDRESSABLES
        private static object NoPackage() => Response.Error("ADDRESSABLES_REQUIRED", new { hint = NoPackageHint });
#endif

        internal static object NotAddressable(string path, string reason = null) =>
            reason == null
                ? Response.Error("ASSET_NOT_ADDRESSABLE", new { path },
                    "Addressables returned no entry for this asset, so nothing was marked. Pick an asset that ships in the player.")
                : Response.Error("ASSET_NOT_ADDRESSABLE", new { path, reason },
                    "Nothing was marked. Pick an asset that ships in the player.");

        // Why a player cannot serve this path, or null. Checked before any group is created, because
        // Addressables either returns no entry for these or accepts them and fails the build.
        internal static string UnservableReason(string path)
        {
            var normalized = (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
            if (string.Equals(normalized, "Assets", System.StringComparison.OrdinalIgnoreCase))
                return "The Assets folder itself cannot be an Addressable entry; mark a subfolder or the assets inside it.";

            if (!normalized.StartsWith("Assets/", System.StringComparison.OrdinalIgnoreCase) &&
                !normalized.StartsWith("Packages/", System.StringComparison.OrdinalIgnoreCase))
                return "Only assets under Assets/ or Packages/ can be Addressable; built-in resources and other folders are not served from a player.";

            if (string.Equals(System.IO.Path.GetExtension(normalized), ".cs", System.StringComparison.OrdinalIgnoreCase))
                return "A script is compiled into the player's code and is not an asset a bundle can hold.";

            foreach (var segment in normalized.Split('/'))
            {
                if (string.Equals(segment, "Editor", System.StringComparison.OrdinalIgnoreCase))
                    return "Anything under an Editor folder is stripped from player builds, so it cannot be loaded at runtime.";
            }

            return null;
        }

        [Description("Mark an asset as Addressable and set its address key. Optionally place it in a named group (created if missing). Requires the Addressables package.")]
        public static object MarkAddressable(
            [ToolParam("Project-relative asset path (e.g. 'Assets/Prefabs/Enemy.prefab')")] string path,
            [ToolParam("Address key (default: the asset path)", Required = false)] string address = null,
            [ToolParam("Group name to place the entry in", Required = false)] string group = null)
        {
#if KITWRIGHT_ADDRESSABLES
            if (!TryGetSettings(out var settings, out var err)) return err;

            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return Response.Error("ASSET_NOT_FOUND", new { path });

            var unservable = UnservableReason(path);
            if (unservable != null) return NotAddressable(path, unservable);

            var createdGroup = !string.IsNullOrEmpty(group) && settings.FindGroup(group) == null;
            var targetGroup = string.IsNullOrEmpty(group) ? settings.DefaultGroup : GetOrCreateGroup(settings, group);
            var entry = settings.CreateOrMoveEntry(guid, targetGroup);
            if (entry == null)
            {
                if (createdGroup)
                    settings.RemoveGroup(targetGroup);
                return NotAddressable(path);
            }

            entry.address = string.IsNullOrEmpty(address) ? path : address;
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);
            AssetDatabase.SaveAssets();

            return Response.Success($"Marked '{path}' addressable.", new { path, address = entry.address, group = targetGroup.Name });
#else
            return NoPackage();
#endif
        }

        [Description("Remove an asset's Addressable entry (unmark it). Requires the Addressables package.")]
        public static object UnmarkAddressable(
            [ToolParam("Project-relative asset path")] string path)
        {
#if KITWRIGHT_ADDRESSABLES
            if (!TryGetSettings(out var settings, out var err)) return err;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return Response.Error("ASSET_NOT_FOUND", new { path });
            if (settings.FindAssetEntry(guid) == null) return Response.Error("NOT_ADDRESSABLE", new { path });

            settings.RemoveAssetEntry(guid);
            AssetDatabase.SaveAssets();
            return Response.Success($"Unmarked '{path}'.", new { path });
#else
            return NoPackage();
#endif
        }

        [Description("Set the address key of an already-addressable asset. Requires the Addressables package.")]
        public static object SetAddressableAddress(
            [ToolParam("Project-relative asset path")] string path,
            [ToolParam("New address key")] string address)
        {
#if KITWRIGHT_ADDRESSABLES
            if (!TryGetEntry(path, out var settings, out var entry, out var err)) return err;
            entry.address = address;
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);
            AssetDatabase.SaveAssets();
            return Response.Success($"Address set to '{address}'.", new { path, address });
#else
            return NoPackage();
#endif
        }

        [Description("Add or remove a label on an addressable asset. Labels let you load groups of assets together. Requires the Addressables package.")]
        public static object SetAddressableLabel(
            [ToolParam("Project-relative asset path")] string path,
            [ToolParam("Label name")] string label,
            [ToolParam("true to add, false to remove", Required = false)] bool add = true)
        {
#if KITWRIGHT_ADDRESSABLES
            if (!TryGetEntry(path, out var settings, out var entry, out var err)) return err;

            if (add && !settings.GetLabels().Contains(label))
                settings.AddLabel(label);
            entry.SetLabel(label, add, force: true);
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryModified, entry, true);
            AssetDatabase.SaveAssets();

            return Response.Success($"{(add ? "Added" : "Removed")} label '{label}'.", new { path, label, labels = entry.labels.ToArray() });
#else
            return NoPackage();
#endif
        }

        [Description("List all Addressable groups and their entry counts. Requires the Addressables package.")]
        [ReadOnlyTool]
        public static object ListAddressableGroups()
        {
#if KITWRIGHT_ADDRESSABLES
            if (!TryGetSettings(out var settings, out var err)) return err;
            var groups = settings.groups
                .Where(g => g != null)
                .Select(g => new { name = g.Name, entryCount = g.entries.Count, isDefault = g == settings.DefaultGroup })
                .ToArray();
            return Response.Success($"{groups.Length} group(s).", groups);
#else
            return NoPackage();
#endif
        }

        [Description("Get an asset's Addressable info: whether it's addressable, its address, group, and labels. Requires the Addressables package.")]
        [ReadOnlyTool]
        public static object GetAddressableInfo(
            [ToolParam("Project-relative asset path")] string path)
        {
#if KITWRIGHT_ADDRESSABLES
            if (!TryGetSettings(out var settings, out var err)) return err;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) return Response.Error("ASSET_NOT_FOUND", new { path });

            var entry = settings.FindAssetEntry(guid);
            if (entry == null)
                return Response.Success($"'{path}' is not addressable.", new { path, addressable = false });

            return Response.Success($"'{path}' is addressable.", new
            {
                path,
                addressable = true,
                address = entry.address,
                group = entry.parentGroup?.Name,
                labels = entry.labels.ToArray()
            });
#else
            return NoPackage();
#endif
        }

#if KITWRIGHT_ADDRESSABLES
        private static bool TryGetSettings(out AddressableAssetSettings settings, out object error)
        {
            error = null;
            settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                error = Response.Error("NO_ADDRESSABLE_SETTINGS", new { hint = "Open Window > Asset Management > Addressables > Groups once to create the settings asset." });
                return false;
            }
            return true;
        }

        private static bool TryGetEntry(string path, out AddressableAssetSettings settings, out AddressableAssetEntry entry, out object error)
        {
            entry = null;
            if (!TryGetSettings(out settings, out error)) return false;
            var guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid)) { error = Response.Error("ASSET_NOT_FOUND", new { path }); return false; }
            entry = settings.FindAssetEntry(guid);
            if (entry == null) { error = Response.Error("NOT_ADDRESSABLE", new { path, hint = "Call mark_addressable first." }); return false; }
            return true;
        }

        private static AddressableAssetGroup GetOrCreateGroup(AddressableAssetSettings settings, string name)
        {
            var existing = settings.groups.FirstOrDefault(g => g != null && g.Name == name);
            if (existing != null) return existing;
            return settings.CreateGroup(name, false, false, false, settings.DefaultGroup.Schemas);
        }
#endif
    }
}
