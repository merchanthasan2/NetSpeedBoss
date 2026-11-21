# Renders the NetSpeed Boss icon (speedometer gauge) at every Windows icon size and writes:
#   src\app.ico (PNG-compressed multi-size icon)   docs\images\icon.png (256 px)   docs\images\icon-512.png
# Usage: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function New-Color([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }

function New-IconBitmap([int]$size) {
    # draw at 4x and downsample for clean anti-aliasing
    $ss = 4; $S = [single]($size * $ss)
    $small = $size -le 24
    $big = New-Object System.Drawing.Bitmap ([int]$S), ([int]$S)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # --- tile: rounded square with deep navy gradient
    $pad = if ($small) { 0 } else { $S * 0.04 }
    $w = $S - 2 * $pad; $rad = $w * 0.24; $d = $rad * 2
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($pad, $pad, $d, $d, 180, 90); $tile.AddArc($pad + $w - $d, $pad, $d, $d, 270, 90)
    $tile.AddArc($pad + $w - $d, $pad + $w - $d, $d, $d, 0, 90); $tile.AddArc($pad, $pad + $w - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $S, $S), (New-Color 255 34 52 92), (New-Color 255 9 13 26)
    $g.FillPath($bg, $tile)
    if (-not $small) {
        # soft top-left glow + hairline rim
        $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glow.AddEllipse(-$S * 0.25, -$S * 0.35, $S * 1.1, $S * 0.95)
        $pgb = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
        $pgb.CenterColor = New-Color 46 120 170 255; $pgb.SurroundColors = @((New-Color 0 120 170 255))
        $g.SetClip($tile); $g.FillPath($pgb, $glow); $g.ResetClip()
        $rim = New-Object System.Drawing.Pen (New-Color 40 255 255 255), ($S * 0.008)
        $g.DrawPath($rim, $tile)
    }

    # --- gauge
    $cx = $S * 0.5; $cy = $S * 0.56
    $r = $S * $(if ($small) { 0.33 } else { 0.30 })
    $sw = $S * $(if ($small) { 0.17 } else { 0.105 })
    $rect = New-Object System.Drawing.RectangleF ($cx - $r), ($cy - $r), (2 * $r), (2 * $r)
    $start = 150; $sweep = 240; $value = 172
    $track = New-Object System.Drawing.Pen (New-Color 46 255 255 255), $sw
    $track.StartCap = 'Round'; $track.EndCap = 'Round'
    $g.DrawArc($track, $rect, $start, $sweep)

    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF ($cx - $r - $sw), 0), (New-Object System.Drawing.PointF ($cx + $r + $sw), 0), (New-Color 255 70 215 120), (New-Color 255 255 165 60)
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @((New-Color 255 52 211 153), (New-Color 255 190 230 90), (New-Color 255 255 160 50))
    $blend.Positions = @([single]0, [single]0.55, [single]1)
    $grad.InterpolationColors = $blend
    $arc = New-Object System.Drawing.Pen $grad, $sw
    $arc.StartCap = 'Round'; $arc.EndCap = 'Round'
    $g.DrawArc($arc, $rect, $start, $value)

    if ($size -ge 48) {
        # tick marks inside the arc
        $tickPen = New-Object System.Drawing.Pen (New-Color 90 255 255 255), ($S * 0.014)
        $tickPen.StartCap = 'Round'; $tickPen.EndCap = 'Round'
        for ($i = 0; $i -le 8; $i++) {
            $a = ($start + $sweep * $i / 8) * [Math]::PI / 180
            $r1 = $r - $sw * 0.95; $r2 = $r - $sw * 1.45
            $g.DrawLine($tickPen, [single]($cx + $r1 * [Math]::Cos($a)), [single]($cy + $r1 * [Math]::Sin($a)), [single]($cx + $r2 * [Math]::Cos($a)), [single]($cy + $r2 * [Math]::Sin($a)))
        }
    }

    # --- needle (tapered) pointing at the end of the value arc
    $na = ($start + $value) * [Math]::PI / 180
    $len = $r * $(if ($small) { 0.92 } else { 0.86 })
    $nw = $S * $(if ($small) { 0.075 } else { 0.05 })
    $tip = New-Object System.Drawing.PointF ([single]($cx + $len * [Math]::Cos($na))), ([single]($cy + $len * [Math]::Sin($na)))
    $pa = $na + [Math]::PI / 2
    $b1 = New-Object System.Drawing.PointF ([single]($cx + $nw * [Math]::Cos($pa))), ([single]($cy + $nw * [Math]::Sin($pa)))
    $b2 = New-Object System.Drawing.PointF ([single]($cx - $nw * [Math]::Cos($pa))), ([single]($cy - $nw * [Math]::Sin($pa)))
    $tail = New-Object System.Drawing.PointF ([single]($cx - $nw * 1.2 * [Math]::Cos($na))), ([single]($cy - $nw * 1.2 * [Math]::Sin($na)))
    if (-not $small) {
        $shadow = New-Object System.Drawing.SolidBrush (New-Color 90 0 0 0)
        $off = $S * 0.012
        $g.FillPolygon($shadow, [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($tip.X + $off), ($tip.Y + $off)), (New-Object System.Drawing.PointF ($b1.X + $off), ($b1.Y + $off)),
            (New-Object System.Drawing.PointF ($tail.X + $off), ($tail.Y + $off)), (New-Object System.Drawing.PointF ($b2.X + $off), ($b2.Y + $off))))
    }
    $white = New-Object System.Drawing.SolidBrush (New-Color 255 248 250 252)
    $g.FillPolygon($white, [System.Drawing.PointF[]]@($tip, $b1, $tail, $b2))
    $hub = $S * $(if ($small) { 0.085 } else { 0.07 })
    $g.FillEllipse($white, $cx - $hub, $cy - $hub, 2 * $hub, 2 * $hub)
    $dot = $hub * 0.45
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (New-Color 255 22 32 58)), $cx - $dot, $cy - $dot, 2 * $dot, 2 * $dot)

    if ($size -ge 64) {
        # small down / up chevrons under the hub: download & upload
        $cy2 = $cy + $r * 0.62; $cw = $S * 0.05
        $gp = New-Object System.Drawing.Pen (New-Color 255 70 215 120), ($S * 0.028); $gp.StartCap = 'Round'; $gp.EndCap = 'Round'; $gp.LineJoin = 'Round'
        $op = New-Object System.Drawing.Pen (New-Color 255 255 165 60), ($S * 0.028); $op.StartCap = 'Round'; $op.EndCap = 'Round'; $op.LineJoin = 'Round'
        $lx = $cx - $S * 0.075; $rx = $cx + $S * 0.075
        $g.DrawLines($gp, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($lx - $cw), ($cy2 - $cw * 0.5)), (New-Object System.Drawing.PointF $lx, ($cy2 + $cw * 0.5)), (New-Object System.Drawing.PointF ($lx + $cw), ($cy2 - $cw * 0.5))))
        $g.DrawLines($op, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($rx - $cw), ($cy2 + $cw * 0.5)), (New-Object System.Drawing.PointF $rx, ($cy2 - $cw * 0.5)), (New-Object System.Drawing.PointF ($rx + $cw), ($cy2 + $cw * 0.5))))
    }
    $g.Dispose()

    $out = New-Object System.Drawing.Bitmap $size, $size
    $g2 = [System.Drawing.Graphics]::FromImage($out)
    $g2.InterpolationMode = 'HighQualityBicubic'; $g2.PixelOffsetMode = 'HighQuality'; $g2.SmoothingMode = 'AntiAlias'
    $g2.DrawImage($big, 0, 0, $size, $size)
    $g2.Dispose(); $big.Dispose()
    return $out
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , ($ms.ToArray())
    if ($s -eq 256) { $bmp.Save((Join-Path $root 'docs\images\icon.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
}
$big = New-IconBitmap 512; $big.Save((Join-Path $root 'docs\images\icon-512.png'), [System.Drawing.Imaging.ImageFormat]::Png); $big.Dispose()

$ico = Join-Path $root 'src\app.ico'
$fs = [System.IO.File]::Create($ico)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close()
Write-Host "Wrote $ico, docs\images\icon.png, docs\images\icon-512.png"
