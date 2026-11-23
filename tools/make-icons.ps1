# Regenerates the tray and app .ico files from the knob drawings in
# src/PowerModeSwitcher/Assets/KnobImages.xaml (which were converted from the SVGs).
#
# Run with Windows PowerShell 5.1 (it has WPF built in):
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-icons.ps1
#
# Sizes: 16-48 for the tray at different display scalings (40 = 16 at 250%),
# plus 64 and 256 for the .exe icon in File Explorer.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml

$assets = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\PowerModeSwitcher\Assets'
$dictionary = [System.Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assets 'KnobImages.xaml')))

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$outputs = [ordered]@{
    'KnobBattery'     = 'knob-battery-ice.ico'
    'KnobBalanced'    = 'knob-balanced-sailboat.ico'
    'KnobPerformance' = 'knob-performance-rocket.ico'
}

function Render-Image($image, [int]$size) {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.DrawImage($image, (New-Object System.Windows.Rect 0, 0, $size, $size))
    $dc.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    return $bitmap
}

# Classic icon image: 32-bit BGRA bitmap (bottom-up) plus a 1-bit transparency mask.
function New-BitmapEntry($image, [int]$size) {
    $straight = New-Object System.Windows.Media.Imaging.FormatConvertedBitmap (Render-Image $image $size), ([System.Windows.Media.PixelFormats]::Bgra32), $null, 0
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $straight.CopyPixels($pixels, $stride, 0)

    $maskStride = [int][math]::Ceiling($size / 32.0) * 4
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($stride * $size + $maskStride * $size))
    $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $stride) }
    for ($y = $size - 1; $y -ge 0; $y--) {
        $row = New-Object byte[] $maskStride
        for ($x = 0; $x -lt $size; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) { $row[$x -shr 3] = $row[$x -shr 3] -bor (0x80 -shr ($x -band 7)) }
        }
        $w.Write($row)
    }
    $w.Flush()
    return , $ms.ToArray()
}

# Large icon image stored as PNG (standard for 256 px).
function New-PngEntry($image, [int]$size) {
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create((Render-Image $image $size)))
    $ms = New-Object IO.MemoryStream
    $encoder.Save($ms)
    return , $ms.ToArray()
}

function Write-Icon([string]$path, $entries) {
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
    $offset = 6 + 16 * $entries.Count
    foreach ($e in $entries) {
        $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$e.Data.Length); $w.Write([int]$offset)
        $offset += $e.Data.Length
    }
    foreach ($e in $entries) { $w.Write($e.Data) }
    $w.Flush()
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
}

foreach ($key in $outputs.Keys) {
    $image = $dictionary[$key]
    $entries = foreach ($size in $sizes) {
        $data = if ($size -ge 256) { New-PngEntry $image $size } else { New-BitmapEntry $image $size }
        [pscustomobject]@{ Size = $size; Data = $data }
    }
    $path = Join-Path $assets $outputs[$key]
    Write-Icon $path @($entries)
    Write-Host "Wrote $path"
}
