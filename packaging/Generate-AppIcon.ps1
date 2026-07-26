param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\RabTrans.App\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RabbitIconBitmap([int]$size) {
    $scale = 4
    $canvas = $size * $scale
    $bitmap = [System.Drawing.Bitmap]::new($canvas, $canvas, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.ScaleTransform($canvas / 256.0, $canvas / 256.0)

    $outline = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 28, 30, 34), 15)
    $outline.StartCap = $outline.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $outline.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $ink = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 28, 30, 34))

    # A single continuous silhouette keeps the rabbit recognizable at tray-icon sizes.
    $rabbit = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $rabbit.StartFigure()
    $rabbit.AddBezier(78, 100, 65, 63, 66, 20, 88, 18)
    $rabbit.AddBezier(88, 18, 110, 16, 119, 61, 122, 89)
    $rabbit.AddBezier(122, 89, 126, 88, 130, 88, 134, 89)
    $rabbit.AddBezier(134, 89, 137, 61, 146, 16, 168, 18)
    $rabbit.AddBezier(168, 18, 190, 20, 191, 63, 178, 100)
    $rabbit.AddBezier(178, 100, 203, 114, 218, 137, 218, 166)
    $rabbit.AddBezier(218, 166, 218, 208, 181, 234, 128, 234)
    $rabbit.AddBezier(128, 234, 75, 234, 38, 208, 38, 166)
    $rabbit.AddBezier(38, 166, 38, 137, 53, 114, 78, 100)
    $rabbit.CloseFigure()
    $graphics.FillPath($white, $rabbit)
    $graphics.DrawPath($outline, $rabbit)

    # Minimal facial marks: two eyes and one small translation-arrow motif.
    $graphics.FillEllipse($ink, 82, 145, 20, 25)
    $graphics.FillEllipse($ink, 154, 145, 20, 25)
    $facePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 28, 30, 34), 11)
    $facePen.StartCap = $facePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $facePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawLine($facePen, 104, 195, 152, 195)
    $graphics.DrawLine($facePen, 142, 185, 152, 195)
    $graphics.DrawLine($facePen, 142, 205, 152, 195)

    $result = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $resultGraphics = [System.Drawing.Graphics]::FromImage($result)
    $resultGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $resultGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $resultGraphics.DrawImage($bitmap, 0, 0, $size, $size)

    $resultGraphics.Dispose()
    $facePen.Dispose()
    $rabbit.Dispose()
    $ink.Dispose()
    $white.Dispose()
    $outline.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    return $result
}

function Convert-ToIconDib([System.Drawing.Bitmap]$bitmap) {
    $width = $bitmap.Width
    $height = $bitmap.Height
    $rect = [System.Drawing.Rectangle]::new(0, 0, $width, $height)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $pixels = [byte[]]::new($data.Stride * $height)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    $bitmap.UnlockBits($data)

    $stream = [System.IO.MemoryStream]::new()
    $writer = [System.IO.BinaryWriter]::new($stream)
    $xorSize = $width * $height * 4
    $maskStride = [int]([Math]::Ceiling($width / 32.0) * 4)
    $maskSize = $maskStride * $height

    # BITMAPINFOHEADER followed by bottom-up BGRA pixels and a 1-bit alpha mask.
    $writer.Write([uint32]40)
    $writer.Write([int32]$width)
    $writer.Write([int32]($height * 2))
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]0)
    $writer.Write([uint32]$xorSize)
    $writer.Write([int32]0)
    $writer.Write([int32]0)
    $writer.Write([uint32]0)
    $writer.Write([uint32]0)

    for ($y = $height - 1; $y -ge 0; $y--) {
        $writer.Write($pixels, $y * $data.Stride, $width * 4)
    }
    for ($y = $height - 1; $y -ge 0; $y--) {
        $mask = [byte[]]::new($maskStride)
        for ($x = 0; $x -lt $width; $x++) {
            $alpha = $pixels[($y * $data.Stride) + ($x * 4) + 3]
            if ($alpha -eq 0) {
                $maskIndex = [int][Math]::Floor($x / 8.0)
                $mask[$maskIndex] = $mask[$maskIndex] -bor (0x80 -shr ($x % 8))
            }
        }
        $writer.Write($mask)
    }

    $writer.Flush()
    $bytes = $stream.ToArray()
    $writer.Dispose()
    $stream.Dispose()
    return $bytes
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()

foreach ($size in $sizes) {
    $bitmap = New-RabbitIconBitmap $size
    if ($size -eq 256) {
        $bitmap.Save((Join-Path $resolvedOutput 'RabTransIcon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    [byte[]]$frameBytes = Convert-ToIconDib $bitmap
    $frames += ,$frameBytes
    $bitmap.Dispose()
}

$iconPath = Join-Path $resolvedOutput 'RabTransIcon.ico'
$file = [System.IO.File]::Create($iconPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)

for ($index = 0; $index -lt $sizes.Count; $index++) {
    $size = $sizes[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$index].Length)
    $writer.Write([uint32]$offset)
    $offset += $frames[$index].Length
}

foreach ($frame in $frames) {
    $writer.Write([byte[]]$frame)
}

$writer.Dispose()
$file.Dispose()
Write-Host "Generated $iconPath and RabTransIcon.png"
