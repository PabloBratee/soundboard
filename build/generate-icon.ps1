[CmdletBinding()]
param(
    [string]$OutputPath = (
        Join-Path $PSScriptRoot '..\src\Soundboard.App\Assets\Soundboard.ico')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Drawing.Common

function New-RoundedRectanglePath {
    param(
        [System.Drawing.RectangleF]$Bounds,
        [float]$Radius
    )

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($Bounds.Left, $Bounds.Top, $diameter, $diameter, 180, 90)
    $path.AddArc(
        $Bounds.Right - $diameter,
        $Bounds.Top,
        $diameter,
        $diameter,
        270,
        90)
    $path.AddArc(
        $Bounds.Right - $diameter,
        $Bounds.Bottom - $diameter,
        $diameter,
        $diameter,
        0,
        90)
    $path.AddArc(
        $Bounds.Left,
        $Bounds.Bottom - $diameter,
        $diameter,
        $diameter,
        90,
        90)
    $path.CloseFigure()
    return $path
}

function New-IconPng {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode =
            [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode =
            [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

        $scale = $Size / 256.0
        $outer = [System.Drawing.RectangleF]::new(
            [float](12 * $scale),
            [float](12 * $scale),
            [float](232 * $scale),
            [float](232 * $scale))
        $inner = [System.Drawing.RectangleF]::new(
            [float](52 * $scale),
            [float](52 * $scale),
            [float](152 * $scale),
            [float](152 * $scale))

        $outerPath = New-RoundedRectanglePath $outer ([float](52 * $scale))
        $innerPath = New-RoundedRectanglePath $inner ([float](32 * $scale))
        $outerBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#111827'))
        $innerBrush = [System.Drawing.SolidBrush]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#0F1726'))
        $outerPen = [System.Drawing.Pen]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#314462'),
            [float]([Math]::Max(1, 8 * $scale)))
        $innerPen = [System.Drawing.Pen]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#3A5072'),
            [float]([Math]::Max(1, 6 * $scale)))
        try {
            $graphics.FillPath($outerBrush, $outerPath)
            $graphics.DrawPath($outerPen, $outerPath)
            $graphics.FillPath($innerBrush, $innerPath)
            $graphics.DrawPath($innerPen, $innerPath)
        }
        finally {
            $outerPath.Dispose()
            $innerPath.Dispose()
            $outerBrush.Dispose()
            $innerBrush.Dispose()
            $outerPen.Dispose()
            $innerPen.Dispose()
        }

        $signalPen = [System.Drawing.Pen]::new(
            [System.Drawing.ColorTranslator]::FromHtml('#61D7FF'),
            [float]([Math]::Max(1.5, 13 * $scale)))
        try {
            $signalPen.StartCap =
                [System.Drawing.Drawing2D.LineCap]::Round
            $signalPen.EndCap =
                [System.Drawing.Drawing2D.LineCap]::Round
            $signalPen.LineJoin =
                [System.Drawing.Drawing2D.LineJoin]::Round

            [System.Drawing.PointF[]]$points = @(
                [System.Drawing.PointF]::new(72 * $scale, 128 * $scale),
                [System.Drawing.PointF]::new(90 * $scale, 128 * $scale),
                [System.Drawing.PointF]::new(90 * $scale, 100 * $scale),
                [System.Drawing.PointF]::new(108 * $scale, 100 * $scale),
                [System.Drawing.PointF]::new(108 * $scale, 164 * $scale),
                [System.Drawing.PointF]::new(126 * $scale, 164 * $scale),
                [System.Drawing.PointF]::new(126 * $scale, 78 * $scale),
                [System.Drawing.PointF]::new(144 * $scale, 78 * $scale),
                [System.Drawing.PointF]::new(144 * $scale, 178 * $scale),
                [System.Drawing.PointF]::new(162 * $scale, 178 * $scale),
                [System.Drawing.PointF]::new(162 * $scale, 114 * $scale),
                [System.Drawing.PointF]::new(180 * $scale, 114 * $scale),
                [System.Drawing.PointF]::new(180 * $scale, 142 * $scale),
                [System.Drawing.PointF]::new(198 * $scale, 142 * $scale)
            )
            $graphics.DrawLines($signalPen, $points)
        }
        finally {
            $signalPen.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $stream.Position = 0
        return $stream
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @($sizes | ForEach-Object { New-IconPng -Size $_ })
$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$stream = [System.IO.FileStream]::new(
    $resolvedOutput,
    [System.IO.FileMode]::Create,
    [System.IO.FileAccess]::Write,
    [System.IO.FileShare]::None)
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $iconDimension = if ($size -eq 256) { 0 } else { $size }
        $writer.Write([byte]$iconDimension)
        $writer.Write([byte]$iconDimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$images[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $images[$index].Length
    }

    foreach ($image in $images) {
        $writer.Write($image.ToArray())
    }
}
finally {
    $writer.Dispose()
    foreach ($image in $images) {
        $image.Dispose()
    }
}

Write-Output "Generated $resolvedOutput with sizes: $($sizes -join ', ')"
