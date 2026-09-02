# End-to-end check: drives Dimly's manual override button and watches the monitor's real DDC/CI
# brightness go down and come back. Restores brightness itself if anything goes wrong.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Ddc {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PHYSICAL_MONITOR {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr p, POINT pt, uint f);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PHYSICAL_MONITOR[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint cur, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    static IntPtr monitor = IntPtr.Zero;

    public static bool Open() {
        POINT origin; origin.X = 0; origin.Y = 0;
        IntPtr hMonitor = MonitorFromPoint(origin, 1);   // MONITOR_DEFAULTTOPRIMARY
        uint count = 0;
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

if (-not [Ddc]::Open()) { throw 'No DDC/CI monitor to measure.' }
$baseline = -1
for ($try = 0; $try -lt 8 -and $baseline -lt 0; $try++) {
    $baseline = [Ddc]::Read()
    if ($baseline -lt 0) { Start-Sleep -Milliseconds 400 }
}
if ($baseline -lt 0) { throw 'The monitor never answered a brightness query.' }
Write-Host "Baseline brightness: $baseline%"

if ($baseline -lt 50) {
    Write-Host ''
    Write-Host "SKIPPED - the display is already at $baseline%, at or near the level this test" -ForegroundColor Yellow
    Write-Host 'dims to, so dimming would prove nothing. Run tools/restore.ps1 first.'
    exit 0
}

# Seed the settings this test assumes, rather than inheriting whatever was left behind. A run
# that dims to 30% is only evidence if the display did not start there already.
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
)


try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    $app = Start-Process $exe -PassThru

    $handle = [IntPtr]::Zero
    $rect = New-Object Ddc+RECT
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        $app.Refresh()
        $handle = $app.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero) {
            [Ddc]::GetWindowRect($handle, [ref]$rect) | Out-Null
            if (($rect.R - $rect.L) -gt 600) { break }
        }
    }
    if (($rect.R - $rect.L) -lt 600) { throw 'Dimly window never appeared.' }

    [Ddc]::SetWindowPos($handle, [IntPtr]::new(-1), 40, 40, 0, 0, 0x0001 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 600
    [Ddc]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $scale = ($rect.B - $rect.T) / 840.0

    # The Dim now / Restore brightness button, in design coordinates inside the window.
    $px = $rect.L + [int]((210 + 28 + 188 + 84) * $scale)
    $py = $rect.T + [int]((54 + 148 + 17) * $scale)

    Write-Host 'Clicking Dim now...'
    [Ddc]::ClickAt($handle, $px, $py)

    $readings = @()
    for ($i = 0; $i -lt 8; $i++) { Start-Sleep -Milliseconds 400; $readings += [Ddc]::Read() }
    Write-Host ("While dimmed: " + ($readings -join ', '))
    # A read can fail while the monitor is busy handling a write; -1 is "no answer", not "dark".
    $valid = @($readings | Where-Object { $_ -ge 0 })
    if ($valid.Count -eq 0) { throw 'The monitor never answered a brightness query.' }
    $dimmed = ($valid | Measure-Object -Minimum).Minimum

    Write-Host 'Clicking Restore brightness...'
    [Ddc]::ClickAt($handle, $px, $py)
    Start-Sleep -Milliseconds 2000

    # One unanswered query is not a failed restore; ask again before believing it.
    $restored = -1
    for ($i = 0; $i -lt 8 -and $restored -lt 0; $i++) {
        $restored = [Ddc]::Read()
        if ($restored -lt 0) { Start-Sleep -Milliseconds 400 }
    }
    Write-Host "After restore: $restored%"

    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 600
    $final = [Ddc]::Read()

    Write-Host ''
    # Auto restore ships on, so coming back means coming back to the level it was given -
    # RestoreFallback above - and not to whatever the screen happened to be at beforehand.
    Write-Host "baseline=$baseline  dimmed=$dimmed  restored=$restored  afterExit=$final  (auto restore level: 100)"
    if ($dimmed -le 35 -and $restored -ge 97 -and $final -ge 97) {
        Write-Host 'PASS - dimmed to the away level and came back.' -ForegroundColor Green
    } else {
        Write-Host 'FAIL - see the numbers above.' -ForegroundColor Red
    }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 300
    if ([Ddc]::Read() -lt ($baseline - 3)) {
        Write-Host "Restoring brightness to $baseline% ..." -ForegroundColor Yellow
        [Ddc]::Write($baseline)
    }
    if (Test-Path $settings) { Remove-Item $settings }
    [Ddc]::Close()
}
