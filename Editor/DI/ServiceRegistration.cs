// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.MCP.Server;
using KitWright.Editor.Services;
using KitWright.Editor.Services.UnityLogs;
using KitWright.Editor.Settings;
using KitWright.Editor.State;
using KitWright.Editor.Threading;
using KitWright.Editor.Tools;

namespace KitWright.Editor.DI
{
    internal static class ServiceRegistration
    {
        public static ServiceCollection RegisterServices(this ServiceCollection services)
        {
            // Core Infrastructure (Singletons)
            services.AddSingleton<EditorContextBuilder, EditorContextBuilder>();
            services.AddSingleton<SettingsController, SettingsController>();
            services.AddSingleton<EditorThreadHelper, EditorThreadHelper>();

            // Services (Singletons)
            services.AddSingleton<CompilationService, CompilationService>();
            services.AddSingleton<UnityLogsRepository, UnityLogsRepository>();
            services.AddSingleton<FunctionInvoker, FunctionInvoker>();

            // MCP Server (Singleton)
            services.AddSingleton<MCPServerService, MCPServerService>();

            // State (Singleton)
            services.AddSingleton<StateController, StateController>();

            return services;
        }
    }
}
