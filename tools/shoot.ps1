# Development helper: launches dist/Dimly.exe and saves a screenshot of every page to
# assets/preview. Navigation is done by posting messages to Dimly's own child windows and
# capture uses PrintWindow, so nothing outside the app is clicked or brought to the front.
# Saved settings are reset first so every capture starts from defaults.

param([string]$Theme = '')

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT p, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, IntPtr extra);

    /// A zero-distance mouse move: it resets the session idle clock without moving the pointer,
    /// so Dimly stays in its "watching" state and never dims mid-capture. Posted messages do not
    /// count as input, so without this the screenshots would show an already-dimmed app - and a
    /// force-killed Dimly cannot run its restore, which would leave the screen dark.
    public static void Nudge() { mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); }

    /// Parks the window on top at a known spot so captures are stable and clicks are safe.
    public static void Park(IntPtr h, int x, int y) {
        SetWindowPos(h, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);   // HWND_TOPMOST, NOSIZE|NOACTIVATE
    }

    const uint CWP_SKIPINVISIBLE = 0x0001;
    const uint CWP_SKIPTRANSPARENT = 0x0004;

    /// Walks down to the deepest child at a screen point, staying inside this window tree.
    public static IntPtr Deepest(IntPtr root, int screenX, int screenY) {
        IntPtr current = root;
        for (int depth = 0; depth < 8; depth++) {
            POINT p; p.X = screenX; p.Y = screenY;
            ScreenToClient(current, ref p);
            IntPtr child = ChildWindowFromPointEx(current, p, CWP_SKIPINVISIBLE | CWP_SKIPTRANSPARENT);
            if (child == IntPtr.Zero || child == current) break;
            current = child;
        }
        return current;
    }

    public static void ClickAt(IntPtr root, int screenX, int screenY) {
        IntPtr target = Deepest(root, screenX, screenY);
        POINT p; p.X = screenX; p.Y = screenY;
        ScreenToClient(target, ref p);
        IntPtr lParam = (IntPtr)((p.Y << 16) | (p.X & 0xFFFF));
        PostMessage(target, 0x0201, (IntPtr)1, lParam);   // WM_LBUTTONDOWN
        System.Threading.Thread.Sleep(60);
        PostMessage(target, 0x0202, IntPtr.Zero, lParam); // WM_LBUTTONUP
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Dimly.exe'
$preview = Join-Path $root 'assets\preview'
New-Item -ItemType Directory -Force -Path $preview | Out-Null

Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# Screenshots should always show a fresh install, not whatever earlier runs left behind -
# but those settings belong to whoever is taking them, so they are put back at the end.
. (Join-Path $PSScriptRoot 'common.ps1')
Protect-DimlySettings
Wake-Screen
trap { Restore-DimlySettings; break }   # put them back even if this stops early
Remove-Item (Join-Path $env:APPDATA 'Dimly\settings.ini') -ErrorAction SilentlyContinue

[Shot]::Nudge()
$app = Start-Process $exe -PassThru

$rect = New-Object Shot+RECT
$handle = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 400
    $app.Refresh()
    $handle = $app.MainWindowHandle
    if ($handle -ne [IntPtr]::Zero) {
        [Shot]::GetWindowRect($handle, [ref]$rect) | Out-Null
        if (($rect.R - $rect.L) -gt 600) { break }
    }
}
if (($rect.R - $rect.L) -lt 600) { throw "No Dimly window appeared (handle=$handle)" }

[Shot]::Park($handle, 40, 40)
[Shot]::Nudge()
Start-Sleep -Milliseconds 500
[Shot]::GetWindowRect($handle, [ref]$rect) | Out-Null
$scale = ($rect.B - $rect.T) / 840.0
Write-Host "Window $($rect.R - $rect.L) x $($rect.B - $rect.T) at $($rect.L),$($rect.T)  scale $([Math]::Round($scale,2))"

function Capture([string]$name) {
    [Shot]::Nudge()
    Start-Sleep -Milliseconds 250
    [Shot]::GetWindowRect($handle, [ref]$rect) | Out-Null
    $bitmap = New-Object System.Drawing.Bitmap(($rect.R - $rect.L), ($rect.B - $rect.T))
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.CopyFromScreen($rect.L, $rect.T, 0, 0,
        (New-Object System.Drawing.Size(($rect.R - $rect.L), ($rect.B - $rect.T))))
    $g.Dispose()

    $path = Join-Path $preview "ui-$name.png"
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "  saved ui-$name.png"
}

function ClickDesign([double]$x, [double]$y) {
    [Shot]::Nudge()
    [Shot]::GetWindowRect($handle, [ref]$rect) | Out-Null
    [Shot]::ClickAt($handle, ($rect.L + [int]($x * $scale)), ($rect.T + [int]($y * $scale)))
    Start-Sleep -Milliseconds 450
}

function GoToPage([int]$index) {
    ClickDesign 105 (112 + ($index - 1) * 46 + 20)
}

if ($Theme -ne '') {
    GoToPage 3
    $slot = @{ 'midnight' = 0; 'neon' = 1; 'daylight' = 2 }[$Theme]
    $cardWidth = 900 - 210 - 56
    $swatchWidth = ($cardWidth - 40 - 28) / 3
    $x = 210 + 28 + 20 + $slot * ($swatchWidth + 14) + $swatchWidth / 2
    ClickDesign $x (54 + 44 + 70)
    Start-Sleep -Milliseconds 400
}

$suffix = if ($Theme -ne '') { "-$Theme" } else { '' }
GoToPage 1; Capture "away$suffix"
GoToPage 2; Capture "displays$suffix"
GoToPage 3; Capture "appearance$suffix"

Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force

Restore-DimlySettings
