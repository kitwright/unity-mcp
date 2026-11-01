// Copyright (C) KitWright. Licensed under MIT.

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static KitWright.Editor.Tests.ToolCall;

namespace KitWright.Editor.Tests
{
    public sealed class PlayModeSceneEditTests
    {
        private const string ObjectName = "KwPlayModeEditProbe";

        // EditorSceneManager.MarkSceneDirty throws "This cannot be used during play mode", and these tools
        // called it after the change was already applied: the edit landed and the reply was FUNCTION_FAILED.
        [UnityTest]
        public IEnumerator SceneEditingToolsAnswerInPlayModeInsteadOfFailingAfterTheEdit()
        {
            yield return new EnterPlayMode();

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = ObjectName;
            go.GetComponent<Renderer>().material = new Material(Shader.Find("Sprites/Default"));
            try
            {
                var lighting = Ok("set_lighting_settings", "fog", "false");
                StringAssert.Contains("discarded", (string)lighting["message"]);

                Ok("set_material_property", "path", ObjectName, "name", "_Color", "value", "1,0,0,1");
            }
            finally
            {
                Object.DestroyImmediate(go);
            }

            yield return new ExitPlayMode();
        }
    }
}
