# Builds and runs tools/whynot.cs: shows every input Dimly's decision is made from, live.
#
# Run it while the countdown is stuck. Whichever column says BLOCKING is the reason, and if
# none of them do, the idle column will show the machine is not going idle at all.
#
#   powershell -ExecutionPolicy Bypass -File tools/whynot.ps1 -Seconds 90
#
# The log lands on the Desktop by default, so it can be read after walking back.

param([int]$Seconds = 60, [string]$Log = (Join-Path $env:USERPROFILE 'Desktop\dimly-diagnostics.txt'))

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $env:TEMP 'dimly-whynot.exe'

& $csc /nologo /target:exe /langversion:5 "/out:$out" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    (Join-Path $root 'src\Native.cs') (Join-Path $root 'tools\whynot.cs')
if ($LASTEXITCODE -ne 0) { throw 'whynot failed to build' }

& $out $Seconds $Log
Write-Host ''
Write-Host "Saved to $Log" -ForegroundColor Cyan
