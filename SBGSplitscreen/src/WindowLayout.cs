using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace SBGSplitscreen
{
    /// <summary>
    /// Test-only (--sbg-window x,y,w,h): keeps the game in a borderless window at a fixed spot,
    /// so unattended runs stay on a spare monitor instead of going fullscreen on the main one.
    /// The game re-applies its saved resolution on start and on settings changes, hence the re-check.
    /// </summary>
    internal static class WindowLayout
    {
        private static IntPtr hwnd;
        private static float nextCheck;
        private static int framesUntilPlace = -1;

        public static void Update()
        {
            if (!(Settings.TestWindow is RectInt target))
            {
                return;
            }
            if (framesUntilPlace > 0)
            {
                framesUntilPlace--;
                return;
            }
            if (framesUntilPlace == 0)
            {
                framesUntilPlace = -1;
                Place(target);
                return;
            }
            if (Time.unscaledTime < nextCheck)
            {
                return;
            }
            nextCheck = Time.unscaledTime + 1f;
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            {
                hwnd = FindOwnWindow();
                if (hwnd == IntPtr.Zero)
                {
                    return;
                }
            }
            GetWindowRect(hwnd, out RECT r);
            bool inPlace = Screen.fullScreenMode == FullScreenMode.Windowed
                && r.Left == target.x && r.Top == target.y && r.Right - r.Left == target.width && r.Bottom - r.Top == target.height;
            if (inPlace)
            {
                return;
            }
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != target.width || Screen.height != target.height)
            {
                Screen.SetResolution(target.width, target.height, FullScreenMode.Windowed);
                framesUntilPlace = 2;
            }
            else
            {
                Place(target);
            }
        }

        private static void Place(RectInt target)
        {
            long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            long newStyle = (style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX)) | WS_POPUP;
            if (newStyle != style)
            {
                SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(newStyle));
            }
            SetWindowPos(hwnd, IntPtr.Zero, target.x, target.y, target.width, target.height,
                SWP_NOZORDER | SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        private static IntPtr FindOwnWindow()
        {
            uint pid = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            var className = new StringBuilder(64);
            EnumWindowsProc proc = (h, data) =>
            {
                GetWindowThreadProcessId(h, out uint windowPid);
                if (windowPid != pid || !IsWindowVisible(h))
                {
                    return true;
                }
                className.Length = 0;
                GetClassName(h, className, className.Capacity);
                if (className.ToString() == "UnityWndClass")
                {
                    found = h;
                    return false;
                }
                return true;
            };
            EnumWindows(proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            return found;
        }

        private const int GWL_STYLE = -16;
        private const long WS_POPUP = 0x80000000L;
        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_SYSMENU = 0x00080000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_NOOWNERZORDER = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    }
}
