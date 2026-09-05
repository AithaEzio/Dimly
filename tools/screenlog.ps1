# Records everything that happens around your screens while you are away, so a stuck-dim
# monitor can be diagnosed from evidence rather than guesswork.
#
#   powershell -ExecutionPolicy Bypass -File tools/screenlog.ps1
#
#   -Minutes 15   how long to record for  (default 15 - long enough for a 3 minute screen
#                 timeout plus ten minutes away, with room to spare)
#   -Every 5      seconds between readings (default 5)
#   -NoProbe      do not ask the monitors anything at all. Use this for a second run if you
#                 suspect the asking is itself keeping a monitor awake.
#   -Out <path>   where to write the log (default: dimly-screenlog.txt on your Desktop)
#
# It only watches. It never changes a brightness, never touches Dimly, and never produces
# input, so leaving the machine alone with this running is the same as leaving it alone.

param(
    [int]$Minutes = 15,
    [int]$Every = 5,
    [switch]$NoProbe,
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $env:TEMP 'dimly-screenlog.exe'

& $csc /nologo /target:exe /langversion:5 "/out:$exe" `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    (Join-Path $root 'tools\screenlog.cs')
if ($LASTEXITCODE -ne 0) { throw 'screenlog failed to build' }

$arguments = @('-minutes', $Minutes, '-every', $Every)
if ($NoProbe) { $arguments += '-noprobe' }
if ($Out) { $arguments += @('-out', $Out) }

Write-Host ''
Write-Host 'Starting the recorder.' -ForegroundColor Cyan
Write-Host 'Leave the machine completely alone once it starts - do not move the mouse,'
Write-Host 'and do not touch the keyboard. Let Windows switch the screen off by itself,'
Write-Host 'stay away for several minutes after that, then wake it as you normally would.'
Write-Host ''

& $exe $arguments
