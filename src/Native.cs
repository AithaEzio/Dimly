using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Dimly
{
    /// <summary>The Win32 surface Dimly depends on. Stateless by design.</summary>
    internal static class Native
    {
        // ---------------------------------------------------------------- idle

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        private static readonly uint LastInputInfoSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));

        /// <summary>Milliseconds since the last keyboard or mouse input in this session.</summary>
        public static int IdleMilliseconds()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = LastInputInfoSize;
            if (!GetLastInputInfo(ref info)) return 0;

            // Unsigned subtraction stays correct across the 49-day tick count wrap.
            uint elapsed = unchecked((uint)Environment.TickCount - info.dwTime);
            return elapsed > int.MaxValue ? int.MaxValue : (int)elapsed;
        }

        // ------------------------------------------------------------- monitors

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;

            public Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;

            public static MONITORINFOEX Create()
            {
                MONITORINFOEX mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                mi.szDevice = string.Empty;
                return mi;
            }

            public bool IsPrimary { get { return (dwFlags & 1) != 0; } }
        }

        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT clip, IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayDevices(string device, uint index, ref DISPLAY_DEVICE info, uint flags);

        private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;

        /// <summary>
        /// Reads the monitor attached to an adapter, returning its Plug-and-Play identity and
        /// the model string Windows reports for it.
        /// </summary>
        /// <param name="adapter">An adapter device name such as <c>\\.\DISPLAY1</c>.</param>
        /// <param name="pnpKey">
        /// Normalised as <c>HARDWAREID\INSTANCE</c> (for example <c>LGD05C0\4&amp;1a2b&amp;0&amp;UID8388688</c>),
        /// which is the form WMI's InstanceName also reduces to. Null when unavailable.
        /// </param>
        public static void DescribeMonitor(string adapter, out string pnpKey, out string model)
        {
            pnpKey = null;
            model = null;

            DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
            dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
            if (!EnumDisplayDevices(adapter, 0, ref dd, EDD_GET_DEVICE_INTERFACE_NAME)) return;

            model = dd.DeviceString;

            // DeviceID looks like \\?\DISPLAY#LGD05C0#4&1a2b&0&UID8388688#{guid}
            string id = dd.DeviceID;
            if (string.IsNullOrEmpty(id)) return;
            string[] parts = id.Split('#');
            if (parts.Length < 3) return;
            pnpKey = (parts[1] + "\\" + parts[2]).ToUpperInvariant();
        }

        // ------------------------------------------------------------- DDC/CI

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyPhysicalMonitor(IntPtr handle);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorBrightness(IntPtr handle, ref uint minimum, ref uint current, ref uint maximum);

        [DllImport("dxva2.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetMonitorBrightness(IntPtr handle, uint value);

        // ------------------------------------------------------- foreground app

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int capacity);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        /// <summary>
        /// True when the focused window covers its whole monitor - a video, slideshow or game
        /// that the user is watching rather than ignoring.
        /// </summary>
        public static bool IsFullscreenAppActive()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;

            // The desktop and the shell are always "fullscreen"; they mean nobody is watching.
            StringBuilder className = new StringBuilder(64);
            GetClassName(window, className, className.Capacity);
            string name = className.ToString();
            if (name == "Progman" || name == "WorkerW" || name == "Shell_TrayWnd") return false;

            RECT window_rect;
            if (!GetWindowRect(window, out window_rect)) return false;

            MONITORINFOEX info = MONITORINFOEX.Create();
            if (!GetMonitorInfo(MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST), ref info)) return false;

            RECT screen = info.rcMonitor;
            return window_rect.Left <= screen.Left && window_rect.Top <= screen.Top
                && window_rect.Right >= screen.Right && window_rect.Bottom >= screen.Bottom;
        }

        // ------------------------------------------------------------- windows

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
        public static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);

        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;

        public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOACTIVATE = 0x0010;

        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int CS_DROPSHADOW = 0x00020000;

        [DllImport("psapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EmptyWorkingSet(IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        /// <summary>
        /// Hands the working set back to Windows. Dimly spends nearly all of its life asleep
        /// between one-second ticks, and the startup and window-drawing pages it touched on the
        /// way in are not needed again until the user opens the window.
        /// </summary>
        public static void TrimMemory()
        {
            try { EmptyWorkingSet(GetCurrentProcess()); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }

        /// <summary>Asks DWM for Windows 11 rounded corners. Silently ignored on Windows 10.</summary>
        public static void RoundCorners(IntPtr hWnd)
        {
            const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
            const int DWMWCP_ROUND = 2;
            int preference = DWMWCP_ROUND;
            try { DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int)); }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }
        }
    }
}
