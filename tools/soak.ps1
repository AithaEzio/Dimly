# Runs Dimly through many dim-and-restore cycles and watches what it holds on to.
#
# An app that paints itself creates fonts, pens and brushes on every repaint, and one that is
# meant to sit in the tray for weeks has to give all of them back. Memory growth would show up
# eventually; GDI and USER handle growth shows up much sooner, so both are sampled here.
#
# Cycles are driven by the Dim now button rather than by waiting to go idle: a leak test that
# only manages two cycles because somebody touched the mouse proves nothing.
#
# Dims to a gentle level rather than the usual one, because it does it many times over.

param([int]$Cycles = 8)

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Soak {
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
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr p, POINT pt, uint f);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

    /// Presses a control by posting to it, so nothing outside Dimly is ever clicked.
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
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);

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

    /// Genuine movement, so the watcher counts it as somebody coming back.
    public static void Wiggle() {
        mouse_event(0x0001, 2, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0001, unchecked((uint)-2), 0, 0, IntPtr.Zero);
    }

    /// 0 = GDI objects, 1 = USER objects.
    public static uint Handles(int pid, uint kind) {
        IntPtr process = OpenProcess(0x0400, false, pid);   // PROCESS_QUERY_INFORMATION
        if (process == IntPtr.Zero) return 0;
        uint count = GetGuiResources(process, kind);
        CloseHandle(process);
        return count;
    }

    static IntPtr Primary() {
        POINT origin; origin.X = 0; origin.Y = 0;
        uint count = 0;
        IntPtr monitor = MonitorFromPoint(origin, 1);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, ref count) || count == 0) return IntPtr.Zero;
        PM[] monitors = new PM[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, monitors)) return IntPtr.Zero;
        for (int i = 1; i < monitors.Length; i++) DestroyPhysicalMonitor(monitors[i].handle);
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
    $baseline = [Soak]::Read()
    if ($baseline -lt 0) { Start-Sleep -Milliseconds 400 }
}
if ($baseline -lt 0) { throw 'The monitor never answered a brightness query.' }
Write-Host "Baseline brightness: $baseline%"

New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=60'
    'IdleSeconds=5'
    'Fade=1'
    'FadeMillis=350'
    'DimOnLock=0'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=1'
    'IgnoreNoisyDevices=1'
    'RestoreFallback=100'
    'TrayHintShown=1'
    'StartHidden=0'
    'Theme=midnight'
    'DisabledDisplays='
)

try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    [Soak]::Wiggle()
    $app = Start-Process $exe -PassThru

    $rect = New-Object Soak+RECT
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $app.Refresh()
        if ($app.MainWindowHandle -ne 0) {
            [Soak]::GetWindowRect($app.MainWindowHandle, [ref]$rect) | Out-Null
            if (($rect.R - $rect.L) -gt 600) { break }
        }
    }
    if (($rect.R - $rect.L) -lt 600) { throw 'Dimly window never appeared.' }
    $window = $app.MainWindowHandle
    [Soak]::SetWindowPos($window, [IntPtr]::new(-1), 40, 40, 0, 0, 0x0001 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 800
    [Soak]::GetWindowRect($window, [ref]$rect) | Out-Null
    $scale = ($rect.B - $rect.T) / 840.0

    # The Dim now / Restore brightness button, in design coordinates.
    $bx = $rect.L + [int]((210 + 28 + 188 + 84) * $scale)
    $by = $rect.T + [int]((54 + 148 + 17) * $scale)

    $first = $null
    $dims = 0
    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        [Soak]::ClickAt($window, $bx, $by)          # Dim now
        Start-Sleep -Seconds 2
        $v = [Soak]::Read()
        $dimmed = ($v -ge 0 -and $v -le 70)
        if ($dimmed) { $dims++ }

        [Soak]::ClickAt($window, $bx, $by)          # Restore brightness
        Start-Sleep -Seconds 3

        $app.Refresh()
        $gdi = [Soak]::Handles($app.Id, 0)
        $user = [Soak]::Handles($app.Id, 1)
        $sample = [pscustomobject]@{
            Cycle = $cycle
            WorkingSetKB = [Math]::Round($app.WorkingSet64 / 1KB)
            PrivateKB = [Math]::Round($app.PrivateMemorySize64 / 1KB)
            Handles = $app.HandleCount
            Gdi = $gdi
            User = $user
        }
        if ($cycle -eq 1) { $first = $sample }
        "  cycle {0,2}  dimmed={1,-5}  ws={2,6} KB  private={3,6} KB  handles={4,4}  gdi={5,4}  user={6,4}" -f `
            $cycle, $dimmed, $sample.WorkingSetKB, $sample.PrivateKB, $sample.Handles, $sample.Gdi, $sample.User
        $last = $sample
    }

    Write-Host ''
    Write-Host ("dim cycles observed : {0} of {1}" -f $dims, $Cycles)
    Write-Host ("handles   {0} -> {1}" -f $first.Handles, $last.Handles)
    Write-Host ("GDI       {0} -> {1}" -f $first.Gdi, $last.Gdi)
    Write-Host ("USER      {0} -> {1}" -f $first.User, $last.User)
    Write-Host ("private   {0} -> {1} KB" -f $first.PrivateKB, $last.PrivateKB)

    $failures = 0
    function Check([string]$what, [bool]$ok) {
        Write-Host ("  {0}  {1}" -f $(if ($ok) { 'pass' } else { 'FAIL' }), $what)
        if (-not $ok) { $script:failures++ }
    }

    Write-Host ''
    if ($dims -eq 0) {
        Write-Host 'SKIPPED - it never dimmed, so nothing was exercised.' -ForegroundColor Yellow
    }
    else {
        Check "GDI objects did not creep up" ($last.Gdi -le ($first.Gdi + 15))
        Check "USER objects did not creep up" ($last.User -le ($first.User + 15))
        Check "handles did not creep up" ($last.Handles -le ($first.Handles + 40))
        Check "private memory did not creep up" ($last.PrivateKB -le ($first.PrivateKB + 4096))
        if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
        else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
    }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if ([Soak]::Read() -lt ($baseline - 3)) { [Soak]::Write($baseline) }
    Restore-DimlySettings
}
