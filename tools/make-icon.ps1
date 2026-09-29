<#
.SYNOPSIS
  Draws the Glossa icon in the mockup colours (Onest ExtraBold "G") and writes src\Glossa.App\Assets\glossa.ico.
.PARAMETER Variant
  plate  - dark tile, light plate, dark G: the word card's inverse highlight (default);
  letter - dark tile, light G, plate bar under it;
  light  - light tile, dark G.
.PARAMETER Preview
  Also write a PNG sheet with every variant at real sizes on a dark and a light taskbar.
#>
param(
    [ValidateSet('plate', 'letter', 'light')] [string]$Variant = 'plate',
    [string]$Preview
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$root = Split-Path $PSScriptRoot
$fonts = Join-Path $root 'src\Glossa.App\Assets\Fonts\'
$ico = Join-Path $root 'src\Glossa.App\Assets\glossa.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256

function Brush([string]$hex) {
    $b = New-Object Windows.Media.SolidColorBrush ([Windows.Media.ColorConverter]::ConvertFromString($hex))
    $b.Freeze()
    $b
}

# Dark theme tokens from Theme\Palettes.cs in sRGB: Page, card Plate, MarkInk, Ink.
$page = Brush '#0D1014'
$plate = Brush '#E2DED0'
$ink = Brush '#141B24'
$light = Brush '#F3F2ED'

$family = New-Object Windows.Media.FontFamily ((New-Object Uri ('file:///' + $fonts.Replace('\', '/'))), './#Onest')
$face = New-Object Windows.Media.Typeface ($family, [Windows.FontStyles]::Normal, [Windows.FontWeights]::ExtraBold, [Windows.FontStretches]::Normal)

# The G outline centred on its own ink box (not the line box) and scaled to height $h.
function Glyph([double]$h, [double]$cx, [double]$cy) {
    $ft = New-Object Windows.Media.FormattedText ('G', [Globalization.CultureInfo]::InvariantCulture,
        [Windows.FlowDirection]::LeftToRight, $face, 100, $page, 1.0)
    $g = $ft.BuildGeometry((New-Object Windows.Point 0, 0)).Clone()
    $b = $g.Bounds
    $k = $h / $b.Height
    $t = New-Object Windows.Media.TransformGroup
    $t.Children.Add((New-Object Windows.Media.TranslateTransform (-$b.X - $b.Width / 2), (-$b.Y - $b.Height / 2)))
    $t.Children.Add((New-Object Windows.Media.ScaleTransform $k, $k))
    $t.Children.Add((New-Object Windows.Media.TranslateTransform $cx, $cy))
    $g.Transform = $t
    $g
}

function Draw([string]$v, [int]$s) {
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $r = [Math]::Max(2, $s * 0.22)
    $tile = New-Object Windows.Rect 0, 0, $s, $s
    switch ($v) {
        'plate' {
            $dc.DrawRoundedRectangle($page, $null, $tile, $r, $r)
            # Small sizes keep a thin frame so the letter stays readable in the tray.
            $i = if ($s -le 24) { [Math]::Max(1, $s * 0.09) } else { $s * 0.14 }
            $p = New-Object Windows.Rect $i, $i, ($s - 2 * $i), ($s - 2 * $i)
            $pr = [Math]::Max(1, $r - $i * 0.6)
            $dc.DrawRoundedRectangle($plate, $null, $p, $pr, $pr)
            $dc.DrawGeometry($ink, $null, (Glyph ($p.Height * 0.62) ($s / 2) ($s / 2)))
        }
        'letter' {
            $dc.DrawRoundedRectangle($page, $null, $tile, $r, $r)
            $dc.DrawGeometry($light, $null, (Glyph ($s * 0.52) ($s / 2) ($s * 0.44)))
            $bw = $s * 0.5
            $bh = [Math]::Max(1.5, $s * 0.08)
            $bar = New-Object Windows.Rect (($s - $bw) / 2), ($s * 0.78), $bw, $bh
            $dc.DrawRoundedRectangle($plate, $null, $bar, $bh / 2, $bh / 2)
        }
        'light' {
            $dc.DrawRoundedRectangle($plate, $null, $tile, $r, $r)
            $dc.DrawGeometry($ink, $null, (Glyph ($s * 0.58) ($s / 2) ($s / 2)))
        }
    }
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $s, $s, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $bmp
}

function Png($bmp) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

# Classic icon entry: BITMAPINFOHEADER, bottom-up straight-alpha BGRA, then the 1-bit AND mask.
function Dib($bmp) {
    $s = $bmp.PixelWidth
    $conv = New-Object Windows.Media.Imaging.FormatConvertedBitmap ($bmp, [Windows.Media.PixelFormats]::Bgra32, $null, 0)
    $px = New-Object byte[] ($s * $s * 4)
    $conv.CopyPixels($px, $s * 4, 0)
    $maskStride = [int][Math]::Ceiling($s / 32.0) * 4
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$s); $w.Write([int]($s * 2)); $w.Write([int16]1); $w.Write([int16]32)
    $w.Write([int]0); $w.Write([int]($s * $s * 4 + $maskStride * $s))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { $w.Write($px, $y * $s * 4, $s * 4) }
    for ($y = $s - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $s; $x++) {
            if ($px[($y * $s + $x) * 4 + 3] -eq 0) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) }
        }
        $w.Write($row)
    }
    $w.Flush()
    , $ms.ToArray()
}

$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $b = Draw $Variant $s
    if ($s -eq 256) { $images.Add((Png $b)) } else { $images.Add((Dib $b)) }
}
$out = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $out
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $s = $sizes[$k]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$images[$k].Length); $w.Write([int]$offset)
    $offset += $images[$k].Length
}
foreach ($data in $images) { $w.Write($data) }
$w.Flush()
[IO.File]::WriteAllBytes($ico, $out.ToArray())
Write-Host "Icon ($Variant): $ico"

if ($Preview) {
    $shown = 128, 64, 48, 32, 24, 16
    $gap = 20
    $label = 90
    $half = ($shown | Measure-Object -Sum).Sum + 64 + $gap * ($shown.Count + 2)
    $rowH = 128 + 2 * $gap
    $variants = 'plate', 'letter', 'light'
    $dv = New-Object Windows.Media.DrawingVisual
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($dv, [Windows.Media.BitmapScalingMode]::NearestNeighbor)
    $dc = $dv.RenderOpen()
    $sheet = New-Object Windows.Rect 0, 0, ($label + 2 * $half), ($rowH * $variants.Count)
    $dc.DrawRectangle((Brush '#FFFFFF'), $null, $sheet)
    $ui = New-Object Windows.Media.Typeface 'Segoe UI'
    for ($v = 0; $v -lt $variants.Count; $v++) {
        $top = $v * $rowH
        $name = New-Object Windows.Media.FormattedText ($variants[$v], [Globalization.CultureInfo]::InvariantCulture,
            [Windows.FlowDirection]::LeftToRight, $ui, 15, (Brush '#202020'), 1.0)
        $dc.DrawText($name, (New-Object Windows.Point 12, ($top + $rowH / 2 - 10)))
        foreach ($bg in @(@{ X = $label; C = '#1F1F1F' }, @{ X = $label + $half; C = '#EEEEEE' })) {
            $dc.DrawRectangle((Brush $bg.C), $null, (New-Object Windows.Rect $bg.X, $top, $half, $rowH))
            $x = $bg.X + $gap
            foreach ($s in $shown) {
                $img = Draw $variants[$v] $s
                $y = $top + [Math]::Floor(($rowH - $s) / 2)
                $dc.DrawImage($img, (New-Object Windows.Rect $x, $y, $s, $s))
                $x += $s + $gap
            }
            # The tray size blown up 4x, to judge its pixels.
            $img = Draw $variants[$v] 16
            $dc.DrawImage($img, (New-Object Windows.Rect $x, ($top + ($rowH - 64) / 2), 64, 64))
        }
    }
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap ([int]$sheet.Width), ([int]$sheet.Height), 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    [IO.File]::WriteAllBytes($Preview, (Png $bmp))
    Write-Host "Preview: $Preview"
}
