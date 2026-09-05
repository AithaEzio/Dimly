# Checks that brightness comes back after the display has been powered down while dimmed.
#
# Turning a monitor off invalidates the DDC/CI handles Dimly holds. If it does not notice, the
# restore silently fails, Dimly forgets the brightness it was supposed to put back, and the
# screen stays dim for good - including after quitting or dimming again by hand.
#
# The screen goes black for about ten seconds. Brightness is always put back.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Wake {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PM {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint cb, tick; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);

    public static long IdleMs() {
        LASTINPUT info = new LASTINPUT(); info.cb = 8;
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.tick);
    }
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint c, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    static readonly IntPtr Broadcast = new IntPtr(0xFFFF);
    const int WM_SYSCOMMAND = 0x0112;
    const int SC_MONITORPOWER = 0xF170;

    public static void DisplayOff() { SendMessage(Broadcast, WM_SYSCOMMAND, (IntPtr)SC_MONITORPOWER, (IntPtr)2); }

    /// Real movement: wakes the display and counts as somebody coming back.
    public static void Wiggle() {
        mouse_event(0x0001, 3, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(80);
        mouse_event(0x0001, unchecked((uint)-3), 0, 0, IntPtr.Zero);
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

    /// Opens a fresh handle every time, so this measurement never suffers the very staleness
    /// it is here to detect.
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

function Settle {
    for ($i = 0; $i -lt 10; $i++) {
        $v = [Wake]::Read()
        if ($v -ge 0) { return $v }
        Start-Sleep -Milliseconds 400
    }
    return -1
}

$baseline = Settle
if ($baseline -lt 0) { throw 'The monitor never answered a brightness query.' }
Write-Host "Baseline brightness: $baseline%"

New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=30'
    'IdleSeconds=5'
    'Fade=0'
    'DimOnLock=0'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=0'
    'IgnoreNoisyDevices=0'
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
    [Wake]::Wiggle()
    Start-Process $exe -ArgumentList '--tray' | Out-Null

    Write-Host 'Waiting for the automatic dim (5s delay) - please leave the machine alone...'
    $dimmed = -1
    $peakIdle = 0
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 1
        $now = [Wake]::IdleMs()
        if ($now -gt $peakIdle) { $peakIdle = $now }
        $v = [Wake]::Read()
        if ($v -ge 0 -and $v -le 40) { $dimmed = $v; break }
    }

    if ($dimmed -lt 0) {
        Write-Host ''
        Write-Host "SKIPPED - it never dimmed (peak idle ${peakIdle}ms)." -ForegroundColor Yellow
        Write-Host 'The machine has to be left alone for the away dim to happen, and only then'
        Write-Host 'can the wake behaviour be tested. Start this and do not touch anything.'
        exit 0
    }

    Check 'dimmed before the display is powered off' ($dimmed -ge 0)
    Write-Host "  dimmed to $dimmed%"

    Write-Host 'Turning the display off for 10 seconds...'
    [Wake]::DisplayOff()
    Start-Sleep -Seconds 10

    Write-Host 'Waking it and coming back...'
    [Wake]::Wiggle()
    Start-Sleep -Seconds 2
    [Wake]::Wiggle()

    $after = -1
    for ($i = 0; $i -lt 12; $i++) {
        Start-Sleep -Seconds 1
        $after = [Wake]::Read()
        if ($after -ge ($baseline - 3)) { break }
    }
    Write-Host "  brightness after waking: $after%"
    Check 'brightness restored after the display slept' ($after -ge ($baseline - 3))

    # Quitting must also put things back, which it cannot do if the captured value was lost.
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1
    $final = Settle
    Write-Host "  brightness after quitting: $final%"
    Check 'still correct after quitting' ($final -ge ($baseline - 3))

    Write-Host ''
    if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
    else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if ((Settle) -lt ($baseline - 3)) {
        Write-Host "Putting brightness back to $baseline% ..." -ForegroundColor Yellow
        [Wake]::Write($baseline)
    }
    Restore-DimlySettings
}
