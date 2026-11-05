# Renders the CiciPDA logo (same geometry as docs\images\logo.svg) to
#   src\app\app.ico, docs\images\logo.png (512 mark) and docs\images\logo-horizontal.png (lockup with wordmark).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
function Col($h, $a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($h); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }

function DrawMark([System.Drawing.Graphics]$g, [float]$ox, [float]$oy, [float]$size) {
    $k = $size / 256.0
    $m = 8 * $k; $w = 240 * $k; $r = 56 * $k
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($ox + $m, $oy + $m, 2 * $r, 2 * $r, 180, 90); $path.AddArc($ox + $m + $w - 2 * $r, $oy + $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($ox + $m + $w - 2 * $r, $oy + $m + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $path.AddArc($ox + $m, $oy + $m + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90); $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF $ox, $oy), (New-Object System.Drawing.PointF ($ox + $size), ($oy + $size)), (Col '#101822'), (Col '#070B10'))
    $g.FillPath($bg, $path)
    $edge = New-Object System.Drawing.Pen (Col '#2A394A'), ([Math]::Max(1, $k))
    $g.DrawPath($edge, $path)
    # soft glow behind the mark
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath; $gp.AddEllipse($ox + 16 * $k, $oy + 24 * $k, 208 * $k, 208 * $k)
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $gp
    $pg.CenterColor = Col '#19E08B' 46; $pg.SurroundColors = @((Col '#19E08B' 0))
    $g.FillPath($pg, $gp)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF ($ox + 30 * $k), ($oy + 40 * $k)), (New-Object System.Drawing.PointF ($ox + 200 * $k), ($oy + 216 * $k)), (Col '#19E08B'), (Col '#22D3EE'))
    $cx = 120.0; $cy = 128.0
    foreach ($spec in @(@(84, 255), @(56, 209), @(28, 163))) {
        $rad = $spec[0]
        $pen = New-Object System.Drawing.Pen ((New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF ($ox + 30 * $k), ($oy + 40 * $k)), (New-Object System.Drawing.PointF ($ox + 200 * $k), ($oy + 216 * $k)), (Col '#19E08B' $spec[1]), (Col '#22D3EE' $spec[1])))), (14 * $k)
        $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, $ox + ($cx - $rad) * $k, $oy + ($cy - $rad) * $k, 2 * $rad * $k, 2 * $rad * $k, 38, 284)
        $pen.Dispose()
    }
    $g.FillEllipse($grad, $ox + ($cx - 12) * $k, $oy + ($cy - 12) * $k, 24 * $k, 24 * $k)
    $bg.Dispose(); $edge.Dispose(); $grad.Dispose(); $pg.Dispose()
}

function RenderMark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    DrawMark $g 0 0 $size; $g.Dispose(); return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) { $b = RenderMark $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }
$ico = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $root 'src\app\app.ico'), $ico.ToArray())
$big = RenderMark 512; $big.Save((Join-Path $root 'docs\images\logo.png'), [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()

# horizontal lockup on a dark banner
$W = 1280; $H = 360
$bmp = New-Object System.Drawing.Bitmap $W, $H, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear((Col '#070B10'))
DrawMark $g 80 52 256
$f1 = New-Object System.Drawing.Font 'Segoe UI Semibold', 96, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$f2 = New-Object System.Drawing.Font 'Segoe UI', 30, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
$tb = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 380, 90), (New-Object System.Drawing.PointF 900, 200), (Col '#19E08B'), (Col '#22D3EE'))
$g.DrawString('Cici', $f1, (New-Object System.Drawing.SolidBrush (Col '#E7EEF5')), 372, 84)
$wCici = $g.MeasureString('Cici', $f1).Width - 24
$g.DrawString('PDA', $f1, $tb, 372 + $wCici, 84)
$g.DrawString('YOUR PHONE''S INTERNET, ON WINDOWS', $f2, (New-Object System.Drawing.SolidBrush (Col '#7B8CA1')), 380, 206)
$g.DrawString('CiciByte Technology, Inc.', $f2, (New-Object System.Drawing.SolidBrush (Col '#4A586A')), 380, 250)
$g.Dispose(); $bmp.Save((Join-Path $root 'docs\images\logo-horizontal.png'), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
Write-Host 'app.ico, logo.png and logo-horizontal.png written'
