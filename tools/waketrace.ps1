# Builds and runs tools/waketrace.cs against the shipping display code.
#
#   powershell -ExecutionPolicy Bypass -File tools/waketrace.ps1 [-OffSeconds 60] [-WatchSeconds 45]
#
# The screen goes dark for OffSeconds. Touching the mouse or keyboard during that wakes it
# early and the run proves nothing, so start it and leave the machine alone.

param([int]$OffSeconds = 60, [int]$WatchSeconds = 45)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $env:TEMP 'dimly-waketrace.exe'

& $csc /nologo /target:exe /langversion:5 "/out:$out" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /reference:System.Management.dll `
    (Join-Path $root 'src\Native.cs') (Join-Path $root 'src\Displays.cs') `
    (Join-Path $root 'tools\waketrace.cs')
if ($LASTEXITCODE -ne 0) { throw 'waketrace failed to build' }

Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

& $out $OffSeconds $WatchSeconds
