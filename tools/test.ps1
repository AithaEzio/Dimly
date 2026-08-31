# Runs the checks that do not need a person at the keyboard.
#   enginetest - the real DimEngine driven against stand-ins for Win32 idle and the displays
#   probe      - the real display enumeration against this machine's actual hardware
#
# tools/functest.ps1 and tools/idletest.ps1 are separate: they move real screen brightness.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = @('/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll',
          '/reference:System.Windows.Forms.dll', '/reference:System.Management.dll')

function Build([string]$name, [string[]]$sources) {
    $out = Join-Path $env:TEMP "dimly-$name.exe"
    & $csc (@('/nologo', '/target:exe', '/langversion:5', "/out:$out") + $refs +
            ($sources | ForEach-Object { Join-Path $root $_ }))
    if ($LASTEXITCODE -ne 0) { throw "$name failed to build" }
    $out
}

Write-Host '== engine state machine ==' -ForegroundColor Cyan
$engine = Build 'enginetest' @('src\DimEngine.cs', 'src\AppSettings.cs', 'src\Theme.cs', 'src\AppInfo.cs', 'tools\enginetest.cs')
& $engine
$engineOk = $LASTEXITCODE -eq 0

Write-Host ''
Write-Host '== display enumeration on this machine ==' -ForegroundColor Cyan
$probe = Build 'probe' @('src\Native.cs', 'src\Displays.cs', 'tools\probe.cs')
& $probe

Write-Host ''
if ($engineOk) { Write-Host 'Engine checks passed.' -ForegroundColor Green }
else { Write-Host 'Engine checks FAILED.' -ForegroundColor Red; exit 1 }
