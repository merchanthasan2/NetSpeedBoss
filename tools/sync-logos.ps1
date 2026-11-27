# Downloads official SVG brand marks (Simple Icons / thesvg / gilbarbara) and rasterizes
# crisp transparent white PNGs into assets\logos\ for build-time embedding via csc.exe /resource:.
# Uses Windows built-in .NET Framework WPF (PresentationCore) - zero external tools needed.
param([switch]$Force)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = Join-Path $root 'assets\logos'
New-Item -ItemType Directory -Force $outDir | Out-Null

$sources = [ordered]@{
    acer       = 'https://cdn.simpleicons.org/acer/ffffff'
    amd        = 'https://cdn.simpleicons.org/amd/ffffff'
    apple      = 'https://cdn.simpleicons.org/apple/ffffff'
    asus       = 'https://cdn.simpleicons.org/asus/ffffff'
    broadcom   = 'https://cdn.simpleicons.org/broadcom/ffffff'
    corsair    = 'https://cdn.simpleicons.org/corsair/ffffff'
    dell       = 'https://cdn.simpleicons.org/dell/ffffff'
    hp         = 'https://cdn.simpleicons.org/hp/ffffff'
    intel      = 'https://cdn.simpleicons.org/intel/ffffff'
    lenovo     = 'https://cdn.simpleicons.org/lenovo/ffffff'
    mediatek   = 'https://cdn.simpleicons.org/mediatek/ffffff'
    microsoft  = 'https://raw.githubusercontent.com/glincker/thesvg/main/public/icons/microsoft/default.svg'
    micron     = 'https://raw.githubusercontent.com/gilbarbara/logos/main/logos/micron.svg'
    msi        = 'https://cdn.simpleicons.org/msi/ffffff'
    nvidia     = 'https://cdn.simpleicons.org/nvidia/ffffff'
    samsung    = 'https://cdn.simpleicons.org/samsung/ffffff'
    sandisk    = 'https://raw.githubusercontent.com/glincker/thesvg/main/public/icons/sandisk/default.svg'
    seagate    = 'https://cdn.simpleicons.org/seagate/ffffff'
    snapdragon = 'https://cdn.simpleicons.org/qualcomm/ffffff'
    toshiba    = 'https://cdn.simpleicons.org/toshiba/ffffff'
    wd         = 'https://raw.githubusercontent.com/glincker/thesvg/main/public/icons/western-digital/default.svg'
    windows    = 'https://raw.githubusercontent.com/glincker/thesvg/main/public/icons/windows/default.svg'
}

function Render-SvgToPng([string]$svg, [string]$outPath) {
    $matches = [regex]::Matches($svg, '<path\b([^>]*)>')
    if ($matches.Count -eq 0) { return $false }

    $group = New-Object System.Windows.Media.GeometryGroup
    $group.FillRule = if ($svg -match 'evenodd') { [System.Windows.Media.FillRule]::EvenOdd } else { [System.Windows.Media.FillRule]::Nonzero }

    foreach ($m in $matches) {
        $attrs = $m.Groups[1].Value
        if ($attrs -match '\bd="([^"]+)"') {
            $d = $Matches[1]
            # ignore bounding-box clip/bg rectangles with fill="none"
            if ($attrs -match 'fill="none"' -and $attrs -notmatch 'stroke=') { continue }
            $geo = [System.Windows.Media.Geometry]::Parse($d)
            $group.Children.Add($geo)
        }
    }
    if ($group.Children.Count -eq 0) { return $false }

    $b = $group.Bounds
    if ($b.IsEmpty -or $b.Width -le 0 -or $b.Height -le 0) { return $false }

    $targetH = 64.0
    $maxW = 220.0
    $pad = 2.0
    $scale = [Math]::Min(($targetH - $pad * 2) / $b.Height, ($maxW - $pad * 2) / $b.Width)
    $pxW = [Math]::Max(16, [int][Math]::Ceiling($b.Width * $scale + $pad * 2))
    $pxH = [Math]::Max(16, [int][Math]::Ceiling($b.Height * $scale + $pad * 2))

    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $tx = ($pxW - $b.Width * $scale) / 2.0 - $b.X * $scale
    $ty = ($pxH - $b.Height * $scale) / 2.0 - $b.Y * $scale
    $tg = New-Object System.Windows.Media.TransformGroup
    $tg.Children.Add((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
    $tg.Children.Add((New-Object System.Windows.Media.TranslateTransform($tx, $ty)))
    $dc.PushTransform($tg)
    $dc.DrawGeometry([System.Windows.Media.Brushes]::White, $null, $group)
    $dc.Pop()
    $dc.Close()

    $rtb = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($pxW, $pxH, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $rtb.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rtb))
    $fs = [System.IO.File]::Open($outPath, [System.IO.FileMode]::Create)
    try { $enc.Save($fs) } finally { $fs.Close() }
    return $true
}

$ok = 0
foreach ($key in $sources.Keys) {
    $png = Join-Path $outDir "$key.png"
    if (-not $Force -and (Test-Path $png) -and (Get-Item $png).Length -gt 0) { $ok++; continue }
    $url = $sources[$key]
    try {
        $svg = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 10).Content
        if (Render-SvgToPng $svg $png) {
            $ok++
            Write-Host "Synced $key.png"
        } else {
            Write-Warning "Could not parse paths for $key ($url)"
        }
    } catch {
        Write-Warning "Failed $key ($url): $($_.Exception.Message)"
    }
}
Write-Host "Logos ready: $ok / $($sources.Count) in $outDir"
