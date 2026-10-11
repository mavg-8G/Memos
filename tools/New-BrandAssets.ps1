<#
.SYNOPSIS
    Generates every icon the app and its MSIX package need from one drawing.

.DESCRIPTION
    The Memos mark (a sheet of paper with a pencil on a coloured tile) is drawn with vector
    primitives, so it stays sharp at every size. Running this script rewrites:

      Memos/Memos/Assets/Memos.ico            window, taskbar and executable icon
      Memos/Memos (Package)/Images/*.png      tiles, Start/taskbar icons, Store logo, splash screen

    To rebrand, change the colours below (or the drawing in Draw-Mark) and run it again:

      pwsh tools/New-BrandAssets.ps1

.PARAMETER TileStart
    Top-left colour of the tile gradient.
.PARAMETER TileEnd
    Bottom-right colour of the tile gradient.
#>
param(
    [string]$TileStart = '#3C8CFF',
    [string]$TileEnd = '#5A3FD8',
    [string]$PreviewPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$assets = Join-Path $repo 'Memos\Memos\Assets'
$images = Join-Path $repo 'Memos\Memos (Package)\Images'

function ColorOf([string]$hex, [int]$alpha = 255) {
    $c = [System.Drawing.ColorTranslator]::FromHtml($hex)
    [System.Drawing.Color]::FromArgb($alpha, $c.R, $c.G, $c.B)
}

function New-RoundedRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [single]($r * 2)
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function New-Polygon([single[]]$points) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $list = for ($i = 0; $i -lt $points.Count; $i += 2) { [System.Drawing.PointF]::new($points[$i], $points[$i + 1]) }
    $path.AddPolygon([System.Drawing.PointF[]]$list)
    $path
}

# Draws the mark in a square of side $s whose top-left corner is ($ox, $oy).
# $detail is the size in pixels the mark will finally be shown at; small sizes get a simpler drawing.
function Draw-Mark([System.Drawing.Graphics]$g, [single]$ox, [single]$oy, [single]$s, [int]$detail) {
    $state = $g.Save()
    $g.TranslateTransform($ox, $oy)

    # Tile
    $tile = New-RoundedRect ($s * 0.04) ($s * 0.04) ($s * 0.92) ($s * 0.92) ($s * 0.205)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($s, $s), (ColorOf $TileStart), (ColorOf $TileEnd))
    $g.FillPath($gradient, $tile)

    # Soft light from the top, as on Fluent app icons
    $g.SetClip($tile)
    $shine = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new(0, $s * 0.6), (ColorOf '#FFFFFF' 46), (ColorOf '#FFFFFF' 0))
    $g.FillRectangle($shine, 0, 0, $s, $s * 0.6)
    $g.ResetClip()

    # Sheet of paper with a folded corner
    $simple = $detail -le 24
    $l = $s * $(if ($simple) { 0.25 } else { 0.235 })
    $t = $s * 0.17
    $w = $s * $(if ($simple) { 0.50 } else { 0.47 })
    $h = $s * 0.66
    $fold = $s * 0.15
    $r = $s * 0.045
    $right = $l + $w
    $bottom = $t + $h

    $page = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $page.AddArc($l, $t, $r * 2, $r * 2, 180, 90)
    $page.AddLine($l + $r, $t, $right - $fold, $t)
    $page.AddLine($right - $fold, $t, $right, $t + $fold)
    $page.AddArc($right - $r * 2, $bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $page.AddArc($l, $bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $page.CloseFigure()

    $shadow = $page.Clone()
    $matrix = [System.Drawing.Drawing2D.Matrix]::new(); $matrix.Translate(0, $s * 0.022); $shadow.Transform($matrix)
    $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#1A1060' 70)), $shadow)
    $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#FFFFFF')), $page)

    $corner = New-Polygon @(($right - $fold), $t, ($right - $fold), ($t + $fold), $right, ($t + $fold))
    $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#C6D6FF')), $corner)

    # Lines of text: a heading and body lines
    $x = $l + $s * 0.075
    $lines = if ($simple) {
        @(@(0.43, 0.20, 0.075, '#3D6BF0'), @(0.60, 0.30, 0.065, '#9FB3EA'))
    } else {
        @(@(0.415, 0.17, 0.052, '#3D6BF0'), @(0.525, 0.31, 0.036, '#A9BBEE'), @(0.615, 0.25, 0.036, '#A9BBEE'), @(0.705, 0.16, 0.036, '#A9BBEE'))
    }
    foreach ($line in $lines) {
        $thickness = [single]($s * $line[2])
        $bar = New-RoundedRect $x ($s * $line[0] - $thickness / 2) ($s * $line[1]) $thickness ($thickness / 2.001)
        $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf $line[3])), $bar)
    }

    # Pencil, writing on the sheet. Left out at the smallest sizes, where it would only be noise.
    if (-not $simple) {
        $inner = $g.Save()
        $g.TranslateTransform($s * 0.565, $s * 0.795)
        $g.RotateTransform(45)
        $u = $s
        $half = $u * 0.058

        $outline = [System.Drawing.Drawing2D.GraphicsPath]::new()
        $outline.AddPolygon([System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($half, -$u * 0.105),
            [System.Drawing.PointF]::new($half, -$u * 0.43), [System.Drawing.PointF]::new(-$half, -$u * 0.43),
            [System.Drawing.PointF]::new(-$half, -$u * 0.105)))
        $halo = [System.Drawing.Pen]::new((ColorOf '#2A1C8F' 60), $u * 0.035); $halo.LineJoin = 'Round'
        $g.DrawPath($halo, $outline)

        # Eraser, ferrule, body, wood, lead
        $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#FF8FA3')), (New-RoundedRect (-$half) (-$u * 0.43) ($half * 2) ($u * 0.075) ($u * 0.022)))
        $g.FillRectangle([System.Drawing.SolidBrush]::new((ColorOf '#D7DCE8')), -$half, -$u * 0.375, $half * 2, $u * 0.04)
        $body = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new(-$half, 0), [System.Drawing.PointF]::new($half, 0), (ColorOf '#FFD24A'), (ColorOf '#FFB01F'))
        $g.FillRectangle($body, -$half, -$u * 0.336, $half * 2, $u * 0.232)
        $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#F6DFBA')), (New-Polygon @((-$half), (-$u * 0.105), $half, (-$u * 0.105), ($half * 0.42), (-$u * 0.044), (-$half * 0.42), (-$u * 0.044))))
        $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#33334D')), (New-Polygon @((-$half * 0.42), (-$u * 0.044), ($half * 0.42), (-$u * 0.044), 0, 0)))
        $g.Restore($inner)
    }

    $g.Restore($state)
}

# Renders a canvas of $width x $height with the mark centred, occupying $fill of the shorter side.
function New-Bitmap([int]$width, [int]$height, [double]$fill = 1.0) {
    $scale = if ([Math]::Max($width, $height) -ge 512) { 2 } else { 4 }
    $big = [System.Drawing.Bitmap]::new($width * $scale, $height * $scale, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $side = [Math]::Min($width, $height) * $scale * $fill
    $final = [int][Math]::Round([Math]::Min($width, $height) * $fill)
    Draw-Mark $g ([single](($width * $scale - $side) / 2)) ([single](($height * $scale - $side) / 2)) ([single]$side) $final
    $g.Dispose()

    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'
    $g.CompositingQuality = 'HighQuality'
    $g.DrawImage($big, [System.Drawing.Rectangle]::new(0, 0, $width, $height))
    $g.Dispose(); $big.Dispose()
    $bitmap
}

function Save-Png([string]$path, [int]$width, [int]$height, [double]$fill = 1.0) {
    $bitmap = New-Bitmap $width $height $fill
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Get-PngBytes([int]$size) {
    $bitmap = New-Bitmap $size $size
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , $stream.ToArray()
}

if ($PreviewPath) {
    # A contact sheet to judge the drawing at several sizes without touching the project.
    $sizes = 256, 96, 48, 32, 24, 16
    $sheet = [System.Drawing.Bitmap]::new(560, 300)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.Clear((ColorOf '#F3F3F3')); $g.FillRectangle([System.Drawing.SolidBrush]::new((ColorOf '#202020')), 280, 0, 280, 300)
    foreach ($offset in 0, 280) {
        $x = $offset + 12
        $big = New-Bitmap 160 160; $g.DrawImageUnscaled($big, $x, 12); $big.Dispose()
        $cx = $x
        foreach ($size in 96, 48, 32, 24, 16) { if ($size -eq 96) { continue }; $b = New-Bitmap $size $size; $g.DrawImageUnscaled($b, $cx, 190); $b.Dispose(); $cx += $size + 14 }
        $b = New-Bitmap 96 96; $g.DrawImageUnscaled($b, $x + 170, 40); $b.Dispose()
    }
    $g.Dispose(); $sheet.Save($PreviewPath, [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()
    "Preview written to $PreviewPath"
    return
}

New-Item -ItemType Directory -Force $assets, $images | Out-Null

# --- Memos.ico: PNG-compressed entries, which Windows 10 and 11 read at every size.
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$entries = foreach ($size in $icoSizes) { , (Get-PngBytes $size) }
$ico = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ico)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $dimension = [byte]($icoSizes[$i] -band 0xFF) # 256 is stored as 0
    $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$entries[$i].Length); $writer.Write([uint32]$offset)
    $offset += $entries[$i].Length
}
foreach ($entry in $entries) { $writer.Write([byte[]]$entry) }
[System.IO.File]::WriteAllBytes((Join-Path $assets 'Memos.ico'), $ico.ToArray())

# --- Package images
Get-ChildItem $images -Filter *.png | Remove-Item -Force

$scales = @{ 100 = 1.0; 125 = 1.25; 150 = 1.5; 200 = 2.0; 400 = 4.0 }
foreach ($scale in $scales.Keys) {
    $k = $scales[$scale]
    # Windows expects half pixels rounded up (71 x 1.5 = 107), not to the nearest even number.
    $px = { param($n) [int][Math]::Floor($n * $k + 0.5) }
    Save-Png (Join-Path $images "Square44x44Logo.scale-$scale.png") (& $px 44) (& $px 44) 0.9
    Save-Png (Join-Path $images "SmallTile.scale-$scale.png") (& $px 71) (& $px 71) 0.66
    Save-Png (Join-Path $images "Square150x150Logo.scale-$scale.png") (& $px 150) (& $px 150) 0.56
    Save-Png (Join-Path $images "Wide310x150Logo.scale-$scale.png") (& $px 310) (& $px 150) 0.56
    Save-Png (Join-Path $images "LargeTile.scale-$scale.png") (& $px 310) (& $px 310) 0.46
    Save-Png (Join-Path $images "StoreLogo.scale-$scale.png") (& $px 50) (& $px 50) 0.9
    Save-Png (Join-Path $images "SplashScreen.scale-$scale.png") (& $px 620) (& $px 300) 0.42
}

# Icons Windows uses in the taskbar, Start, Alt+Tab and Explorer; "unplated" means no backplate is added.
foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Save-Png (Join-Path $images "Square44x44Logo.targetsize-$size.png") $size $size
    Save-Png (Join-Path $images "Square44x44Logo.targetsize-${size}_altform-unplated.png") $size $size
    Save-Png (Join-Path $images "Square44x44Logo.targetsize-${size}_altform-lightunplated.png") $size $size
}

"Wrote Memos.ico ($($icoSizes.Count) sizes) and $((Get-ChildItem $images -Filter *.png).Count) package images."
