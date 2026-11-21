# Builds NetSpeedBoss.exe using the C# compiler that ships with Windows (.NET Framework 4.x).
# No SDK or Visual Studio needed.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.2.3] [-Installer]
param([string]$Version = '1.0.0', [switch]$Installer)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'src'
$out  = Join-Path $root 'bin'
$csc  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "csc.exe not found at $csc" }
New-Item -ItemType Directory -Force $out | Out-Null

# ---- generate the application icon (multi-size PNG .ico) ----
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) {
    Add-Type -AssemblyName System.Drawing
    $sizes = 16, 24, 32, 48, 64, 128, 256
    $pngs = @()
    foreach ($s in $sizes) {
        $bmp = New-Object System.Drawing.Bitmap $s, $s
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $r = [single]($s * 0.22)
        $path = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = [single]($s - 1)
        $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90); $path.AddArc($d - $r * 2, 0, $r * 2, $r * 2, 270, 90)
        $path.AddArc($d - $r * 2, $d - $r * 2, $r * 2, $r * 2, 0, 90); $path.AddArc(0, $d - $r * 2, $r * 2, $r * 2, 90, 90)
        $path.CloseFigure()
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(255, 30, 36, 48)), ([System.Drawing.Color]::FromArgb(255, 14, 16, 22))
        $g.FillPath($bg, $path)
        $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 215, 120))
        $orange = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 165, 60))
        $w = [single]$s
        # down arrow
        $g.FillRectangle($green, $w * 0.24, $w * 0.18, $w * 0.13, $w * 0.38)
        $g.FillPolygon($green, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($w * 0.12), ($w * 0.52)), (New-Object System.Drawing.PointF ($w * 0.49), ($w * 0.52)), (New-Object System.Drawing.PointF ($w * 0.305), ($w * 0.84))))
        # up arrow
        $g.FillRectangle($orange, $w * 0.63, $w * 0.44, $w * 0.13, $w * 0.38)
        $g.FillPolygon($orange, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF ($w * 0.51), ($w * 0.48)), (New-Object System.Drawing.PointF ($w * 0.88), ($w * 0.48)), (New-Object System.Drawing.PointF ($w * 0.695), ($w * 0.16))))
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += ,($ms.ToArray())
        $bmp.Dispose()
    }
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
    Write-Host "Generated $ico"
}

$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$v4 = "$Version.0"
@"
using System.Reflection;
[assembly: AssemblyTitle("NetSpeed Boss")]
[assembly: AssemblyProduct("NetSpeed Boss")]
[assembly: AssemblyDescription("Real-time network speed meter, per-app traffic and data usage monitor")]
[assembly: AssemblyCopyright("Copyright (c) NetSpeed Boss contributors. MIT License.")]
[assembly: AssemblyVersion("$v4")]
[assembly: AssemblyFileVersion("$v4")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content (Join-Path $obj 'AssemblyInfo.cs') -Encoding utf8

$files = @(Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }) + (Join-Path $obj 'AssemblyInfo.cs')
$exe = Join-Path $out 'NetSpeedBoss.exe'
& $csc /nologo /target:winexe /platform:x64 /optimize+ /out:"$exe" `
    /win32icon:"$ico" /win32manifest:"$(Join-Path $src 'app.manifest')" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll `
    $files
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $exe ($Version)"

if ($Installer) {
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
              "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { $c = Get-Command iscc -ErrorAction SilentlyContinue; if ($c) { $iscc = $c.Source } }
    if (-not $iscc) { throw "Inno Setup 6 (ISCC.exe) not found. Install with: winget install JRSoftware.InnoSetup" }
    $dist = Join-Path $root 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null
    & $iscc /Q "/DAppVersion=$Version" "/O$dist" (Join-Path $root 'installer\NetSpeedBoss.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
    # portable zip as well
    $zip = Join-Path $dist "NetSpeedBoss-$Version-portable.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Compress-Archive -Path $exe, (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') -DestinationPath $zip
    Get-ChildItem $dist | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name }
}
