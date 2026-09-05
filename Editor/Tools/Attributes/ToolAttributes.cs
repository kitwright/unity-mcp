// Copyright (C) KitWright. Licensed under MIT.
using System;

namespace KitWright.Editor.Tools
{
    /// Marks a static class whose public static methods are exposed as MCP tools.
    /// Public so project and third-party editor assemblies can declare their own tools;
    /// <see cref="ToolRegistry"/> scans every loaded assembly, not just this package.
    [AttributeUsage(AttributeTargets.Class)]
    public class ToolProviderAttribute : Attribute
    {
        public string Category { get; }

        public ToolProviderAttribute(string category = null)
        {
            Category = category;
        }
    }

    /// The package id a tool needs at runtime, on the provider class or on a single method.
    /// Use it where the code still compiles without the package and answers with a "not installed"
    /// error instead: the Tool Exposure editor greys those tools out so nobody switches on a tool
    /// that cannot run. Tools that simply vanish with their package (an asmdef defineConstraint, or
    /// a whole class inside #if) need nothing here.
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public class RequiresPackageAttribute : Attribute
    {
        public string PackageId { get; }

        public RequiresPackageAttribute(string packageId)
        {
            PackageId = packageId;
        }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public class ToolParamAttribute : Attribute
    {
        public string Description { get; }
        public bool Required { get; set; } = true;
        public string DefaultValue { get; set; }

        public ToolParamAttribute(string description)
        {
            Description = description;
        }
    }

    /// Functions with this attribute do not modify the scene or project.
    [AttributeUsage(AttributeTargets.Method)]
    public class ReadOnlyToolAttribute : Attribute { }

    /// Runs the tool on the request thread instead of queueing it for the editor loop.
    /// Only for tools that touch no Unity API, because the point is to still answer while a
    /// modal dialog owns the editor loop and nothing queued can run.
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class OffEditorThreadAttribute : Attribute { }

    /// A tool that legitimately blocks the editor for minutes (a player build, a NavMesh bake).
    /// The transports widen their per-request timeout to this budget instead of answering
    /// "Request timeout" while the work is still running and the main thread is pinned.
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class LongRunningToolAttribute : Attribute
    {
        public int Seconds { get; }

        public LongRunningToolAttribute(int seconds)
        {
            Seconds = seconds;
        }
    }
}
