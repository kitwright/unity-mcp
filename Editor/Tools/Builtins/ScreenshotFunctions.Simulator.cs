// Copyright (C) KitWright. All rights reserved.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using DescriptionAttribute = System.ComponentModel.DescriptionAttribute;
using KitWright.Editor.Tools.Helpers;
using UnityEditor;
using UnityEngine;

namespace KitWright.Editor.Tools.Builtins
{
    internal static partial class ScreenshotFunctions
    {
        [Description("Capture the Unity Device Simulator screen. Optionally selects a Simulator device and draws a Safe Area outline over the captured image. " +
                     "If the Simulator view has to be opened or the device changes, retry once after Unity renders the next editor frame.")]
        [ReadOnlyTool]
        public static async Task<string> CaptureSimulatorView(
            [ToolParam("Width of the screenshot in pixels. Default uses the simulator screen texture size. If only width is provided, height preserves source aspect.", Required = false)] int width = 0,
            [ToolParam("Height of the screenshot in pixels. Default uses the simulator screen texture size. If only height is provided, width preserves source aspect.", Required = false)] int height = 0,
            [ToolParam("Simulator device name to select before capture, e.g. 'iPhone 12' or 'Apple iPad Pro 12.9 (2018)'.", Required = false)] string device_name = null,
            [ToolParam("Draw a high-contrast Safe Area outline on top of the captured simulator image.", Required = false)] bool safe_area_overlay = false,
            [ToolParam("Open the Simulator view if no Simulator window is currently open.", Required = false)] bool open_if_needed = true,
            [ToolParam(SaveToFileParamDescription, Required = false)] bool save_to_file = false,
            [ToolParam(OutputPathParamDescription, Required = false)] string output_path = null)
        {
            if (!TryGetSimulatorWindow(open_if_needed, out var simulatorWindow, out var simulatorError))
                return ToolResultFormatter.Error("SIMULATOR_VIEW_NOT_AVAILABLE", simulatorError);

            if (simulatorWindow is EditorWindow editorWindow)
            {
                editorWindow.Show();
                editorWindow.Focus();
                editorWindow.Repaint();
            }

            if (!TrySelectSimulatorDevice(simulatorWindow, device_name, out var deviceChanged, out var deviceSelectionError))
                return ToolResultFormatter.Error("SIMULATOR_DEVICE_NOT_FOUND", deviceSelectionError);

            if (deviceChanged)
                await WaitForEditorFramesAsync(2);

            var deviceView = GetSimulatorDeviceView(simulatorWindow);
            if (deviceView == null)
            {
                return ToolResultFormatter.Error("SIMULATOR_DEVICE_VIEW_UNAVAILABLE", new
                {
                    hint = "The Simulator window is available, but its internal DeviceView could not be resolved. Try updating Unity or use capture_game_view instead."
                });
            }

            var sourceTexture = GetPropertyOrField<Texture>(deviceView, "PreviewTexture");
            if ((sourceTexture == null || sourceTexture.width <= 0 || sourceTexture.height <= 0) && !string.IsNullOrWhiteSpace(device_name))
            {
                await WaitForEditorFramesAsync(2);
                deviceView = GetSimulatorDeviceView(simulatorWindow);
                sourceTexture = GetPropertyOrField<Texture>(deviceView, "PreviewTexture");
            }

            if (sourceTexture == null || sourceTexture.width <= 0 || sourceTexture.height <= 0)
            {
                return ToolResultFormatter.Error("SIMULATOR_VIEW_NOT_RENDERED", new
                {
                    hint = open_if_needed
                        ? "The Simulator view is open but has not rendered a preview texture yet. Retry after the next editor frame."
                        : "Open the Simulator view and wait for it to render, or retry with open_if_needed=true."
                });
            }

            Rect? safeArea = null;
            if (safe_area_overlay)
            {
                safeArea = ResolveSimulatorSafeArea(deviceView, sourceTexture.width, sourceTexture.height);
            }

            if (width <= 0 && height <= 0)
            {
                var cap = ResolveDefaultScreenshotSize();
                var longest = Mathf.Max(sourceTexture.width, sourceTexture.height);
                if (cap > 0 && longest > cap)
                {
                    var scale = cap / (float)longest;
                    width = Mathf.Max(64, Mathf.RoundToInt(sourceTexture.width * scale));
                    height = Mathf.Max(64, Mathf.RoundToInt(sourceTexture.height * scale));
                }
            }

            return CaptureTexture(sourceTexture, width, height, safeArea, flipVertically: true,
                saveToFile: save_to_file, outputPath: output_path, defaultBaseName: "simulator-view");
        }

        private static bool TryGetSimulatorWindow(bool openIfNeeded, out object simulatorWindow, out object errorDetail)
        {
            simulatorWindow = null;
            errorDetail = null;

            var simulatorWindowType = ResolveType(
                "UnityEditor.DeviceSimulation.SimulatorWindow",
                "UnityEditor.DeviceSimulatorModule");
            if (simulatorWindowType == null)
            {
                errorDetail = new
                {
                    hint = "Unity Device Simulator is not available in this editor. Use capture_game_view instead."
                };
                return false;
            }

            simulatorWindow = GetExistingSimulatorWindow(simulatorWindowType);
            if (simulatorWindow != null)
                return true;

            if (!openIfNeeded)
            {
                errorDetail = new
                {
                    hint = "No Simulator view is open. Retry with open_if_needed=true or open Window > General > Device Simulator."
                };
                return false;
            }

            try
            {
                var showWindow = simulatorWindowType.GetMethod(
                    "ShowWindow",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                showWindow?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                errorDetail = new
                {
                    message = ex.Message,
                    hint = "Failed to open the Simulator view via Unity's internal SimulatorWindow.ShowWindow()."
                };
                return false;
            }

            simulatorWindow = GetExistingSimulatorWindow(simulatorWindowType)
                              ?? Resources.FindObjectsOfTypeAll(simulatorWindowType).FirstOrDefault();
            if (simulatorWindow != null)
                return true;

            errorDetail = new
            {
                hint = "Unity accepted the request to open the Simulator view, but the window is not available yet. Retry after the next editor frame."
            };
            return false;
        }

        private static object GetExistingSimulatorWindow(Type simulatorWindowType)
        {
            try
            {
                var instancesField = simulatorWindowType.GetField(
                    "s_SimulatorInstances",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (instancesField?.GetValue(null) is System.Collections.IEnumerable instances)
                {
                    object last = null;
                    foreach (var instance in instances)
                    {
                        if (instance != null)
                            last = instance;
                    }
                    if (last != null)
                        return last;
                }
            }
            catch
            {
            }

            return Resources.FindObjectsOfTypeAll(simulatorWindowType).FirstOrDefault();
        }

        private static bool TrySelectSimulatorDevice(object simulatorWindow, string deviceName, out bool deviceChanged, out object errorDetail)
        {
            deviceChanged = false;
            errorDetail = null;
            if (string.IsNullOrWhiteSpace(deviceName))
                return true;

            var main = GetSimulatorMain(simulatorWindow);
            if (main == null)
            {
                errorDetail = new
                {
                    requested = deviceName,
                    hint = "The Simulator window is available, but its internal main controller could not be resolved."
                };
                return false;
            }

            var devices = GetPropertyOrField<Array>(main, "devices")
                          ?? GetPropertyOrField<Array>(main, "m_Devices");
            if (devices == null || devices.Length == 0)
            {
                errorDetail = new
                {
                    requested = deviceName,
                    hint = "No Simulator devices are currently loaded."
                };
                return false;
            }

            if (!TryFindSimulatorDevice(devices, deviceName, out var deviceIndex, out var resolvedDeviceName))
            {
                errorDetail = new
                {
                    requested = deviceName,
                    available = GetSimulatorDeviceNames(devices)
                };
                return false;
            }

            var currentIndex = GetPropertyOrField<int?>(main, "deviceIndex")
                               ?? GetPropertyOrField<int?>(main, "m_DeviceIndex");
            if (currentIndex.HasValue && currentIndex.Value == deviceIndex)
                return true;

            if (!TrySetPropertyOrField(main, "deviceIndex", deviceIndex) &&
                !TrySetPropertyOrField(main, "m_DeviceIndex", deviceIndex))
            {
                errorDetail = new
                {
                    requested = deviceName,
                    matched = resolvedDeviceName,
                    hint = "The Simulator device was found, but deviceIndex could not be set."
                };
                return false;
            }

            TryInvokeInstanceMethod(main, "HandleScreenChange");
            TryInvokeInstanceMethod(main, "InitScreenUI");
            deviceChanged = true;

            if (simulatorWindow is EditorWindow editorWindow)
            {
                editorWindow.Show();
                editorWindow.Focus();
                editorWindow.Repaint();
            }

            EditorApplication.QueuePlayerLoopUpdate();
            return true;
        }

        private static Task WaitForEditorFramesAsync(int frameCount)
        {
            frameCount = Mathf.Max(frameCount, 1);
            var tcs = new TaskCompletionSource<bool>();

            void Tick()
            {
                frameCount--;
                if (frameCount <= 0)
                {
                    EditorApplication.update -= Tick;
                    tcs.TrySetResult(true);
                    return;
                }

                EditorApplication.QueuePlayerLoopUpdate();
            }

            EditorApplication.update += Tick;
            EditorApplication.QueuePlayerLoopUpdate();
            return tcs.Task;
        }

        private static bool TryFindSimulatorDevice(Array devices, string requestedName, out int deviceIndex, out string resolvedDeviceName)
        {
            deviceIndex = -1;
            resolvedDeviceName = null;

            var requestedNormalized = NormalizeDeviceName(requestedName);
            for (var i = 0; i < devices.Length; i++)
            {
                var name = GetSimulatorDeviceName(devices.GetValue(i));
                if (string.Equals(name, requestedName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeDeviceName(name), requestedNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    deviceIndex = i;
                    resolvedDeviceName = name;
                    return true;
                }
            }

            for (var i = 0; i < devices.Length; i++)
            {
                var name = GetSimulatorDeviceName(devices.GetValue(i));
                var normalized = NormalizeDeviceName(name);
                if (!string.IsNullOrEmpty(normalized) &&
                    (normalized.Contains(requestedNormalized) || requestedNormalized.Contains(normalized)))
                {
                    deviceIndex = i;
                    resolvedDeviceName = name;
                    return true;
                }
            }

            return false;
        }

        private static string[] GetSimulatorDeviceNames(Array devices)
        {
            var names = new List<string>();
            for (var i = 0; i < devices.Length; i++)
            {
                var name = GetSimulatorDeviceName(devices.GetValue(i));
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            return names.ToArray();
        }

        private static string GetSimulatorDeviceName(object deviceAsset)
        {
            if (deviceAsset == null)
                return null;

            var deviceInfo = GetPropertyOrField<object>(deviceAsset, "deviceInfo");
            return GetPropertyOrField<string>(deviceAsset, "name")
                   ?? GetPropertyOrField<string>(deviceInfo, "friendlyName")
                   ?? GetPropertyOrField<string>(deviceInfo, "deviceModel")
                   ?? GetPropertyOrField<string>(deviceInfo, "model")
                   ?? GetPropertyOrField<string>(deviceInfo, "name");
        }

        internal static string NormalizeDeviceName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(chars);
        }

        private static object GetSimulatorDeviceView(object simulatorWindow)
        {
            if (simulatorWindow == null)
                return null;

            var deviceView = GetPropertyOrField<object>(simulatorWindow, "DeviceView")
                             ?? GetPropertyOrField<object>(simulatorWindow, "m_DeviceView");
            if (deviceView != null)
                return deviceView;

            var main = GetSimulatorMain(simulatorWindow);
            var userInterfaceController = GetPropertyOrField<object>(main, "userInterface")
                                          ?? GetPropertyOrField<object>(main, "ui")
                                          ?? GetPropertyOrField<object>(main, "m_UserInterfaceController")
                                          ?? GetPropertyOrField<object>(main, "userInterfaceController");
            return GetPropertyOrField<object>(userInterfaceController, "DeviceView")
                   ?? GetPropertyOrField<object>(userInterfaceController, "m_DeviceView");
        }

        private static object GetSimulatorMain(object simulatorWindow)
        {
            return GetPropertyOrField<object>(simulatorWindow, "main")
                   ?? GetPropertyOrField<object>(simulatorWindow, "m_Main");
        }

        private static Rect? ResolveSimulatorSafeArea(object deviceView, int sourceWidth, int sourceHeight)
        {
            var reflectedSafeArea = GetPropertyOrField<Rect?>(deviceView, "SafeArea")
                                    ?? GetPropertyOrField<Rect?>(deviceView, "m_SafeArea");
            if (IsValidSafeArea(reflectedSafeArea, sourceWidth, sourceHeight))
                return reflectedSafeArea;

            var screenSafeArea = Screen.safeArea;
            if (IsValidSafeArea(screenSafeArea, sourceWidth, sourceHeight))
                return screenSafeArea;

            return new Rect(0, 0, sourceWidth, sourceHeight);
        }

        private static bool IsValidSafeArea(Rect? safeArea, int sourceWidth, int sourceHeight)
        {
            return safeArea.HasValue && IsValidSafeArea(safeArea.Value, sourceWidth, sourceHeight);
        }

        private static bool IsValidSafeArea(Rect safeArea, int sourceWidth, int sourceHeight)
        {
            if (safeArea.width <= 0f || safeArea.height <= 0f || sourceWidth <= 0 || sourceHeight <= 0)
                return false;

            var maxWidth = sourceWidth * 1.05f;
            var maxHeight = sourceHeight * 1.05f;
            return safeArea.xMin >= -1f &&
                   safeArea.yMin >= -1f &&
                   safeArea.xMax <= maxWidth &&
                   safeArea.yMax <= maxHeight;
        }

        private static Type ResolveType(string fullName, string assemblyName)
        {
            var type = Type.GetType($"{fullName},{assemblyName}") ?? Type.GetType(fullName);
            if (type != null)
                return type;

            try
            {
                var assembly = Assembly.Load(assemblyName);
                type = assembly?.GetType(fullName);
                if (type != null)
                    return type;
            }
            catch
            {
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static T GetPropertyOrField<T>(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return default(T);

            try
            {
                var type = instance.GetType();
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (property != null && TryCastReflectedValue(property.GetValue(instance, null), out T propertyValue))
                    return propertyValue;

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (field != null && TryCastReflectedValue(field.GetValue(instance), out T fieldValue))
                    return fieldValue;
            }
            catch
            {
            }

            return default(T);
        }

        private static bool TrySetPropertyOrField(object instance, string name, object value)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return false;

            try
            {
                var type = instance.GetType();
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(instance, value, null);
                    return true;
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                if (field != null)
                {
                    field.SetValue(instance, value);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static void TryInvokeInstanceMethod(object instance, string name)
        {
            if (instance == null || string.IsNullOrWhiteSpace(name))
                return;

            try
            {
                var method = instance.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                method?.Invoke(instance, null);
            }
            catch
            {
            }
        }

        private static bool TryCastReflectedValue<T>(object value, out T result)
        {
            if (value is T typed)
            {
                result = typed;
                return true;
            }

            var targetType = typeof(T);
            var nullableType = Nullable.GetUnderlyingType(targetType);
            if (nullableType != null && value != null && nullableType.IsInstanceOfType(value))
            {
                result = (T)value;
                return true;
            }

            result = default(T);
            return false;
        }
    }
}
