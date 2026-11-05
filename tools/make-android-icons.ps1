# Renders the Android companion's launcher icons, in-app logo and notification icon from the CiciPDA mark
# (same geometry as docs\images\logo.svg) into src\agent\android\res. Re-run after changing the logo.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$res = Join-Path $root 'src\agent\android\res'
function Col($h, $a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($h); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }

function P($x, $y) { New-Object System.Drawing.PointF ([float]$x), ([float]$y) }
function Grad($a, $b, $c1, $c2) { New-Object System.Drawing.Drawing2D.LinearGradientBrush $a, $b, $c1, $c2 }

function RoundRect([float]$x, [float]$y, [float]$w, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90); $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $w - 2 * $r, 2 * $r, 2 * $r, 0, 90); $p.AddArc($x, $y + $w - 2 * $r, 2 * $r, 2 * $r, 90, 90); $p.CloseFigure(); return $p
}

# full-colour mark: dark rounded square, glow, three gradient arcs, solid node
function Mark([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0
    $path = RoundRect (8 * $k) (8 * $k) (240 * $k) (56 * $k)
    $bg = Grad (P 0 0) (P $size $size) (Col '#101822') (Col '#070B10')
    $g.FillPath($bg, $path); $g.DrawPath((New-Object System.Drawing.Pen (Col '#2A394A'), ([Math]::Max(1, $k))), $path)
    $gp = New-Object System.Drawing.Drawing2D.GraphicsPath; $gp.AddEllipse(16 * $k, 24 * $k, 208 * $k, 208 * $k)
    $pg = New-Object System.Drawing.Drawing2D.PathGradientBrush $gp; $pg.CenterColor = Col '#19E08B' 46; $pg.SurroundColors = @((Col '#19E08B' 0)); $g.FillPath($pg, $gp)
    foreach ($spec in @(@(84, 255), @(56, 209), @(28, 163))) {
        $rad = $spec[0]
        $br = Grad (P (30 * $k) (40 * $k)) (P (200 * $k) (216 * $k)) (Col '#19E08B' $spec[1]) (Col '#22D3EE' $spec[1])
        $pen = New-Object System.Drawing.Pen $br, (14 * $k); $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, (120 - $rad) * $k, (128 - $rad) * $k, 2 * $rad * $k, 2 * $rad * $k, 38, 284)
    }
    $nb = Grad (P (100 * $k) (110 * $k)) (P (140 * $k) (146 * $k)) (Col '#19E08B') (Col '#22D3EE')
    $g.FillEllipse($nb, 108 * $k, 116 * $k, 24 * $k, 24 * $k); $g.Dispose(); return $bmp
}

# notification icon: white, transparent background (Android tints it)
function StatIcon([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0
    foreach ($rad in 100, 62, 24) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (22 * $k); $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
        $g.DrawArc($pen, (118 - $rad) * $k, (128 - $rad) * $k, 2 * $rad * $k, 2 * $rad * $k, 38, 284)
    }
    $g.FillEllipse([System.Drawing.Brushes]::White, (118 - 16) * $k, (128 - 16) * $k, 32 * $k, 32 * $k); $g.Dispose(); return $bmp
}

function Save($bmp, $path) { New-Item -ItemType Directory (Split-Path $path) -Force | Out-Null; $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

foreach ($d in @(@('mdpi', 48, 24), @('hdpi', 72, 36), @('xhdpi', 96, 48), @('xxhdpi', 144, 72), @('xxxhdpi', 192, 96))) {
    Save (Mark $d[1]) (Join-Path $res "mipmap-$($d[0])\ic_launcher.png")
    Save (StatIcon $d[2]) (Join-Path $res "drawable-$($d[0])\ic_stat.png")
}
Save (Mark 384) (Join-Path $res 'drawable-nodpi\logo.png')
Write-Host 'Android icons written to' $res
