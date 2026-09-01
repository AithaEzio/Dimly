# Checks that EVERY connected display is dimmed and restored together, whatever channel each
# one uses: displays that answer DDC/CI are measured directly, and displays driven by the
# software overlay are verified by finding their layered, click-through, on-top windows.
#
# Moves real screen brightness. Puts every display back if anything goes wrong.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class Multi {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PM {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    public delegate bool MonitorEnumProc(IntPtr h, IntPtr hdc, ref RECT clip, IntPtr data);
    public delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr p, POINT pt, uint f);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint c, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_TRANSPARENT = 0x00000020;
    const int WS_EX_LAYERED = 0x00080000;

    /// Keeps the session non-idle, so only the button under test dims anything.
    public static void Nudge() { mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); }

    static List<IntPtr> Screens() {
        List<IntPtr> screens = new List<IntPtr>();
        MonitorEnumProc collect = delegate(IntPtr h, IntPtr hdc, ref RECT clip, IntPtr data) {
            screens.Add(h);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
        return screens;
    }

    /// Brightness of every display, in enumeration order. -1 means "does not answer DDC/CI",
    /// which is exactly the case the overlay exists to cover.
    public static int[] ReadAll() {
        List<int> values = new List<int>();
        foreach (IntPtr screen in Screens()) {
            uint count = 0;
            int value = -1;
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(screen, ref count) && count > 0) {
                PM[] monitors = new PM[count];
                if (GetPhysicalMonitorsFromHMONITOR(screen, count, monitors)) {
                    uint lo = 0, cur = 0, hi = 0;
                    if (GetMonitorBrightness(monitors[0].handle, ref lo, ref cur, ref hi) && hi > lo)
                        value = (int)Math.Round((cur - (double)lo) * 100.0 / (hi - lo));
                    foreach (PM m in monitors) DestroyPhysicalMonitor(m.handle);
                }
            }
            values.Add(value);
        }
        return values.ToArray();
    }

    public static void WriteAll(int level) {
        foreach (IntPtr screen in Screens()) {
            uint count = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(screen, ref count) || count == 0) continue;
            PM[] monitors = new PM[count];
            if (!GetPhysicalMonitorsFromHMONITOR(screen, count, monitors)) continue;
            foreach (PM m in monitors) {
                uint lo = 0, cur = 0, hi = 0;
                if (GetMonitorBrightness(m.handle, ref lo, ref cur, ref hi) && hi > lo)
                    SetMonitorBrightness(m.handle, (uint)(lo + Math.Round((hi - lo) * level / 100.0)));
                DestroyPhysicalMonitor(m.handle);
            }
        }
    }

    /// Every visible layered window the process owns - Dimly's overlay dimmers.
    public static List<string> Overlays(uint target) {
        List<string> found = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr l) {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != target || !IsWindowVisible(h)) return true;
            int ex = GetWindowLong(h, GWL_EXSTYLE);
            if ((ex & WS_EX_LAYERED) == 0) return true;
            RECT r;
            GetWindowRect(h, out r);
            found.Add(string.Format("{0}x{1} at {2},{3}  click-through={4}  always-on-top={5}",
                r.R - r.L, r.B - r.T, r.L, r.T,
                (ex & WS_EX_TRANSPARENT) != 0, (ex & WS_EX_TOPMOST) != 0));
            return true;
        }, IntPtr.Zero);
        return found;
    }

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

# The first DDC/CI query after a monitor has been idle often goes unanswered, so settle first.
$baseline = @()
for ($try = 0; $try -lt 8; $try++) {
    $baseline = [Multi]::ReadAll()
    if (($baseline | Where-Object { $_ -ge 0 }).Count -gt 0) { break }
    Start-Sleep -Milliseconds 400
}
if ($baseline.Count -eq 0) { throw 'No displays found.' }

$ddc = @(0..($baseline.Count - 1) | Where-Object { $baseline[$_] -ge 0 })
$soft = @(0..($baseline.Count - 1) | Where-Object { $baseline[$_] -lt 0 })
Write-Host "Displays: $($baseline.Count)   answering DDC/CI: $($ddc.Count)   overlay-only: $($soft.Count)"
Write-Host "Baselines: $($baseline -join ', ')"

$failures = 0
function Check([string]$what, [bool]$ok) {
    Write-Host ("  {0}  {1}" -f $(if ($ok) { 'pass' } else { 'FAIL' }), $what)
    if (-not $ok) { $script:failures++ }
}

try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    if (Test-Path $settings) { Remove-Item $settings }
    Start-Sleep -Milliseconds 500

    [Multi]::Nudge()
    $app = Start-Process $exe -PassThru
    $rect = New-Object Multi+RECT
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 400
        [Multi]::Nudge()
        $app.Refresh()
        if ($app.MainWindowHandle -ne 0) {
            [Multi]::GetWindowRect($app.MainWindowHandle, [ref]$rect) | Out-Null
            if (($rect.R - $rect.L) -gt 600) { break }
        }
    }
    $handle = $app.MainWindowHandle
    if (($rect.R - $rect.L) -lt 600) { throw 'Dimly window never appeared.' }

    [Multi]::SetWindowPos($handle, [IntPtr]::new(-1), 40, 40, 0, 0, 0x0001 -bor 0x0010) | Out-Null
    Start-Sleep -Milliseconds 500
    [Multi]::GetWindowRect($handle, [ref]$rect) | Out-Null

    Check 'no overlays before dimming' (([Multi]::Overlays([uint32]$app.Id)).Count -eq 0)

    [Multi]::ClickAt($handle, ($rect.L + 510), ($rect.T + 219))    # Dim now
    Start-Sleep -Milliseconds 2500

    $dimmed = [Multi]::ReadAll()
    $overlays = [Multi]::Overlays([uint32]$app.Id)
    Write-Host "  while dimmed: $($dimmed -join ', ')"
    foreach ($o in $overlays) { Write-Host "  overlay: $o" }

    $allDown = $true
    foreach ($i in $ddc) { if ($dimmed[$i] -lt 0 -or $dimmed[$i] -gt 25) { $allDown = $false } }
    Check "every DDC/CI display dimmed ($($ddc.Count) of them)" $allDown
    Check "one overlay per display without DDC/CI ($($soft.Count))" ($overlays.Count -eq $soft.Count)
    if ($overlays.Count -gt 0) {
        Check 'overlays are click-through and on top' (
            ($overlays | Where-Object { $_ -match 'click-through=True\s+always-on-top=True' }).Count -eq $overlays.Count)
    }

    [Multi]::ClickAt($handle, ($rect.L + 510), ($rect.T + 219))    # Restore brightness
    Start-Sleep -Milliseconds 2500

    $back = [Multi]::ReadAll()
    Write-Host "  after restore: $($back -join ', ')"
    $allBack = $true
    foreach ($i in $ddc) { if ($back[$i] -lt ($baseline[$i] - 3)) { $allBack = $false } }
    Check 'every DDC/CI display restored' $allBack
    Check 'overlays withdrawn' (([Multi]::Overlays([uint32]$app.Id)).Count -eq 0)

    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 900
    $final = [Multi]::ReadAll()
    $stillBack = $true
    foreach ($i in $ddc) { if ($final[$i] -lt ($baseline[$i] - 3)) { $stillBack = $false } }
    Check 'still restored after exit' $stillBack

    Write-Host ''
    if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
    else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    $now = [Multi]::ReadAll()
    $needsHelp = $false
    foreach ($i in $ddc) { if ($now[$i] -ge 0 -and $now[$i] -lt ($baseline[$i] - 3)) { $needsHelp = $true } }
    if ($needsHelp) {
        Write-Host 'Putting displays back...' -ForegroundColor Yellow
        [Multi]::WriteAll(100)
    }
    if (Test-Path $settings) { Remove-Item $settings }
}
