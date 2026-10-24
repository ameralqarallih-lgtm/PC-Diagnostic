<#
.SYNOPSIS
    Draws the application icon (the dashboard's favicon: a dark rounded square with a cyan pulse line) as a
    multi-size .ico for the Agent, the Web host, the installer and the shortcuts.
.EXAMPLE
    installer\Make-Icon.ps1 -Output src\PcDiag.Agent\app.ico
#>
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string] $Output)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Draw-Image([int] $size) {
    $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $scale = $size / 32.0

    # Rounded square (rx 7 on a 32 grid), #0f172a.
    $radius = 7 * $scale
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $radius
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $fill = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0x0f, 0x17, 0x2a))
    $g.FillPath($fill, $path)

    # Pulse: M4 17 h6 l3-7 5 13 3-6 h7, #38bdf8, width 2.5, round caps and joins.
    $points = @(@(4, 17), @(10, 17), @(13, 10), @(18, 23), @(21, 17), @(28, 17)) | ForEach-Object {
        New-Object System.Drawing.PointF(($_[0] * $scale), ($_[1] * $scale))
    }
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(255, 0x38, 0xbd, 0xf8), [single][Math]::Max(1.5, 2.5 * $scale))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawLines($pen, [System.Drawing.PointF[]] $points)

    $pen.Dispose(); $fill.Dispose(); $path.Dispose(); $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        # Large sizes as PNG (supported in .ico since Windows Vista).
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        # Small sizes as a classic 32-bit DIB (every icon reader handles these): BITMAPINFOHEADER with the height
        # doubled for the AND mask, bottom-up BGRA rows, then an all-zero 1-bit AND mask (alpha decides).
        $writer = New-Object System.IO.BinaryWriter($stream)
        $writer.Write([uint32] 40); $writer.Write([int32] $size); $writer.Write([int32] ($size * 2))
        $writer.Write([uint16] 1); $writer.Write([uint16] 32); $writer.Write([uint32] 0)
        $writer.Write([uint32] ($size * $size * 4)); $writer.Write([int32] 0); $writer.Write([int32] 0)
        $writer.Write([uint32] 0); $writer.Write([uint32] 0)
        $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
        $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $pixels = New-Object byte[] ($data.Stride * $size)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        $bitmap.UnlockBits($data)
        for ($row = $size - 1; $row -ge 0; $row--) { $writer.Write($pixels, $row * $data.Stride, $size * 4) }
        $maskRow = [int]([Math]::Ceiling($size / 32.0) * 4)
        $writer.Write((New-Object byte[] ($maskRow * $size)))
        $writer.Flush()
    }
    $bitmap.Dispose()
    return , $stream.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 256)
$images = @($sizes | ForEach-Object { , (Draw-Image $_) })

# ICONDIR + ICONDIRENTRY[n] + image data.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($file)
$writer.Write([uint16] 0); $writer.Write([uint16] 1); $writer.Write([uint16] $sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte] $dimension); $writer.Write([byte] $dimension)
    $writer.Write([byte] 0); $writer.Write([byte] 0)
    $writer.Write([uint16] 1); $writer.Write([uint16] 32)
    $writer.Write([uint32] $images[$i].Length); $writer.Write([uint32] $offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write($image) }
$writer.Flush()
[System.IO.File]::WriteAllBytes((Join-Path (Get-Location) $Output), $file.ToArray())
Write-Host "Wrote $Output ($($file.Length) bytes, sizes $($sizes -join ', '))"
