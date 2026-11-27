# Builds x64 + arm64 MSIX packages and bundles them into a Store-ready .msixbundle.
# Usage: msix\make-bundle.ps1 -Version 1.2.0 [-Exe bin\NetSpeedBoss.exe] [-OutDir dist\msix]
param(
    [Parameter(Mandatory)] [string]$Version,
    [string]$Exe = 'bin\NetSpeedBoss.exe',
    [string]$OutDir = 'dist\msix',
    [string]$IdentityName = 'QuantumStep.NetSpeedBoss',
    [string]$Publisher = 'CN=56509922-8909-48B3-8A7E-09F363321E6A',
    [string]$PublisherDisplayName = 'Quantum Step',
    [string]$MakeAppx = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not [System.IO.Path]::IsPathRooted($Exe)) { $Exe = Join-Path $root $Exe }
if (-not [System.IO.Path]::IsPathRooted($OutDir)) { $OutDir = Join-Path $root $OutDir }
if (-not (Test-Path $Exe)) { throw "Executable not found: $Exe" }

if (-not $MakeAppx) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $found = Get-ChildItem $kits -Filter 'makeappx.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { $MakeAppx = $found.FullName }
    }
}
if (-not $MakeAppx -or -not (Test-Path $MakeAppx)) {
    throw "makeappx.exe not found. Pass -MakeAppx or install Windows 10/11 SDK."
}

$iconSrc = Join-Path $root 'docs\images\icon-512.png'
if (-not (Test-Path $iconSrc)) { throw "Icon not found: $iconSrc" }

$v4 = "$Version.0"
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("NetSpeedBoss-msix-" + [guid]::NewGuid().ToString('N'))
$pkgPool = Join-Path $work 'pool'
New-Item -ItemType Directory -Force $pkgPool, $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
$srcImg = [System.Drawing.Image]::FromFile($iconSrc)
function Save-Tile($w, $h, $outPath) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $sz = [Math]::Min($w, $h)
    $g.DrawImage($srcImg, [int](($w - $sz) / 2), [int](($h - $sz) / 2), $sz, $sz)
    $g.Dispose()
    $bmp.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

try {
    foreach ($arch in @('x64', 'arm64')) {
        $stage = Join-Path $work "stage-$arch"
        $assets = Join-Path $stage 'Assets'
        New-Item -ItemType Directory -Force $assets | Out-Null
        Copy-Item $Exe (Join-Path $stage 'NetSpeedBoss.exe') -Force
        Save-Tile 50 50 (Join-Path $assets 'StoreLogo.png')
        Save-Tile 44 44 (Join-Path $assets 'Square44x44Logo.png')
        Save-Tile 150 150 (Join-Path $assets 'Square150x150Logo.png')
        Save-Tile 310 150 (Join-Path $assets 'Wide310x150Logo.png')

        @"
<?xml version="1.0" encoding="utf-8"?>
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap rescap">
  <Identity
    Name="$IdentityName"
    Publisher="$Publisher"
    Version="$v4"
    ProcessorArchitecture="$arch" />
  <Properties>
    <DisplayName>NetSpeed Boss</DisplayName>
    <PublisherDisplayName>$PublisherDisplayName</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Applications>
    <Application Id="NetSpeedBoss"
      Executable="NetSpeedBoss.exe"
      EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="NetSpeed Boss"
        Description="Live network upload and download speed on your Windows taskbar, with a live graph, per-app traffic, data usage history, and system &amp; ISP info."
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png">
        <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
      </uap:VisualElements>
    </Application>
  </Applications>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
</Package>
"@ | Set-Content (Join-Path $stage 'AppxManifest.xml') -Encoding utf8

        $msixPath = Join-Path $pkgPool "NetSpeedBoss_${v4}_${arch}.msix"
        & $MakeAppx pack /d "$stage" /p "$msixPath" /o
        if ($LASTEXITCODE -ne 0) { throw "makeappx pack ($arch) failed" }
    }

    $bundle = Join-Path $OutDir "NetSpeedBoss-$Version.msixbundle"
    & $MakeAppx bundle /d "$pkgPool" /p "$bundle" /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed" }
    Write-Host "MSIX bundle written to $bundle"
} finally {
    $srcImg.Dispose()
    Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue
}
