# Checks the fix for a screen switched off by Windows on its display timeout, then switched
# back on - which is not a sleep, and which Dimly is told about only by a power notification.
#
# The failure it guards against: the monitor handle Dimly holds survives the power-off in name
# only. It takes writes, ignores them, and echoes them back, so the restore made when the user
# touches the mouse is confirmed and let go of while the panel is still coming up dim - and the
# screen stays dim until the monitor's own buttons are used.
#
# The screen goes dark briefly. Moving the mouse during that is harmless; it just ends the
# power-off sooner, and the off-to-on edge being tested still happens.
#
# Moves real screen brightness and puts it back.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Screen {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint cb, tick; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PM {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr w, int m, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint c, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    public static long IdleMs() {
        LASTINPUT info = new LASTINPUT(); info.cb = 8;
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.tick);
    }

    public static void Wiggle() {
        mouse_event(0x0001, 4, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0001, unchecked((uint)-4), 0, 0, IntPtr.Zero);
    }

    /// The same request Windows makes when its display timeout expires.
    public static void TurnOff() {
        SendMessage((IntPtr)0xFFFF, 0x0112, (IntPtr)0xF170, (IntPtr)2);
    }

    static IntPtr Primary() {
        POINT o; o.X = 0; o.Y = 0;
        uint count = 0;
        IntPtr monitor = MonitorFromPoint(o, 1);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, ref count) || count == 0) return IntPtr.Zero;
        PM[] monitors = new PM[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return IntPtr.Zero;
        for (int i = 1; i < monitors.Length; i++) DestroyPhysicalMonitor(monitors[i].handle);
        return monitors[0].handle;
    }

    /// Every reading takes a fresh handle, so this harness can never be fooled the way a
    /// handle held across the power-off can.
    public static int Read() {
        for (int attempt = 0; attempt < 3; attempt++) {
            if (attempt > 0) System.Threading.Thread.Sleep(60);
            IntPtr h = Primary();
            if (h == IntPtr.Zero) continue;
            uint lo = 0, cur = 0, hi = 0;
            bool ok = GetMonitorBrightness(h, ref lo, ref cur, ref hi);
            DestroyPhysicalMonitor(h);
            if (ok && hi > lo) return (int)Math.Round((cur - (double)lo) * 100.0 / (hi - lo));
        }
        return -1;
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
$restoreLevel = 80

$baseline = [Screen]::Read()
if ($baseline -lt 0) { throw 'The monitor never answered a brightness query.' }
Write-Host "Baseline brightness: $baseline%"

$failures = 0
function Check([string]$what, [bool]$ok) {
    Write-Host ("  {0}  {1}" -f $(if ($ok) { 'pass' } else { 'FAIL' }), $what)
    if (-not $ok) { $script:failures++ }
}

function WaitForLevel([int]$target, [int]$seconds) {
    for ($i = 0; $i -lt $seconds; $i++) {
        Start-Sleep -Seconds 1
        $now = [Screen]::Read()
        if ($now -ge 0 -and [Math]::Abs($now - $target) -le 5) { return $now }
    }
    return [Screen]::Read()
}

New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=30'
    'IdleSeconds=5'
    'Fade=1'
    'FadeMillis=500'
    'DimOnLock=0'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=0'
    'IgnoreNoisyDevices=1'
    "RestoreFallback=$restoreLevel"
    'TrayHintShown=1'
    'StartHidden=1'
    'Theme=midnight'
    'DisabledDisplays='
    'DisplayFallbacks='
    'ManualRestoreDisplays='
)

try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    [Screen]::Wiggle()
    Start-Process $exe | Out-Null
    Start-Sleep -Seconds 3

    Write-Host 'Waiting for it to dim...'
    $dimmed = WaitForLevel 30 22
    $idle = [Screen]::IdleMs()
    if ([Math]::Abs($dimmed - 30) -gt 5) {
        if ($idle -lt 20000) {
            Write-Host "SKIPPED - it never dimmed (somebody used the machine: idle ${idle}ms)" -ForegroundColor Yellow
            return
        }
        throw "It never dimmed - the screen is at $dimmed%."
    }
    Check 'it dimmed' $true

    [Screen]::Wiggle()
    $back = WaitForLevel $restoreLevel 12
    Check 'it restored when the user came back' ([Math]::Abs($back - $restoreLevel) -le 5)

    # The restore is watched over for ten seconds. Wait that out, so what follows tests the
    # display coming back on and nothing else.
    Write-Host 'Letting the restore watch expire...'
    Start-Sleep -Seconds 13

    # The monitor comes up at the dimmed level, exactly as one does after a long power-off.
    [Screen]::Write(30)
    Start-Sleep -Seconds 4
    $stuck = [Screen]::Read()
    Write-Host "  with nothing to tell it, the screen sits at $stuck%"
    Check 'nothing else notices a screen that came back dim' ([Math]::Abs($stuck - 30) -le 5)

    Write-Host 'Switching the screen off and on...'
    [Screen]::TurnOff()
    Start-Sleep -Seconds 4
    [Screen]::Wiggle()

    $fixed = WaitForLevel $restoreLevel 20
    Write-Host "  after the screen came back on: $fixed%"
    Check 'the display coming back on puts the brightness right' ([Math]::Abs($fixed - $restoreLevel) -le 5)

    Write-Host ''
    if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
    else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if ([Screen]::Read() -ne $baseline) {
        Write-Host "Putting brightness back to $baseline% ..." -ForegroundColor Yellow
        [Screen]::Write($baseline)
    }
    if (Test-Path $settings) { Remove-Item $settings }
}
