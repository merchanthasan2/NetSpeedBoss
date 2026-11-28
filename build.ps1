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

# ---- application icon (regenerate with tools\make-icon.ps1) ----
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) { & (Join-Path $root 'tools\make-icon.ps1') }

$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$v4 = "$Version.0"
@"
using System.Reflection;
[assembly: AssemblyTitle("NetSpeed Boss")]
[assembly: AssemblyProduct("NetSpeed Boss")]
[assembly: AssemblyCompany("Quantum Step")]
[assembly: AssemblyDescription("Real-time network speed meter, per-app traffic and data usage monitor")]
[assembly: AssemblyCopyright("Copyright (c) Quantum Step. MIT License.")]
[assembly: AssemblyVersion("$v4")]
[assembly: AssemblyFileVersion("$v4")]
[assembly: AssemblyInformationalVersion("$Version")]
"@ | Set-Content (Join-Path $obj 'AssemblyInfo.cs') -Encoding utf8

$files = @(Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }) + (Join-Path $obj 'AssemblyInfo.cs')
$resArgs = @("/resource:$ico,NetSpeedBoss.app.ico")
$logoDir = Join-Path $root 'assets\logos'
if (-not (Test-Path $logoDir)) {
    $sync = Join-Path $root 'tools\sync-logos.ps1'
    if (Test-Path $sync) { & $sync }
}
if (Test-Path $logoDir) {
    foreach ($png in Get-ChildItem $logoDir -Filter *.png) {
        $resArgs += "/resource:$($png.FullName),NetSpeedBoss.Logos.$($png.Name)"
    }
}
$exe = Join-Path $out 'NetSpeedBoss.exe'
$wpf = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF"
& $csc /nologo /target:winexe /platform:anycpu /optimize+ /out:"$exe" `
    /win32icon:"$ico" /win32manifest:"$(Join-Path $src 'app.manifest')" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    /r:"$wpf\UIAutomationClient.dll" /r:"$wpf\UIAutomationTypes.dll" /r:"$wpf\WindowsBase.dll" `
    $resArgs $files
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
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        Get-ChildItem $dist -File | ForEach-Object {
            $fs = [System.IO.File]::OpenRead($_.FullName)
            try { $h = ([BitConverter]::ToString($sha.ComputeHash($fs))).Replace('-', '') } finally { $fs.Dispose() }
            "{0}  {1}" -f $h, $_.Name
        }
    } finally { $sha.Dispose() }
}
