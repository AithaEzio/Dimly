# Checks the Displays page really is a brightness control: that its slider moves the monitor,
# that "Use current" records the level as that display's fallback, and that setting brightness
# by hand releases a dim rather than fighting it.
#
# Moves real screen brightness and puts it back.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Panel {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PM {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr p, POINT pt, uint f);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);

    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint cb, tick; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);

    /// How long since a person last touched this machine. The clicks below are posted straight
    /// to the control, so they never disturb it - only a real hand does.
    public static long IdleMs() {
        LASTINPUT info = new LASTINPUT(); info.cb = 8;
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.tick);
    }

    /// Genuine movement, so the app counts it as somebody coming back.
    public static void Wiggle() {
        mouse_event(0x0001, 2, 0, 0, IntPtr.Zero);
        System.Threading.Thread.Sleep(60);
        mouse_event(0x0001, unchecked((uint)-2), 0, 0, IntPtr.Zero);
    }

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint c, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

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
        System.Threading.Thread.Sleep(80);
        PostMessage(current, 0x0202, IntPtr.Zero, lParam);
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
    $baseline = [Panel]::Read()
    if ($baseline -lt 0) { Start-Sleep -Milliseconds 400 }
}
if ($baseline -lt 0) { throw 'The monitor never answered a brightness query.' }
Write-Host "Baseline brightness: $baseline%"

New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=30'
    'IdleSeconds=1800'
    'Fade=0'
    'DimOnLock=0'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=0'
    'IgnoreNoisyDevices=0'
    'RestoreFallback=100'
    'TrayHintShown=1'
    'StartHidden=0'
    'Theme=midnight'
    'DisabledDisplays='
    'DisplayFallbacks='
)

$failures = 0
function Check([string]$what, [bool]$ok) {
    Write-Host ("  {0}  {1}" -f $(if ($ok) { 'pass' } else { 'FAIL' }), $what)
    if (-not $ok) { $script:failures++ }
}

try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    $app = Start-Process $exe -PassThru

    $rect = New-Object Panel+RECT
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $app.Refresh()
        if ($app.MainWindowHandle -ne 0) {
            [Panel]::GetWindowRect($app.MainWindowHandle, [ref]$rect) | Out-Null
            if (($rect.R - $rect.L) -gt 600) { break }
        }
    }
    if (($rect.R - $rect.L) -lt 600) { throw 'Dimly window never appeared.' }
    $window = $app.MainWindowHandle
    [Panel]::SetWindowPos($window, [IntPtr]::new(-1), 40, 40, 0, 0, 0x0001 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 800
    [Panel]::GetWindowRect($window, [ref]$rect) | Out-Null
    $scale = ($rect.B - $rect.T) / 840.0

    # Displays page.
    [Panel]::ClickAt($window, ($rect.L + [int](105 * $scale)), ($rect.T + [int](178 * $scale)))
    Start-Sleep -Seconds 2

    # The first display's card sits at the top of the page: content starts at x=238, y=54.
    # Its brightness slider spans x 20..(pageWidth-20) inside the card at card y=150, and a
    # slider's usable track is inset by the knob radius plus its hover ring.
    $pageWidth = 900 - 210 - 56
    $edge = 12
    $trackLeft = 238 + 20 + $edge
    $trackSpan = ($pageWidth - 40) - 2 * $edge
    $target = 40
    $bx = $rect.L + [int](($trackLeft + $trackSpan * $target / 100.0) * $scale)
    $by = $rect.T + [int]((54 + 150 + 15) * $scale)

    Write-Host "Setting the first display to about $target% from the page..."
    [Panel]::ClickAt($window, $bx, $by)
    Start-Sleep -Seconds 3

    $after = [Panel]::Read()
    Write-Host "  monitor now reads $after%"
    Check 'the slider moved the monitor' ([Math]::Abs($after - $target) -le 6)

    # "Use current brightness" records that level as this display's fallback. The button is
    # 200 wide against the card's right edge, at card y=246.
    $ux = $rect.L + [int](($pageWidth + 238 - 220 + 100) * $scale)
    $uy = $rect.T + [int]((54 + 246 + 16) * $scale)
    [Panel]::ClickAt($window, $ux, $uy)
    Start-Sleep -Seconds 2

    $line = ((Get-Content $settings) -match '^DisplayFallbacks=') -join ''
    Write-Host "  $line"
    $recorded = $false
    if ($line -match '=(\d+)$') { $recorded = [Math]::Abs([int]$Matches[1] - $after) -le 6 }
    Check 'Use current brightness recorded it as this display''s fallback' $recorded

    # --- the dim still happens while this page is watching --------------------------
    # The page reads every level again whenever the engine changes state. A refresh that
    # cancelled the engine's own work left the app saying "Dimmed" over a bright screen.
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    [Panel]::Write($baseline)

    Set-Content -Path $settings -Value @(
        'AwayBrightness=30'
        'IdleSeconds=6'
        'Fade=1'
        'FadeMillis=700'
        'DimOnLock=0'
        'SkipFullscreen=0'
        'HoldWhileAudioPlays=0'
        'IgnoreNoisyDevices=1'
        'RestoreFallback=85'
        'TrayHintShown=1'
        'StartHidden=0'
        'Theme=midnight'
        'DisabledDisplays='
        'DisplayFallbacks='
        'ManualRestoreDisplays='
    )

    # Auto restore ships on, so this also checks the level it is given is the level it uses -
    # 85, deliberately not the brightness the screen happens to be at.
    Write-Host ''
    Write-Host 'Leaving it alone on the Displays page to see whether it really dims...'
    $app = Start-Process $exe -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $app.Refresh()
        if ($app.MainWindowHandle -ne 0) {
            [Panel]::GetWindowRect($app.MainWindowHandle, [ref]$rect) | Out-Null
            if (($rect.R - $rect.L) -gt 600) { break }
        }
    }
    if (($rect.R - $rect.L) -lt 600) { throw 'Dimly window never appeared.' }
    $window = $app.MainWindowHandle
    [Panel]::SetWindowPos($window, [IntPtr]::new(-1), 40, 40, 0, 0, 0x0001 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 800
    [Panel]::GetWindowRect($window, [ref]$rect) | Out-Null
    $scale = ($rect.B - $rect.T) / 840.0

    [Panel]::ClickAt($window, ($rect.L + [int](105 * $scale)), ($rect.T + [int](178 * $scale)))

    $watchSeconds = 22
    $dimmed = -1
    for ($i = 0; $i -lt $watchSeconds; $i++) {
        Start-Sleep -Seconds 1
        $now = [Panel]::Read()
        if ($now -ge 0 -and $now -le 45) { $dimmed = $now; break }
    }

    # Posted clicks do not reset the idle clock, so anything that did was a real hand.
    $idle = [Panel]::IdleMs()
    if ($dimmed -lt 0 -and $idle -lt ($watchSeconds * 1000)) {
        Write-Host "  skip  it dims with this page open (somebody used the machine: idle ${idle}ms)" -ForegroundColor Yellow
    }
    else {
        Write-Host "  screen went to $dimmed%"
        Check 'it dims with this page open' ($dimmed -ge 0)

        [Panel]::Wiggle()
        $restored = -1
        for ($i = 0; $i -lt 12; $i++) {
            Start-Sleep -Seconds 1
            $restored = [Panel]::Read()
            if ([Math]::Abs($restored - 85) -le 5) { break }
        }
        Write-Host "  screen came back to $restored%"
        Check 'and comes back to the level Auto restore was given' ([Math]::Abs($restored - 85) -le 5)
    }

    # --- Auto restore switched off puts back the brightness it found ----------------
    # Driven through the toggle itself, so this checks the switch really reaches the engine
    # and not just the settings file. Dimming is driven by the Dim now button rather than by
    # waiting to go idle: the app must not dim before the switch has been thrown, or it would
    # capture in the mode being switched away from and prove nothing.
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    Set-Content -Path $settings -Value @(
        'AwayBrightness=30'
        'IdleSeconds=1800'
        'Fade=0'
        'DimOnLock=0'
        'SkipFullscreen=0'
        'HoldWhileAudioPlays=0'
        'IgnoreNoisyDevices=1'
        'RestoreFallback=85'
        'TrayHintShown=1'
        'StartHidden=0'
        'Theme=midnight'
        'DisabledDisplays='
        'DisplayFallbacks='
        'ManualRestoreDisplays='
    )

    $held = 65
    [Panel]::Write($held)
    Start-Sleep -Milliseconds 600

    $app = Start-Process $exe -PassThru
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $app.Refresh()
        if ($app.MainWindowHandle -ne 0) {
            [Panel]::GetWindowRect($app.MainWindowHandle, [ref]$rect) | Out-Null
            if (($rect.R - $rect.L) -gt 600) { break }
        }
    }
    if (($rect.R - $rect.L) -lt 600) { throw 'Dimly window never appeared.' }
    $window = $app.MainWindowHandle
    [Panel]::SetWindowPos($window, [IntPtr]::new(-1), 40, 40, 0, 0, 0x0001 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 800
    [Panel]::GetWindowRect($window, [ref]$rect) | Out-Null
    $scale = ($rect.B - $rect.T) / 840.0

    [Panel]::ClickAt($window, ($rect.L + [int](105 * $scale)), ($rect.T + [int](178 * $scale)))
    Start-Sleep -Seconds 2

    # The Auto restore switch sits at the right of its row: the row spans x 20..(width-20)
    # inside the card at card y=190, and the switch is 44 wide against that right edge.
    $tx = $rect.L + [int](($pageWidth + 238 - 20 - 22) * $scale)
    $ty = $rect.T + [int]((54 + 190 + 26) * $scale)
    [Panel]::ClickAt($window, $tx, $ty)
    Start-Sleep -Seconds 2

    $line = ((Get-Content $settings) -match '^ManualRestoreDisplays=') -join ''
    Write-Host "  $line"
    Check 'the toggle recorded this display as manual' ($line -match '=.+')

    # Back to Away & dimming, and dim on demand rather than by waiting.
    [Panel]::ClickAt($window, ($rect.L + [int](105 * $scale)), ($rect.T + [int](133 * $scale)))
    Start-Sleep -Seconds 1

    $ox = $rect.L + [int]((210 + 28 + 188 + 84) * $scale)
    $oy = $rect.T + [int]((54 + 148 + 17) * $scale)

    [Panel]::ClickAt($window, $ox, $oy)
    Start-Sleep -Seconds 3
    $dimmed2 = [Panel]::Read()
    Write-Host "  Dim now took it to $dimmed2%"
    Check 'it still dims with Auto restore off' ($dimmed2 -ge 0 -and $dimmed2 -le 45)

    [Panel]::ClickAt($window, $ox, $oy)
    $back = -1
    for ($i = 0; $i -lt 10; $i++) {
        Start-Sleep -Seconds 1
        $back = [Panel]::Read()
        if ([Math]::Abs($back - $held) -le 4) { break }
    }
    Write-Host "  screen came back to $back% (it was at $held%, auto restore level is 85%)"
    Check 'and puts back the brightness it actually found' ([Math]::Abs($back - $held) -le 4)

    Write-Host ''
    if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
    else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    if ([Panel]::Read() -ne $baseline) {
        Write-Host "Putting brightness back to $baseline% ..." -ForegroundColor Yellow
        [Panel]::Write($baseline)
    }
    Restore-DimlySettings
}
