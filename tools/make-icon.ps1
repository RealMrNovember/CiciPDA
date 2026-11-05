# Renders the CiciPDA logo (same geometry as docs\images\logo.svg) to src\app\app.ico and docs\images\logo.png.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0
    $m = 8 * $k; $w = 240 * $k; $r = 56 * $k
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90); $path.AddArc($m + $w - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $w - 2 * $r, $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc($m, $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90); $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.ColorTranslator]::FromHtml('#19E08B')), ([System.Drawing.ColorTranslator]::FromHtml('#22D3EE')))
    $g.FillPath($brush, $path)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml('#04110B')), (22 * $k)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 128 * $k, 52 * $k, 128 * $k, 114 * $k)
    # power arc: circle centre (128,128) radius 68, open at the top between angles -60 and -120 degrees
    $g.DrawArc($pen, (128 - 68) * $k, (128 - 68) * $k, 136 * $k, 136 * $k, -50, 280)
    $g.Dispose(); $pen.Dispose(); $brush.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { $b = Render $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }
$ico = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $root 'src\app\app.ico'), $ico.ToArray())
$big = Render 512; $big.Save((Join-Path $root 'docs\images\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()
Write-Host 'app.ico and logo.png written'
