// Copyright (C) KitWright. All rights reserved.

#if KITWRIGHT_INPUTSYSTEM
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("InputActions")]
    internal static class InputActionAssetFunctions
    {
        [Description("Create a new empty Input Actions asset (.inputactions) for the New Input System. Optionally seed it with one action map.")]
        public static object CreateInputActions(
            [ToolParam("Save path ending in .inputactions (e.g. 'Assets/Input/GameControls.inputactions')")] string path,
            [ToolParam("Optional first action map name to create", Required = false)] string first_map = null)
        {
            if (!IsInputActionsPath(path))
                return Response.Error("INVALID_PATH", new { path, hint = "Path must end with .inputactions" });

            if (File.Exists(path))
                return Response.Error("ALREADY_EXISTS", new { path });

            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                if (!string.IsNullOrEmpty(first_map))
                    asset.AddActionMap(new InputActionMap(first_map));

                WriteAsset(asset, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }

            return Response.Success($"Created input actions asset at {path}.", new { path, first_map });
        }

        [Description("Add an action map (group of related actions like 'Player' or 'UI') to an existing .inputactions asset.")]
        public static object AddInputMap(
            [ToolParam("Path to the .inputactions asset")] string path,
            [ToolParam("Action map name")] string map_name)
        {
            if (string.IsNullOrEmpty(map_name))
                return Response.Error("INVALID_ARGUMENT", new { map_name });

            return WithAsset(path, asset =>
            {
                if (asset.FindActionMap(map_name) != null)
                    return Response.Error("MAP_EXISTS", new { map_name });

                asset.AddActionMap(new InputActionMap(map_name));
                return Response.Success($"Added action map '{map_name}'.", new { path, map_name });
            });
        }

        [Description("Add an action (like 'Jump' or 'Move') to an action map. Type is button, value, or passthrough.")]
        public static object AddInputAction(
            [ToolParam("Path to the .inputactions asset")] string path,
            [ToolParam("Action map name (must already exist)")] string map_name,
            [ToolParam("Action name")] string action_name,
            [ToolParam("Action type: button, value, or passthrough", Required = false)] string type = "button")
        {
            if (string.IsNullOrEmpty(action_name))
                return Response.Error("INVALID_ARGUMENT", new { action_name });

            var actionType = ResolveActionType(type);

            return WithAsset(path, asset =>
            {
                var map = asset.FindActionMap(map_name);
                if (map == null) return Response.Error("MAP_NOT_FOUND", new { map_name });
                if (map.FindAction(action_name) != null) return Response.Error("ACTION_EXISTS", new { action_name });

                map.AddAction(action_name, actionType);
                return Response.Success($"Added {actionType} action '{action_name}' to '{map_name}'.",
                    new { path, map_name, action_name, type = actionType.ToString() });
            });
        }

        [Description("Add a simple binding (single control path like '<Keyboard>/space' or '<Gamepad>/buttonSouth') to an action.")]
        public static object AddInputBinding(
            [ToolParam("Path to the .inputactions asset")] string path,
            [ToolParam("Action map name")] string map_name,
            [ToolParam("Action name (must already exist in the map)")] string action_name,
            [ToolParam("Control path, e.g. '<Keyboard>/space' or '<Gamepad>/leftStick'")] string binding)
        {
            if (string.IsNullOrEmpty(binding))
                return Response.Error("INVALID_ARGUMENT", new { binding });

            return WithAsset(path, asset =>
            {
                var map = asset.FindActionMap(map_name);
                if (map == null) return Response.Error("MAP_NOT_FOUND", new { map_name });
                var action = map.FindAction(action_name);
                if (action == null) return Response.Error("ACTION_NOT_FOUND", new { action_name });

                action.AddBinding(binding);
                return Response.Success($"Added binding '{binding}' to '{action_name}'.",
                    new { path, map_name, action_name, binding });
            });
        }

        [Description("Add a composite binding (combines several controls into one, like WASD into a 2D vector) to an action. " +
                     "Specify parts as 'Name=path' pairs separated by ';', e.g. 'Up=<Keyboard>/w;Down=<Keyboard>/s;Left=<Keyboard>/a;Right=<Keyboard>/d'.")]
        public static object AddInputCompositeBinding(
            [ToolParam("Path to the .inputactions asset")] string path,
            [ToolParam("Action map name")] string map_name,
            [ToolParam("Action name (must already exist in the map)")] string action_name,
            [ToolParam("Composite type: '2DVector', 'Axis', '1DAxis', or '3DVector'")] string composite_type,
            [ToolParam("Parts as 'Name=path' pairs separated by ';'")] string parts)
        {
            if (string.IsNullOrEmpty(composite_type))
                return Response.Error("INVALID_ARGUMENT", new { composite_type });

            var parsed = ParseCompositeParts(parts);
            if (parsed.Count == 0)
                return Response.Error("INVALID_COMPOSITE_PARTS", new { parts, hint = "Use 'Up=<Keyboard>/w;Down=<Keyboard>/s'" });

            return WithAsset(path, asset =>
            {
                var map = asset.FindActionMap(map_name);
                if (map == null) return Response.Error("MAP_NOT_FOUND", new { map_name });
                var action = map.FindAction(action_name);
                if (action == null) return Response.Error("ACTION_NOT_FOUND", new { action_name });

                var syntax = action.AddCompositeBinding(composite_type);
                foreach (var kvp in parsed)
                    syntax = syntax.With(kvp.Key, kvp.Value);

                return Response.Success($"Added {composite_type} composite to '{action_name}'.",
                    new { path, map_name, action_name, composite_type, partCount = parsed.Count });
            });
        }

        [Description("List the structure of an .inputactions asset: action maps, their actions (with type), and each action's binding count.")]
        [ReadOnlyTool]
        public static object GetInputActionsInfo(
            [ToolParam("Path to the .inputactions asset")] string path)
        {
            return WithAsset(path, asset =>
            {
                var maps = asset.actionMaps.Select(m => new
                {
                    name = m.name,
                    actions = m.actions.Select(a => new
                    {
                        name = a.name,
                        type = a.type.ToString(),
                        bindingCount = a.bindings.Count
                    }).ToArray()
                }).ToArray();

                return Response.Success($"'{Path.GetFileName(path)}' has {maps.Length} action map(s).", new { path, maps });
            }, readOnly: true);
        }

        // ----- helpers -----

        internal static bool IsInputActionsPath(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(".inputactions", StringComparison.OrdinalIgnoreCase);
        }

        internal static InputActionType ResolveActionType(string type)
        {
            switch (type?.Trim().ToLowerInvariant())
            {
                case "value": return InputActionType.Value;
                case "passthrough": return InputActionType.PassThrough;
                default: return InputActionType.Button;
            }
        }

        internal static List<KeyValuePair<string, string>> ParseCompositeParts(string parts)
        {
            var result = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrWhiteSpace(parts)) return result;

            foreach (var chunk in parts.Split(';'))
            {
                var trimmed = chunk.Trim();
                if (trimmed.Length == 0) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0 || eq >= trimmed.Length - 1) continue;
                var name = trimmed.Substring(0, eq).Trim();
                var pathValue = trimmed.Substring(eq + 1).Trim();
                if (name.Length == 0 || pathValue.Length == 0) continue;
                result.Add(new KeyValuePair<string, string>(name, pathValue));
            }

            return result;
        }

        private static object WithAsset(string path, Func<InputActionAsset, object> action, bool readOnly = false)
        {
            if (!IsInputActionsPath(path))
                return Response.Error("INVALID_PATH", new { path, hint = "Path must end with .inputactions" });
            if (!File.Exists(path))
                return Response.Error("ASSET_NOT_FOUND", new { path });

            var json = File.ReadAllText(path);
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            try
            {
                asset.LoadFromJson(json);
                var result = action(asset);

                if (!readOnly && IsSuccess(result))
                    WriteAsset(asset, path);

                return result;
            }
            catch (Exception ex)
            {
                return Response.Error("INPUT_ASSET_ERROR", new { path, error = ex.Message });
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        private static void WriteAsset(InputActionAsset asset, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, ToJson(asset, path));
            AssetDatabase.ImportAsset(path);
        }

        // InputActionAsset.ToJson() runs LINQ's Count() straight over its map array, which stays null
        // until the first map is added - so serializing an asset with no maps throws
        // ArgumentNullException instead of writing an empty document. That is exactly what
        // create_input_actions does when no first_map is given, so write that document by hand.
        private static string ToJson(InputActionAsset asset, string path)
        {
            if (asset.actionMaps.Count > 0)
                return asset.ToJson();

            return "{\n    \"name\": \"" + Path.GetFileNameWithoutExtension(path) +
                   "\",\n    \"maps\": [],\n    \"controlSchemes\": []\n}";
        }

        private static bool IsSuccess(object result)
        {
            var prop = result?.GetType().GetProperty("success");
            return prop != null && prop.GetValue(result) is bool b && b;
        }
    }
}
#endif
