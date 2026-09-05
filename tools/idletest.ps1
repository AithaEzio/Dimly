# End-to-end check of the away behaviour: Dimly should dim on its own after the idle delay
# and come back the instant real input arrives. Also verifies settings load and save.
#
# Note: this script must not touch the mouse or keyboard until it deliberately does, because
# that is exactly what Dimly is watching.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Idle {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr p, POINT pt, uint f);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint cb, tick; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);

    /// Milliseconds since the last real input - the very thing this test needs to be climbing.
    public static long IdleMs() {
        LASTINPUT info = new LASTINPUT(); info.cb = 8;
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.tick);
    }

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint cur, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    static IntPtr monitor = IntPtr.Zero;

    public static bool Open() {
        POINT origin; origin.X = 0; origin.Y = 0;
        uint count = 0;
        IntPtr hMonitor = MonitorFromPoint(origin, 1);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0) return false;
        PHYSICAL_MONITOR[] monitors = new PHYSICAL_MONITOR[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return false;
        monitor = monitors[0].handle;
        return true;
    }

    public static int Read() {
        uint lo = 0, cur = 0, hi = 0;
        if (!GetMonitorBrightness(monitor, ref lo, ref cur, ref hi) || hi <= lo) return -1;
        return (int)Math.Round((cur - (double)lo) * 100.0 / (hi - lo));
    }

    public static void Write(int percent) {
        uint lo = 0, cur = 0, hi = 0;
        if (!GetMonitorBrightness(monitor, ref lo, ref cur, ref hi) || hi <= lo) return;
        SetMonitorBrightness(monitor, (uint)(lo + Math.Round((hi - lo) * percent / 100.0)));
    }

    public static void Close() { if (monitor != IntPtr.Zero) DestroyPhysicalMonitor(monitor); }

    /// A zero-distance move: real input as far as Windows is concerned, but the pointer stays put.
    public static void Nudge() { mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); }

    public static void ClickAt(IntPtr root, int sx, int sy) {
        IntPtr current = root;
        for (int d = 0; d < 8; d++) {
            POINT p; p.X = sx; p.Y = sy; ScreenToClient(current, ref p);
            IntPtr child = ChildWindowFromPointEx(current, p, 0x0001 | 0x0004);
            if (child == IntPtr.Zero || child == current) break;
            current = child;
        }
        POINT q; q.X = sx; q.Y = sy; ScreenToClient(current, ref q);
        IntPtr lParam = (IntPtr)((q.Y << 16) | (q.X & 0xFFFF));
        PostMessage(current, 0x0201, (IntPtr)1, lParam);
        System.Threading.Thread.Sleep(60);
        PostMessage(current, 0x0202, IntPtr.Zero, lParam);
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Dimly.exe'
$settings = Join-Path $env:APPDATA 'Dimly\settings.ini'

. (Join-Path $PSScriptRoot 'common.ps1')
Protect-DimlySettings
Wake-Screen

if (-not [Idle]::Open()) { throw 'No DDC/CI monitor to measure.' }
$baseline = [Idle]::Read()
Write-Host "Baseline brightness: $baseline%"

# Pre-seed settings so the app has to read them: 5 second delay, 40% away level.
New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=40'
    'IdleSeconds=5'
    'Fade=0'
    'DimOnLock=1'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=0'   # this test is about idle time; sound must not enter into it
    'StartHidden=0'
    'Theme=midnight'
    'DisabledDisplays='
)

$results = @{}

try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    $app = Start-Process $exe -PassThru

    $handle = [IntPtr]::Zero
    $rect = New-Object Idle+RECT
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $app.Refresh()
        $handle = $app.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            [Idle]::GetWindowRect($handle, [ref]$rect) | Out-Null
            if (($rect.R - $rect.L) -gt 600) { break }
        }
    }
    if (($rect.R - $rect.L) -lt 600) { throw 'Dimly window never appeared.' }
    [Idle]::SetWindowPos($handle, [IntPtr]::new(-1), 40, 40, 0, 0, 0x0001 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 500
    [Idle]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $scale = ($rect.B - $rect.T) / 840.0

    # 1. Settings were loaded: click the "10s" preset and check it lands on disk.
    $x = $rect.L + [int]((210 + 28 + 20 + 3 + 99 + 49) * $scale)
    $y = $rect.T + [int]((54 + 200 + 98 + 17) * $scale)
    [Idle]::ClickAt($handle, $x, $y)
    Start-Sleep -Milliseconds 800
    $saved = (Get-Content $settings) -match '^IdleSeconds='
    $results['save'] = ($saved -join '') -eq 'IdleSeconds=10'
    Write-Host "Saved setting after clicking 10s: $saved"

    # Put it back to 5s so the wait below is short.
    $x5 = $rect.L + [int]((210 + 28 + 20 + 3 + 49) * $scale)
    [Idle]::ClickAt($handle, $x5, $y)
    Start-Sleep -Milliseconds 800
    Write-Host ("Delay now: " + (((Get-Content $settings) -match '^IdleSeconds=') -join ''))

    # 2. Now go quiet. No input of any kind until the nudge below.
    Write-Host 'Idling (no input) - expecting an automatic dim...'
    $samples = @()
    $peakIdle = 0
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 700
        $samples += [Idle]::Read()
        $now = [Idle]::IdleMs()
        if ($now -gt $peakIdle) { $peakIdle = $now }
    }
    Write-Host ("Readings while idle: " + ($samples -join ', ') + "   (peak idle ${peakIdle}ms)")
    $low = ($samples | Measure-Object -Minimum).Minimum

    # Only a real idle stretch can prove anything here. Machines with something injecting input
    # never go idle at all, and calling that a pass would be a lie.
    if ($peakIdle -lt 8000) {
        $results['autoDim'] = 'SKIP'
        Write-Host 'The machine never idled past 8s, so the automatic dim could not be tested.'
    }
    else { $results['autoDim'] = ($low -le 45) }

    # 3. Come back.
    Write-Host 'Sending real input...'
    [Idle]::Nudge()
    Start-Sleep -Milliseconds 2500
    $back = [Idle]::Read()
    Write-Host "After input: $back%"
    $results['restore'] = ($back -ge ($baseline - 3))

    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
    $results['afterExit'] = ([Idle]::Read() -ge ($baseline - 3))

    Write-Host ''
    $failures = 0
    foreach ($k in 'save', 'autoDim', 'restore', 'afterExit') {
        # -is [string] rather than -eq 'SKIP': with a boolean on the left, PowerShell casts the
        # right-hand side to boolean, and any non-empty string is $true - so every passing check
        # matched 'SKIP' and this could never once print PASS.
        $mark = if ($results[$k] -is [string]) { 'SKIP (machine never idle)' }
                elseif ($results[$k]) { 'PASS' } else { $failures++; 'FAIL' }
        Write-Host ("  {0,-10} {1}" -f $k, $mark)
    }
    Write-Host ''
    if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
    else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    if ([Idle]::Read() -lt ($baseline - 3)) {
        Write-Host "Restoring brightness to $baseline% ..." -ForegroundColor Yellow
        [Idle]::Write($baseline)
    }
    [Idle]::Close()
    Restore-DimlySettings
}
