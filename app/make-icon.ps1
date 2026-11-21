#Requires -Version 5.1
# Renders the Nolagi logo (N with a lightning-bolt diagonal) into app\nolagi.ico and app\nolagi.png.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$data = 'M32,92 L32,28 L66,60 L52,64 L88,92 L88,28'

function New-LogoPng([int]$size) {
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = $size / 120.0
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform($s, $s)))
    $bg = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x11, 0x18, 0x27))
    $dc.DrawRoundedRectangle($bg, $null, (New-Object System.Windows.Rect(0, 0, 120, 120)), 28, 28)
    $pen = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromRgb(0x38, 0xBD, 0xF8))), $(if ($size -le 16) { 12 } elseif ($size -le 32) { 10.5 } else { 9 }))
    $pen.LineJoin = 'Round'; $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    $dc.DrawGeometry($null, $pen, [System.Windows.Media.Geometry]::Parse($data))
    $dc.Pop(); $dc.Close()
    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($z in $sizes) { $pngs[$z] = New-LogoPng $z }
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'nolagi.png'), (New-LogoPng 512))

# ICO with PNG-compressed entries (supported since Vista)
$ms = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ms)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($z in $sizes) {
    $b = $pngs[$z]
    $w.Write([byte]($z % 256)); $w.Write([byte]($z % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$b.Length); $w.Write([uint32]$offset)
    $offset += $b.Length
}
foreach ($z in $sizes) { $w.Write($pngs[$z]) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'nolagi.ico'), $ms.ToArray())
Write-Host 'nolagi.ico + nolagi.png written'
