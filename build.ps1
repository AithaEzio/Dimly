# Builds dist/Dimly.exe - one self-contained file, no installer, no runtime download.
# Targets .NET Framework 4.8, which ships with Windows 10 and 11, using the compiler
# that comes with it. Nothing else needs to be installed to build or to run.

param([switch]$Run)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc" }

$icon = Join-Path $root 'assets\dimly.ico'
if (-not (Test-Path $icon)) {
    Write-Host 'Generating icon...'
    & (Join-Path $root 'tools\make-icon.ps1')
}

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$output = Join-Path $dist 'Dimly.exe'

$sources = Get-ChildItem -Path (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$arguments = @(
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/nologo'
    '/warn:4'
    '/langversion:5'
    "/out:$output"
    "/win32icon:$icon"
    "/win32manifest:$(Join-Path $root 'src\App.manifest')"
    "/resource:$icon,Dimly.dimly.ico"
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Management.dll'
) + $sources

& $csc $arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }

$size = (Get-Item $output).Length
Write-Host ("Built {0}  ({1:N0} bytes)" -f $output, $size) -ForegroundColor Green

if ($Run) {
    Get-Process -Name Dimly -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Process $output
}
