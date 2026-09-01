# End-to-end check of "never dim while media is playing".
#
# Plays a quiet tone through the normal audio path - which is all Dimly looks at - and checks
# that the screen does NOT dim while it plays, then DOES dim once it stops. Nothing is clicked:
# the settings are seeded on disk and Dimly starts straight into the tray.
#
# Plays a faint 220 Hz tone for about fifteen seconds. Moves real screen brightness and puts
# it back if anything goes wrong.

$ErrorActionPreference = 'Stop'

Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class Media {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct PM {
        public IntPtr handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint cb, tick; }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);

    /// Milliseconds since the last real input. Everything this script does - reading DDC/CI,
    /// sleeping, playing audio - leaves this climbing, so it is a fair measure of "nobody home".
    public static long IdleMs() {
        LASTINPUT info = new LASTINPUT(); info.cb = 8;
        if (!GetLastInputInfo(ref info)) return -1;
        return (long)(uint)((uint)Environment.TickCount - info.tick);
    }

    [DllImport("dxva2.dll")] public static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr m, ref uint n);
    [DllImport("dxva2.dll")] public static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr m, uint n, [Out] PM[] a);
    [DllImport("dxva2.dll")] public static extern bool GetMonitorBrightness(IntPtr h, ref uint lo, ref uint c, ref uint hi);
    [DllImport("dxva2.dll")] public static extern bool SetMonitorBrightness(IntPtr h, uint v);
    [DllImport("dxva2.dll")] public static extern bool DestroyPhysicalMonitor(IntPtr h);

    /// A zero-distance move: real input to Windows, but the pointer does not budge.
    public static void Nudge() { mouse_event(0x0001, 0, 0, 0, IntPtr.Zero); }

    static IntPtr Primary() {
        POINT origin; origin.X = 0; origin.Y = 0;
        uint count = 0;
        IntPtr hMonitor = MonitorFromPoint(origin, 1);
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref count) || count == 0) return IntPtr.Zero;
        PM[] monitors = new PM[count];
        if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, count, monitors)) return IntPtr.Zero;
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
            SetMonitorBrightness(h, (uint)(lo + Math.Round((hi - lo) * percent / 100.0)));
        DestroyPhysicalMonitor(h);
    }
}
'@

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\Dimly.exe'
$settings = Join-Path $env:APPDATA 'Dimly\settings.ini'
$wav = Join-Path $env:TEMP 'dimly-tone.wav'

# A faint tone, loud enough to clear the silence threshold and quiet enough not to be a nuisance.
$rate = 44100; $seconds = 2; $frequency = 220; $amplitude = 0.015
$samples = $rate * $seconds
$stream = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([char[]]'RIFF'); $writer.Write([int](36 + $samples * 2)); $writer.Write([char[]]'WAVE')
$writer.Write([char[]]'fmt '); $writer.Write([int]16); $writer.Write([int16]1); $writer.Write([int16]1)
$writer.Write([int]$rate); $writer.Write([int]($rate * 2)); $writer.Write([int16]2); $writer.Write([int16]16)
$writer.Write([char[]]'data'); $writer.Write([int]($samples * 2))
for ($i = 0; $i -lt $samples; $i++) {
    $writer.Write([int16][Math]::Round([Math]::Sin(2 * [Math]::PI * $frequency * $i / $rate) * $amplitude * 32767))
}
$writer.Flush()
[System.IO.File]::WriteAllBytes($wav, $stream.ToArray())
$writer.Dispose(); $stream.Dispose()

$baseline = -1
for ($try = 0; $try -lt 8 -and $baseline -lt 0; $try++) {
    $baseline = [Media]::Read()
    if ($baseline -lt 0) { Start-Sleep -Milliseconds 400 }
}
if ($baseline -lt 0) { throw 'The monitor never answered a brightness query.' }
Write-Host "Baseline brightness: $baseline%"

New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
Set-Content -Path $settings -Value @(
    'AwayBrightness=40'
    'IdleSeconds=5'
    'Fade=0'
    'DimOnLock=1'
    'SkipFullscreen=0'
    'HoldWhileAudioPlays=1'
    'StartHidden=1'
    'Theme=midnight'
    'DisabledDisplays='
)

$failures = 0
$inconclusive = 0

function Check([string]$what, [bool]$ok) {
    Write-Host ("  {0}  {1}" -f $(if ($ok) { 'pass' } else { 'FAIL' }), $what)
    if (-not $ok) { $script:failures++ }
}

# A result only means something if the machine was actually left alone. Some machines have
# something injecting input constantly - a jiggler, a remote session, a busy peripheral - and
# on those nothing can ever go idle, so a green tick here would be a lie.
function CheckIdle([string]$what, [bool]$ok, [long]$idleSeen, [int]$neededSeconds) {
    if ($idleSeen -lt ($neededSeconds * 1000)) {
        Write-Host ("  skip  {0} (machine never idled past {1}s - saw {2}ms)" -f $what, $neededSeconds, $idleSeen)
        $script:inconclusive++
        return
    }
    Check $what $ok
}

# Refuse to start until the machine has been left alone for a moment.
$settled = $false
for ($i = 0; $i -lt 40; $i++) {
    if ([Media]::IdleMs() -ge 2000) { $settled = $true; break }
    Start-Sleep -Milliseconds 500
}
if (-not $settled) {
    Write-Host ''
    Write-Host 'SKIPPED - this machine reports no idle time at all.' -ForegroundColor Yellow
    Write-Host 'Something is injecting input continuously (a mouse jiggler, a remote-control'
    Write-Host 'session, or a peripheral). Nothing that waits for idle can be tested here -'
    Write-Host "Windows' own display timeout would be equally stuck. Try again when it stops."
    if (Test-Path $wav) { Remove-Item $wav }
    exit 0
}

$player = $null
try {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 500
    [Media]::Nudge()
    Start-Process $exe -ArgumentList '--tray' | Out-Null
    Start-Sleep -Seconds 4        # let it enumerate displays and start ticking

    # Playback on, then go completely quiet on the input front. The 5s delay will pass several
    # times over; nothing should dim, because sound is holding the countdown at zero.
    $player = New-Object System.Media.SoundPlayer $wav
    $player.PlayLooping()
    Write-Host 'Playing a faint tone; the screen should stay bright...'

    $whilePlaying = @()
    $idleWhilePlaying = 0
    for ($i = 0; $i -lt 14; $i++) {
        Start-Sleep -Seconds 1
        $whilePlaying += [Media]::Read()
        $now = [Media]::IdleMs()
        if ($now -gt $idleWhilePlaying) { $idleWhilePlaying = $now }
    }
    Write-Host ("  readings: " + ($whilePlaying -join ', ') + "   (peak idle ${idleWhilePlaying}ms)")
    $lowest = ($whilePlaying | Where-Object { $_ -ge 0 } | Measure-Object -Minimum).Minimum
    CheckIdle 'no dim while media is playing' ($lowest -ge ($baseline - 3)) $idleWhilePlaying 8

    # Stop playback but keep still. After the grace window plus the delay, it should dim.
    $player.Stop()
    Write-Host 'Stopped; the countdown should start now...'

    $afterStop = @()
    $idleAfterStop = 0
    for ($i = 0; $i -lt 18; $i++) {
        Start-Sleep -Seconds 1
        $afterStop += [Media]::Read()
        $now = [Media]::IdleMs()
        if ($now -gt $idleAfterStop) { $idleAfterStop = $now }
    }
    Write-Host ("  readings: " + ($afterStop -join ', ') + "   (peak idle ${idleAfterStop}ms)")

    # It must not dim the instant playback stops - the delay starts over from that moment.
    $early = @($afterStop[0..3] | Where-Object { $_ -ge 0 })
    Check 'no instant dim when playback stops' (($early | Measure-Object -Minimum).Minimum -ge ($baseline - 3))

    $late = @($afterStop[8..17] | Where-Object { $_ -ge 0 })
    CheckIdle 'dims once playback has stopped' (($late | Measure-Object -Maximum).Maximum -le 45) $idleAfterStop 12

    [Media]::Nudge()
    Start-Sleep -Seconds 3
    Check 'restores on input' ([Media]::Read() -ge ($baseline - 3))

    Write-Host ''
    if ($failures -gt 0) { Write-Host "$failures CHECK(S) FAILED" -ForegroundColor Red }
    elseif ($inconclusive -gt 0) { Write-Host "$inconclusive CHECK(S) INCONCLUSIVE - see the skip notes above" -ForegroundColor Yellow }
    else { Write-Host 'ALL CHECKS PASSED' -ForegroundColor Green }
}
finally {
    if ($player -ne $null) { $player.Stop(); $player.Dispose() }
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Milliseconds 400
    if ([Media]::Read() -lt ($baseline - 3)) {
        Write-Host "Restoring brightness to $baseline% ..." -ForegroundColor Yellow
        [Media]::Write($baseline)
    }
    if (Test-Path $settings) { Remove-Item $settings }
    if (Test-Path $wav) { Remove-Item $wav }
}
