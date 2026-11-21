# Generates the three winget manifest files (schema 1.6.0) for a release.
# Usage: winget\make-manifests.ps1 -Version 1.0.0 -Installer dist\NetSpeedBoss-1.0.0-Setup.exe -OutDir dist\winget
param(
    [Parameter(Mandatory)] [string]$Version,
    [Parameter(Mandatory)] [string]$Installer,
    [string]$OutDir = 'dist\winget',
    [string]$Id = 'Net.NetSpeedBoss',
    [string]$Repo = 'merchanthasan2/NetSpeedBoss'
)
$ErrorActionPreference = 'Stop'
$sha = (Get-FileHash $Installer -Algorithm SHA256).Hash
$name = Split-Path $Installer -Leaf
$url = "https://github.com/$Repo/releases/download/v$Version/$name"
$date = (Get-Date).ToString('yyyy-MM-dd')
New-Item -ItemType Directory -Force $OutDir | Out-Null

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: $Id
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $OutDir "$Id.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: $Id
PackageVersion: $Version
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: user
InstallModes:
- interactive
- silent
- silentWithProgress
UpgradeBehavior: install
ProductCode: '{7C1E4A52-9B3D-4E8F-A6D1-2F5B8C9E0A41}_is1'
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $sha
ManifestType: installer
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $OutDir "$Id.installer.yaml") -Encoding utf8

@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: $Id
PackageVersion: $Version
PackageLocale: en-US
Publisher: NetSpeed Boss
PublisherUrl: https://github.com/$($Repo.Split('/')[0])
PublisherSupportUrl: https://github.com/$Repo/issues
PackageName: NetSpeed Boss
PackageUrl: https://github.com/$Repo
License: MIT
LicenseUrl: https://github.com/$Repo/blob/main/LICENSE
ShortDescription: Live network upload and download speed on your Windows taskbar.
Description: |-
  NetSpeed Boss shows your real-time download and upload speed on the Windows taskbar, next to the clock.
  It also includes a dashboard with a live speed graph, a per-app view of which programs are using the
  network and where they connect, daily and monthly data usage history with an optional data cap alert,
  and a floating always-on-top meter. Lightweight, portable settings, no administrator rights required.
Moniker: netspeedboss
Tags:
- bandwidth
- data-usage
- internet
- monitor
- network
- network-monitor
- speed
- speedmeter
- taskbar
- traffic
ReleaseNotesUrl: https://github.com/$Repo/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@ | Set-Content (Join-Path $OutDir "$Id.locale.en-US.yaml") -Encoding utf8

Write-Host "winget manifests written to $OutDir (sha256 $sha)"
