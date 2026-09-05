# Checks that Dimly still dims on a machine whose system idle clock never advances.
#
# A game controller with drifting analogue sticks reports HID state continuously, and Windows
# treats every report as input: GetLastInputInfo is pinned at zero, so the screen saver, the
# display timeout and Dimly's countdown all stop working. "Ignore devices that keep the PC
# awake" makes Dimly count real keyboard, mouse and gamepad use instead.
#
# This test is only meaningful on a machine showing that fault, and says so if it is not.
# Moves real screen brightness and puts it back.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Devices {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint cb, tick; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PM {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint c, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    public static long SystemIdleMs() {
        LASTINPUT info = new LASTINPUT(); info.cb = 8;
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.tick);
    }

    /// Resets the *system* idle clock without moving the pointer.
    public static void Nudge() { mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); }

    /// Genuine movement: a pixel out and back. A zero-distance move is exactly what the
    /// watcher discards as sensor jitter, so proving restoration needs the real thing.
    public static void RealMove() {
        mouse_event(0x0001, 1, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0001, unchecked((uint)-1), 0, 0, IntPtr.Zero);
    }

    static IntPtr Primary() {
        POINT origin; origin.X = 0; origin.Y = 0;
        uint count = 0;
        IntPtr monitor = MonitorFromPoint(origin, 1);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, ref count) || count == 0) return IntPtr.Zero;
        PM[] monitors = new PM[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return IntPtr.Zero;
        return monitors[0].handle;
    }

    public static int Read() {
        IntPtr h = Primary();
        if (h == IntPtr.Zero) return -1;
        uint lo = 0, cur = 0, hi = 0;
        bool ok = GetMonitorBrightness(h, ref lo, ref cur, ref hi);
        DestroyPhysicalMonitor(h);
        if (!ok || hi <= lo) return -1;
        return (int)Math.Round((cur - (double)lo) * 100.0 / (hi - lo));
    }

    public static void Write(int percent) {
        IntPtr h = Primary();
        if (h == IntPtr.Zero) return;
        uint lo = 0, cur = 0, hi = 0;
        if (GetMonitorBrightness(h, ref lo, ref cur, ref hi) && hi > lo)
            SetMonitorBrightness(h, (uint)(lo + Math.Round((hi - lo) * percent * 0.01)));
        DestroyPhysicalMonitor(h);
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Dimly.exe'
$settings = Join-Path $env:APPDATA 'Dimly\settings.ini'

. (Join-Path $PSScriptRoot 'common.ps1')
Protect-DimlySettings
Wake-Screen

$baseline = -1
for ($try = 0; $try -lt 8 -and $baseline -lt 0; $try++) {
    $baseline = [Devices]::Read()
    if ($baseline -lt 0) { Start-Sleep -Milliseconds 400 }
}
if ($baseline -lt 0) { throw 'The monitor never answered a brightness query.' }
Write-Host "Baseline brightness: $baseline%"

# Is this machine actually showing the fault? Watch the system clock while touching nothing.
$peak = 0
for ($i = 0; $i -lt 12; $i++) {
    Start-Sleep -Milliseconds 500
    $now = [Devices]::SystemIdleMs()
    if ($now -gt $peak) { $peak = $now }
}
Write-Host "Windows' idle clock reached ${peak}ms while nobody touched anything."

$clockStuck = $peak -le 4000
if ($clockStuck) {
    Write-Host 'The system clock is stuck: exactly the case this setting exists for.'
} else {
    Write-Host 'The system clock is healthy today, so the rescue itself cannot be exercised.'
    Write-Host 'The rest still checks that the watcher works: with the setting on, restoring'
    Write-Host 'depends entirely on Raw Input actually delivering the mouse movement.'
}

New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=40'
    'IdleSeconds=15'
    'Fade=0'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=0'
    'IgnoreNoisyDevices=1'
    'StartHidden=1'
    'Theme=midnight'
    'DisabledDisplays='
)

$failures = 0
function Check([string]$what, [bool]$ok) {
    Write-Host ("  {0}  {1}" -f $(if ($ok) { 'pass' } else { 'FAIL' }), $what)
    if (-not $ok) { $script:failures++ }
}

try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    [Devices]::Nudge()
    Start-Process $exe -ArgumentList '--tray' | Out-Null

    Write-Host 'Waiting 30s without touching anything (15s delay, away level 40%)...'
    $readings = @()
    for ($i = 0; $i -lt 30; $i++) { Start-Sleep -Seconds 1; $readings += [Devices]::Read() }
    Write-Host ("  brightness: " + ($readings -join ', '))

    $seen = @($readings | Where-Object { $_ -ge 0 })
    $label = if ($clockStuck) { 'dims even though Windows reports no idle time' }
             else { 'dims with the setting on' }
    Check $label (($seen | Measure-Object -Minimum).Minimum -le 45)

    # With the setting on, the only clock that can bring it back is the watcher's own, so a
    # restore here is proof that Raw Input is delivering real mouse movement.
    [Devices]::RealMove()
    Start-Sleep -Seconds 3
    Check 'Raw Input sees real movement and restores' ([Devices]::Read() -ge ($baseline - 3))

    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 700
    Check 'still restored after exit' ([Devices]::Read() -ge ($baseline - 3))

    Write-Host ''
    if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
    else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    if ([Devices]::Read() -lt ($baseline - 3)) {
        Write-Host "Putting brightness back to $baseline% ..." -ForegroundColor Yellow
        [Devices]::Write($baseline)
    }
    Restore-DimlySettings
}
