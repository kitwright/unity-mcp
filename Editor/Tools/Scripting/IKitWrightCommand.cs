// Copyright (C) KitWright. All rights reserved.

namespace KitWright.Editor.Tools.Scripting
{
    /// <summary>
    /// Implement this in a code snippet passed to <c>execute_code</c> to opt into
    /// the structured execution path: automatic Undo registration, change tracking,
    /// and structured log capture.
    ///
    /// Template:
    /// <code>
    /// using UnityEngine;
    /// using UnityEditor;
    /// using KitWright.Editor.Tools.Scripting;
    ///
    /// public class CommandScript : IKitWrightCommand
    /// {
    ///     public void Execute(ExecutionContext ctx)
    ///     {
    ///         var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
    ///         ctx.RegisterObjectCreation(go);          // Undo + tracking
    ///         ctx.Log("Created {0}", go.name);
    ///     }
    /// }
    /// </code>
    /// </summary>
    public interface IKitWrightCommand
    {
        void Execute(ExecutionContext ctx);
    }
}
