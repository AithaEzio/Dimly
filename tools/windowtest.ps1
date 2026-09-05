# Checks the settings window behaves like a tray app's window should:
#   - closing it only puts it away, and hands its memory back to Windows
#   - launching Dimly again brings that window back rather than doing nothing
#
# The second one is easy to get wrong and silent when it breaks: the app keeps running, the
# shortcut appears to do nothing, and the only way back is the tray icon.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Win {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr h, out int pid);

    /// The settings window: the one visible top-level window this process owns that is
    /// anywhere near full size.
    public static IntPtr Settings(int pid) {
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, null, null)) != IntPtr.Zero) {
            int owner; GetWindowThreadProcessId(h, out owner);
            if (owner != pid || !IsWindowVisible(h)) continue;
            RECT r; GetWindowRect(h, out r);
            if ((r.R - r.L) > 600 && (r.B - r.T) > 600) return h;
        }
        return IntPtr.Zero;
    }

    /// What clicking the close button sends. A bare WM_CLOSE is not the same thing: it does
    /// not come from the user, so the window closes for real instead of putting itself away.
    public static void Close(IntPtr window) {
        PostMessage(window, 0x0112, (IntPtr)0xF060, IntPtr.Zero);
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Dimly.exe'
$settings = Join-Path $env:APPDATA 'Dimly\settings.ini'

. (Join-Path $PSScriptRoot 'common.ps1')
Protect-DimlySettings
Wake-Screen

$failures = 0
function Check([string]$what, [bool]$ok) {
    Write-Host ("  {0}  {1}" -f $(if ($ok) { 'pass' } else { 'FAIL' }), $what)
    if (-not $ok) { $script:failures++ }
}

function WaitForWindow($app, [int]$seconds = 16) {
    for ($i = 0; $i -lt ($seconds * 4); $i++) {
        Start-Sleep -Milliseconds 250
        $found = [Win]::Settings($app.Id)
        if ($found -ne [IntPtr]::Zero) { return $found }
    }
    return [IntPtr]::Zero
}

New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=30'
    'IdleSeconds=1800'
    'Fade=1'
    'DimOnLock=0'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=0'
    'IgnoreNoisyDevices=1'
    'RestoreFallback=100'
    'TrayHintShown=1'
    'StartHidden=0'
    'Theme=midnight'
    'DisabledDisplays='
    'DisplayFallbacks='
    'ManualRestoreDisplays='
)

try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500


    $app = Start-Process $exe -PassThru
    $window = WaitForWindow $app
    Check 'the window opens' ($window -ne [IntPtr]::Zero)
    if ($window -eq [IntPtr]::Zero) { throw 'no window to test' }

    Start-Sleep -Seconds 3
    $app.Refresh()
    $open = [Math]::Round($app.WorkingSet64 / 1KB)
    Write-Host "  with the window open: $open KB"

    [Win]::Close($window)
    Start-Sleep -Seconds 4
    $app.Refresh()
    $away = [Math]::Round($app.WorkingSet64 / 1KB)
    Write-Host "  put away:             $away KB"

    Check 'closing it only puts it away, leaving Dimly running' (-not $app.HasExited)
    Check 'the window is gone from the screen' ([Win]::Settings($app.Id) -eq [IntPtr]::Zero)
    Check 'its memory went back to Windows' ($away -lt ($open / 2))

    # Launching it again is what a desktop or Start-menu shortcut does while it is running.
    Start-Process $exe | Out-Null
    $again = WaitForWindow $app 8
    Check 'launching Dimly again brings the window back' ($again -ne [IntPtr]::Zero)

    Write-Host ''
    if ($failures -eq 0) { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
    else { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
}
finally {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Restore-DimlySettings
}
