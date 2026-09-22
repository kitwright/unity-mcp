// Copyright (C) KitWright. All rights reserved.

using KitWright.Editor.DI;
using UnityEngine;

namespace KitWright.Editor.Settings
{
    internal static class PluginDebugLogger
    {
        public static void Log(string message)
        {
            if (!IsEnabled || string.IsNullOrEmpty(message))
                return;

            Debug.Log(message);
        }

        public static bool IsEnabled
        {
            get
            {
                try
                {
                    var settings = RootScopeServices.Services?.GetService(typeof(SettingsController)) as SettingsController;
                    return settings?.PluginDebugLoggingEnabled == true;
                }
                catch
                {
                    return false;
                }
            }
        }
    }
}
