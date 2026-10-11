<#
.SYNOPSIS
    Generates every icon the app and its MSIX package need from one drawing.

.DESCRIPTION
    The Memos mark (a folded document with an M on a coloured tile) is drawn with vector
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
    [string]$TileStart = '#3969D5',
    [string]$TileEnd = '#254EAE',
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

    # One broad document silhouette survives at taskbar sizes.
    $simple = $detail -le 24
    $l = $s * 0.23
    $t = $s * 0.18
    $w = $s * 0.54
    $h = $s * 0.64
    $fold = $s * 0.14
    $r = $s * 0.05
    $right = $l + $w
    $bottom = $t + $h

    $page = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $page.AddArc($l, $t, $r * 2, $r * 2, 180, 90)
    $page.AddLine($l + $r, $t, $right - $fold, $t)
    $page.AddLine($right - $fold, $t, $right, $t + $fold)
    $page.AddArc($right - $r * 2, $bottom - $r * 2, $r * 2, $r * 2, 0, 90)
    $page.AddArc($l, $bottom - $r * 2, $r * 2, $r * 2, 90, 90)
    $page.CloseFigure()

    $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#FFFFFF')), $page)

    $corner = New-Polygon @(($right - $fold), $t, ($right - $fold), ($t + $fold), $right, ($t + $fold))
    $g.FillPath([System.Drawing.SolidBrush]::new((ColorOf '#BED3FF')), $corner)

    # The M is the sole interior detail. Use a heavier stroke on tiny variants.
    $pen = [System.Drawing.Pen]::new((ColorOf '#315CC8'), [single]($s * $(if ($simple) { 0.075 } else { 0.066 })))
    $pen.LineJoin = 'Round'
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($s * 0.335, $s * 0.655),
        [System.Drawing.PointF]::new($s * 0.335, $s * 0.42),
        [System.Drawing.PointF]::new($s * 0.50, $s * 0.59),
        [System.Drawing.PointF]::new($s * 0.665, $s * 0.42),
        [System.Drawing.PointF]::new($s * 0.665, $s * 0.655))
    $g.DrawLines($pen, $points)

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
