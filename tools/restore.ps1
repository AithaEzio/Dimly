# Safety net: puts every DDC/CI display back to a known brightness (100% by default).
#
# The other test scripts force-kill Dimly, and a force-killed process cannot run its own
# restore, so a crashed or interrupted test run can leave a monitor dim. Run this to undo it.

param([int]$Level = 100)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class RestoreAll {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PM {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    public delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT clip, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint c, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    public static List<string> Apply(int level, bool write) {
        List<string> report = new List<string>();
        List<IntPtr> screens = new List<IntPtr>();
        MonitorEnumProc collect = delegate(IntPtr h, IntPtr hdc, ref RECT clip, IntPtr data) {
            screens.Add(h);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);

        int index = 0;
        foreach (IntPtr screen in screens) {
            index++;
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(screen, ref count) || count == 0) {
                report.Add("Display " + index + ": no physical monitor");
                continue;
            }
            PM[] monitors = new PM[count];
            if (!GetPhysicalMonitorsFromHMONITOR(screen, count, monitors)) {
                report.Add("Display " + index + ": handles unavailable");
                continue;
            }
            foreach (PM monitor in monitors) {
                uint lo = 0, cur = 0, hi = 0;
                if (GetMonitorBrightness(monitor.handle, ref lo, ref cur, ref hi) && hi > lo) {
                    int before = (int)Math.Round((cur - (double)lo) * 100.0 / (hi - lo));
                    if (write && before != level) {
                        SetMonitorBrightness(monitor.handle, (uint)(lo + Math.Round((hi - lo) * level / 100.0)));
                        System.Threading.Thread.Sleep(250);
                        GetMonitorBrightness(monitor.handle, ref lo, ref cur, ref hi);
                        int after = (int)Math.Round((cur - (double)lo) * 100.0 / (hi - lo));
                        report.Add("Display " + index + ": " + before + "% -> " + after + "%");
                    } else {
                        report.Add("Display " + index + ": " + before + "%");
                    }
                } else {
                    report.Add("Display " + index + ": no DDC/CI (an overlay display, nothing to restore)");
                }
                DestroyPhysicalMonitor(monitor.handle);
            }
        }
        return report;
    }
}
'@

Write-Host "Setting every DDC/CI display to $Level%..."
[RestoreAll]::Apply($Level, $true) | ForEach-Object { "  $_" }
