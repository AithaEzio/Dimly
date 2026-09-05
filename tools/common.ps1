# Shared by the end-to-end checks. Dot-source it:
#
#   . (Join-Path $PSScriptRoot 'common.ps1')
#   Protect-DimlySettings
#   Wake-Screen
#   ...
#   finally { Restore-DimlySettings }

# ---------------------------------------------------------------- the settings file
#
# Every check here seeds settings.ini with what it needs, and used to delete the file on the way
# out. That quietly threw away whatever the person running it had configured - their away level,
# their delay, and the restore level chosen for each individual display - which is a high price
# for running a test. These put it back exactly as it was.

$script:DimlySettingsPath = Join-Path $env:APPDATA 'Dimly\settings.ini'
$script:DimlySettingsSaved = $null
$script:DimlySettingsExisted = $false

function Protect-DimlySettings {
    $script:DimlySettingsExisted = Test-Path $script:DimlySettingsPath
    if ($script:DimlySettingsExisted) {
        $script:DimlySettingsSaved = Get-Content -Path $script:DimlySettingsPath -Raw
    }
    New-Item -ItemType Directory -Force -Path (Split-Path $script:DimlySettingsPath) | Out-Null
}

function Restore-DimlySettings {
    if ($script:DimlySettingsExisted) {
        Set-Content -Path $script:DimlySettingsPath -Value $script:DimlySettingsSaved `
                    -NoNewline -Encoding UTF8
    }
    elseif (Test-Path $script:DimlySettingsPath) {
        # There was nothing here before, so leaving the seeded file behind would be just as wrong.
        Remove-Item $script:DimlySettingsPath
    }
}

# ------------------------------------------------------------------- the screen itself
#
# Windows switches the screen off on its own display timeout, and Dimly then deliberately leaves
# every monitor alone: one being powered down refuses every command, and acting on those
# refusals is how a working display gets written off as broken. A check that starts in that
# state therefore measures the power state rather than the application, and reports a confident,
# completely false failure. Wake the screen and let the monitors come back before starting.

Add-Type -Name Screen -Namespace DimlyCheck -MemberDefinition @'
[DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
[StructLayout(LayoutKind.Sequential)] public struct LASTINPUT { public uint cb, tick; }
[DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUT info);
'@

function Wake-Screen {
    param([int]$SettleSeconds = 5)

    [DimlyCheck.Screen]::mouse_event(0x0001, 2, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 80
    [DimlyCheck.Screen]::mouse_event(0x0001, [uint32]::MaxValue - 1, 0, 0, [IntPtr]::Zero)

    # A monitor coming out of power save takes three to five seconds to light up, and answers
    # nothing at all until it has. Nothing worth measuring can happen before that.
    Start-Sleep -Seconds $SettleSeconds
}
