using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace AladdinRug
{
    /// <summary>Win32 bits needed to keep a window on the desktop layer: above the icons, below every app.</summary>
    internal static class Native
    {
        public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TRANSPARENT = 0x20L, WS_EX_TOOLWINDOW = 0x80L, WS_EX_APPWINDOW = 0x40000L, WS_EX_NOACTIVATE = 0x08000000L;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public const uint GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
        public const int SW_HIDE = 0;
        public const uint MONITOR_DEFAULTTOPRIMARY = 1;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        public static string ClassOf(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static long GetExStyle(IntPtr hwnd) => GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & 0xFFFFFFFFL;
        public static void SetExStyle(IntPtr hwnd, long style) => SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)style);

        /// <summary>
        /// Put <paramref name="hwnd"/> directly above the desktop window in the stacking order, so it covers
        /// the icons and wallpaper but every app window stays on top of it.
        /// </summary>
        public static void SitOnDesktop(IntPtr hwnd)
        {
            IntPtr desktop = GetShellWindow();
            if (desktop == IntPtr.Zero) return;

            IntPtr above = GetWindow(desktop, GW_HWNDPREV);   // whatever is stacked directly over the desktop
            if (above == hwnd) return;                        // already exactly where it belongs
            SetWindowPos(hwnd, above == IntPtr.Zero ? HWND_TOP : above, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>Is the desktop window somewhere below <paramref name="hwnd"/>? False means the rug has sunk behind it.</summary>
        public static bool IsAboveDesktop(IntPtr hwnd)
        {
            IntPtr desktop = GetShellWindow();
            if (desktop == IntPtr.Zero) return true;
            for (IntPtr w = GetWindow(hwnd, GW_HWNDNEXT); w != IntPtr.Zero; w = GetWindow(w, GW_HWNDNEXT))
                if (w == desktop) return true;
            return false;
        }

        public static double DpiScaleOf(IntPtr monitor)
        {
            try
            {
                if (GetDpiForMonitor(monitor, 0, out uint dx, out _) == 0 && dx > 0) return dx / 96.0;
            }
            catch (DllNotFoundException) { }
            return 1.0;
        }
    }

    /// <summary>A monitor's usable area (everything above the taskbar), in physical pixels.</summary>
    internal struct DeskGeometry : IEquatable<DeskGeometry>
    {
        public int X, Y, W, H;
        public double Scale;

        public static DeskGeometry ForMonitor(IntPtr mon)
        {
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
            Native.GetMonitorInfo(mon, ref mi);
            return new DeskGeometry
            {
                X = mi.rcWork.Left,
                Y = mi.rcWork.Top,
                W = mi.rcWork.Right - mi.rcWork.Left,
                H = mi.rcWork.Bottom - mi.rcWork.Top,
                Scale = Native.DpiScaleOf(mon),
            };
        }

        public static DeskGeometry Primary() =>
            ForMonitor(Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY));

        public static List<DeskGeometry> All()
        {
            var list = new List<DeskGeometry>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref Native.RECT r, IntPtr data) =>
            {
                list.Add(ForMonitor(m));
                return true;
            }, IntPtr.Zero);
            if (list.Count == 0) list.Add(Primary());
            return list;
        }

        public bool Equals(DeskGeometry o) => X == o.X && Y == o.Y && W == o.W && H == o.H && Scale == o.Scale;
        public override bool Equals(object obj) => obj is DeskGeometry g && Equals(g);
        public override int GetHashCode() => HashCode.Combine(X, Y, W, H, Scale);
    }
}
