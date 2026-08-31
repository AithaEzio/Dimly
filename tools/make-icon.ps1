# Generates assets/dimly.ico (and PNG previews) for Dimly.
# Mark: a dimmed disc with a lit crescent - "the screen, half asleep".
# Each frame is rendered at 4x and downsampled so the 16px tray icon stays crisp.

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent $PSScriptRoot
$assets  = Join-Path $root 'assets'
$preview = Join-Path $assets 'preview'
New-Item -ItemType Directory -Force -Path $assets, $preview | Out-Null

function C([int]$a, [string]$hex) {
    $r = [Convert]::ToInt32($hex.Substring(0, 2), 16)
    $g = [Convert]::ToInt32($hex.Substring(2, 2), 16)
    $b = [Convert]::ToInt32($hex.Substring(4, 2), 16)
    [System.Drawing.Color]::FromArgb($a, $r, $g, $b)
}

function New-Mark([int]$size) {
    $ss  = [Math]::Max(4, [int](512 / $size))   # supersample factor
    $s   = $size * $ss
    $big = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g   = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = $s * 0.055
    $r   = ($s - 2 * $pad) / 2
    $cx  = $s / 2.0
    $cy  = $s / 2.0

    $disc = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disc.AddEllipse($cx - $r, $cy - $r, 2 * $r, 2 * $r)

    # Shadow circle carves the crescent out of the disc.
    $sr = $r * 1.15
    $sx = $cx + $r * 0.80
    $sy = $cy - $r * 0.05
    $shadow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $shadow.AddEllipse($sx - $sr, $sy - $sr, 2 * $sr, 2 * $sr)

    # 1. Halo behind the lit edge.
    $halo = New-Object System.Drawing.Drawing2D.GraphicsPath
    $hr = $r * 1.15
    $halo.AddEllipse($cx - $r * 0.55 - $hr, $cy - $hr, 2 * $hr, 2 * $hr)
    $hb = New-Object System.Drawing.Drawing2D.PathGradientBrush($halo)
    $hb.CenterColor    = (C 52 '55D8FF')
    $hb.SurroundColors = @((C 0 '55D8FF'))
    $g.FillPath($hb, $halo)
    $hb.Dispose(); $halo.Dispose()

    # 2. The unlit body of the disc.
    $bodyRect = New-Object System.Drawing.RectangleF(($cx - $r), ($cy - $r), (2 * $r), (2 * $r))
    $body = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bodyRect, (C 255 '2A3054'), (C 255 '11142A'), 70.0)
    $g.FillPath($body, $disc)
    $body.Dispose()

    # 3. The lit crescent: disc minus shadow.
    $g.SetClip($disc, [System.Drawing.Drawing2D.CombineMode]::Replace)
    $g.SetClip($shadow, [System.Drawing.Drawing2D.CombineMode]::Exclude)
    $lit = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bodyRect, (C 255 '8FEBFF'), (C 255 '9163FF'), 58.0)
    $g.FillRectangle($lit, $bodyRect)
    $lit.Dispose()
    $g.ResetClip()

    # 4. Rim, so the disc reads as a disc on any background.
    $penW = [Math]::Max(1.0, $s * 0.026)
    $rim = New-Object System.Drawing.Pen((C 170 '5566B4'), $penW)
    $g.DrawEllipse($rim, ($cx - $r + $penW / 2), ($cy - $r + $penW / 2), (2 * $r - $penW), (2 * $r - $penW))
    $rim.Dispose()

    $disc.Dispose(); $shadow.Dispose(); $g.Dispose()

    $out = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.Clear([System.Drawing.Color]::Transparent)
    $g2.DrawImage($big, (New-Object System.Drawing.Rectangle(0, 0, $size, $size)))
    $g2.Dispose(); $big.Dispose()
    $out
}

# ---- assemble the .ico ----------------------------------------------------
# Small frames are written as 32bpp DIBs (universally understood, including by
# System.Drawing.Icon); the 256px frame is PNG-compressed as convention requires.

function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $data = $bmp.LockBits((New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $rows = New-Object 'byte[]' ($data.Stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $rows, 0, $rows.Length)
    $bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($w * $h * 4 + ([Math]::Floor(($w + 31) / 32) * 4 * $h)))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($rows, $y * $data.Stride, $w * 4) }   # bottom-up
    $maskRow = [Math]::Floor(($w + 31) / 32) * 4
    $bw.Write((New-Object 'byte[]' ($maskRow * $h)))                                    # AND mask: all opaque
    $bw.Flush()
    $ms.ToArray()
}

function Get-PngBytes([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $ms.ToArray()
}

$frames = @()
foreach ($sz in 16, 20, 24, 32, 40, 48, 64, 128) {
    $bmp = New-Mark $sz
    $frames += [pscustomobject]@{ Size = $sz; Bytes = [byte[]](Get-DibBytes $bmp) }
    if ($sz -in 16, 32, 48) { $bmp.Save((Join-Path $preview "dimly-$sz.png"), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}
$big = New-Mark 256
$frames += [pscustomobject]@{ Size = 256; Bytes = [byte[]](Get-PngBytes $big) }
$big.Save((Join-Path $preview 'dimly-256.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$big.Dispose()

$icoPath = Join-Path $assets 'dimly.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $bw.Write([byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size })))
    $bw.Write([byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]$f.Bytes.Length); $bw.Write([int]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $bw.Write($f.Bytes, 0, $f.Bytes.Length) }
$bw.Flush(); $fs.Close()

Write-Host ("dimly.ico  {0} frames  {1:N0} bytes" -f $frames.Count, (Get-Item $icoPath).Length)
