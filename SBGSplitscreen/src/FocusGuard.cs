using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SBGSplitscreen
{
    /// <summary>
    /// Logs every time this window takes the foreground, and with --sbg-no-focus hands the
    /// foreground straight back to the last non-game window. That is for unattended test runs,
    /// where nobody is playing and the game must not take the keyboard from whoever is working.
    /// </summary>
    internal static class FocusGuard
    {
        private static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;
        private static readonly string OwnName = Process.GetCurrentProcess().ProcessName;

        private static IntPtr lastSeen;
        private static bool lastSeenIsOwn;
        private static IntPtr lastOther;
        private static string lastOtherName;

        public static void Update()
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
            {
                return;
            }
            if (fg != lastSeen)
            {
                lastSeen = fg;
                GetWindowThreadProcessId(fg, out uint pid);
                lastSeenIsOwn = pid == OwnPid;
                if (lastSeenIsOwn)
                {
                    Plugin.Log.LogInfo($"Window took the foreground (previously: {lastOtherName ?? "unknown"}).");
                }
                else
                {
                    string name = ProcessName(pid);
                    // Never bounce focus between the two game copies.
                    if (name != OwnName)
                    {
                        lastOther = fg;
                        lastOtherName = name;
                    }
                }
            }

            if (lastSeenIsOwn && Settings.NoFocus && lastOther != IntPtr.Zero && IsWindow(lastOther) && SetForegroundWindow(lastOther))
            {
                lastSeen = lastOther;
                lastSeenIsOwn = false;
                Plugin.Log.LogInfo($"Handed the foreground back to {lastOtherName}.");
            }
        }

        private static string ProcessName(uint pid)
        {
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    return p.ProcessName;
                }
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    }
}
