# Generates every logo the Store package needs (and the exe icon) from one vector description:
# a rounded blue tile with a white treemap glyph. Run from anywhere; output goes to store\Assets.
param([string]$Out = (Join-Path $PSScriptRoot "Assets"))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = "Stop"
New-Item -ItemType Directory -Force $Out | Out-Null

$blue  = [System.Drawing.Color]::FromArgb(255, 31, 111, 219)   # Accent #1F6FDB
$blue2 = [System.Drawing.Color]::FromArgb(255, 22, 88, 180)

function Draw-Glyph([System.Drawing.Graphics]$g, [double]$x, [double]$y, [double]$size, [bool]$tile) {
    # tile background (optional, the Store composes its own plate for some sizes) + treemap glyph
    $g.SmoothingMode = "AntiAlias"
    if ($tile) {
        $r = [Math]::Max(2, $size * 0.18)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $path.AddArc($x, $y, $r * 2, $r * 2, 180, 90)
        $path.AddArc($x + $size - $r * 2, $y, $r * 2, $r * 2, 270, 90)
        $path.AddArc($x + $size - $r * 2, $y + $size - $r * 2, $r * 2, $r * 2, 0, 90)
        $path.AddArc($x, $y + $size - $r * 2, $r * 2, $r * 2, 90, 90)
        $path.CloseFigure()
        $p1 = New-Object System.Drawing.PointF([float]$x, [float]$y); $p2 = New-Object System.Drawing.PointF([float]$x, [float]($y + $size))
        $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($p1, $p2, $blue, $blue2)
        $g.FillPath($brush, $path)
    }
    # treemap cells: proportions of a squarified layout, gaps scale with size
    $gap = [Math]::Max(1, $size * 0.045)
    $pad = $size * 0.16
    $w = $size - 2 * $pad; $h = $w
    $cells = @(
        @(0.00, 0.00, 0.58, 0.62, 255),
        @(0.58, 0.00, 0.42, 0.38, 225),
        @(0.58, 0.38, 0.42, 0.24, 190),
        @(0.00, 0.62, 0.36, 0.38, 210),
        @(0.36, 0.62, 0.32, 0.38, 170),
        @(0.68, 0.62, 0.32, 0.20, 235),
        @(0.68, 0.82, 0.16, 0.18, 150),
        @(0.84, 0.82, 0.16, 0.18, 200)
    )
    foreach ($c in $cells) {
        $cx = $x + $pad + $c[0] * $w + $gap / 2; $cy = $y + $pad + $c[1] * $h + $gap / 2
        $cw = $c[2] * $w - $gap; $ch = $c[3] * $h - $gap
        $b = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb($c[4], 255, 255, 255))
        $g.FillRectangle($b, [float]$cx, [float]$cy, [float]$cw, [float]$ch)
    }
}

function New-Png([string]$name, [int]$w, [int]$h, [bool]$tile = $true, [double]$glyphFraction = 1.0) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($w -ne $h -and $tile) {
        # wide / splash: full blue background, glyph centred without its own tile
        $g.Clear($blue)
        $s = [Math]::Min($w, $h) * $glyphFraction
        Draw-Glyph $g (($w - $s) / 2) (($h - $s) / 2) $s $false
    } else {
        $s = [Math]::Min($w, $h) * $glyphFraction
        Draw-Glyph $g (($w - $s) / 2) (($h - $s) / 2) $s $tile
    }
    $g.Dispose()
    $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$scales = @{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0; 400 = 4.0 }
foreach ($k in $scales.Keys) {
    $f = $scales[$k]
    New-Png "Square44x44Logo.scale-$k.png"   ([int](44 * $f))  ([int](44 * $f))
    New-Png "Square71x71Logo.scale-$k.png"   ([int](71 * $f))  ([int](71 * $f))
    New-Png "Square150x150Logo.scale-$k.png" ([int](150 * $f)) ([int](150 * $f))
    New-Png "Square310x310Logo.scale-$k.png" ([int](310 * $f)) ([int](310 * $f))
    New-Png "Wide310x150Logo.scale-$k.png"   ([int](310 * $f)) ([int](150 * $f)) $true 0.8
    New-Png "StoreLogo.scale-$k.png"         ([int](50 * $f))  ([int](50 * $f))
    New-Png "SplashScreen.scale-$k.png"      ([int](620 * $f)) ([int](300 * $f)) $true 0.7
}
# taskbar / start list sizes: plated (with tile) and unplated (glyph only, on the system's plate)
foreach ($t in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    New-Png "Square44x44Logo.targetsize-$t.png" $t $t
    New-Png "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $false
}

# exe icon (.ico with PNG frames, Vista+)
$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::Transparent)
    Draw-Glyph $g 0 0 $s $true
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    $frames += ,@($s, $ms.ToArray())
}
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ico)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($fr in $frames) {
    $s = $fr[0]; $data = $fr[1]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($fr in $frames) { $bw.Write($fr[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "..\ByteScape.Wpf\ByteScape.ico"), $ico.ToArray())
"assets written to $Out ($((Get-ChildItem $Out).Count) files) + ByteScape.Wpf\ByteScape.ico"
