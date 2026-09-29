// Copyright (C) KitWright. Licensed under MIT.

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace KitWright.Editor.MCP.Server
{
    /// <summary>
    /// Keeps Windows from running the editor under EcoQoS while it serves requests. Windows 11
    /// throttles a process whose window is not in front: its threads move to the efficiency cores at
    /// a lower clock. That is exactly when the server works, with the user in their agent's window;
    /// a Sprite Finder scan measured 7.0-8.6 s throttled and 2.1-2.6 s opted out. The opt-out lives
    /// as long as the process, so an editor that quits hands the decision back to Windows.
    /// </summary>
    internal static class PowerThrottling
    {
        private const int ProcessPowerThrottling = 4;
        private const uint ExecutionSpeed = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct State
        {
            public uint Version;
            public uint ControlMask;
            public uint StateMask;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref State info, int size);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref State info, int size);

        // A Windows older than 10 1709 refuses the class; it has no throttling to opt out of.
        internal static void OptOut()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                return;

            var state = new State { Version = 1, ControlMask = ExecutionSpeed, StateMask = 0 };
            SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<State>());
        }

        internal static bool IsOptedOut()
        {
            var state = new State { Version = 1 };
            return GetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, Marshal.SizeOf<State>())
                   && (state.ControlMask & ExecutionSpeed) != 0 && (state.StateMask & ExecutionSpeed) == 0;
        }
    }
}
