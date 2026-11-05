// Copyright (C) KitWright. Licensed under MIT.
using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    [ToolProvider("GameObject")]
    internal static class AttachScriptFunctions
    {
        [Description("Add a MonoBehaviour to a GameObject from its script asset path; add_component takes a type name instead. " +
                     "The class is read from the MonoScript, so it may be named differently from the file. A script that has not " +
                     "compiled yet answers SCRIPT_NOT_COMPILED: request_recompile, then retry.")]
        public static object AttachScript(
            [ToolParam("Identifier of the GameObject")] string target,
            [ToolParam("Script asset path, e.g. 'Assets/Scripts/Player.cs'")] string script_path,
            [ToolParam("How to resolve target: " + ObjectsHelper.FindMethodValues, Required = false)] string find_method = null)
        {
            var go = ObjectsHelper.FindObject(target, find_method);
            if (go == null)
                return ObjectsHelper.NotFound("target", target, find_method);

            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(script_path);
            if (script == null)
                return Response.Error("SCRIPT_NOT_FOUND", new { script_path });

            var type = script.GetClass();
            if (type == null)
                return Response.Error("SCRIPT_NOT_COMPILED", new { script_path },
                    "The file has no compiled class yet, or its class name differs from what Unity expects. Run request_recompile and retry.");

            if (!typeof(MonoBehaviour).IsAssignableFrom(type) || type.IsAbstract)
                return Response.Error("NOT_A_MONOBEHAVIOUR", new { script_path, type = type.FullName },
                    "Only a concrete MonoBehaviour can be attached to a GameObject.");

            var component = Undo.AddComponent(go, type);
            if (component == null)
                return Response.Error("ADD_COMPONENT_FAILED", new { script_path, target = go.name },
                    "Unity refused the component: it may conflict with one already on the GameObject (a [DisallowMultipleComponent] or [RequireComponent] rule).");

            return Response.Success($"Attached {type.Name} to '{go.name}'.", new
            {
                gameObjectInstanceId = ObjectIdCodec.GetSerializableId(go),
                componentInstanceId = ObjectIdCodec.GetSerializableId(component),
                type = type.FullName
            });
        }
    }
}
