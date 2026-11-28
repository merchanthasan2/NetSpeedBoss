<p align="center">
  <img src="docs/images/icon.png" width="96" alt="NetSpeed Boss icon">
</p>

<h1 align="center">NetSpeed Boss</h1>

<p align="center">
  <b>Your live internet speed, right on the Windows taskbar.</b><br>
  See which apps use your connection and how much data you use. Free, open source, tiny.
</p>

<p align="center">
  <a href="https://github.com/merchanthasan2/NetSpeedBoss/releases/latest/download/NetSpeedBoss-Setup.exe"><b>⬇ Download for Windows</b></a>
  &nbsp;·&nbsp;
  <a href="https://merchanthasan2.github.io/NetSpeedBoss/">Website</a>
  &nbsp;·&nbsp;
  <a href="https://github.com/merchanthasan2/NetSpeedBoss/releases">All releases</a>
</p>

<p align="center">
  <img src="docs/images/dashboard-live.png" width="760" alt="NetSpeed Boss dashboard with live speed graph, Speedtest, and multi-WAN adapters">
</p>
<p align="center">
  <img src="docs/images/gaming-hud.png" width="375" alt="NetSpeed Boss Gaming HUD tab with RAG status colors and transparent background preview">
  &nbsp;
  <img src="docs/images/system-info.png" width="375" alt="NetSpeed Boss System tab with CPU, GPU, Memory cards and brand logos">
</p>

---

## Install

**Option 1: winget** (recommended)
```powershell
winget install Net.NetSpeedBoss
```

**Option 2: installer.** Download [`NetSpeedBoss-Setup.exe`](https://github.com/merchanthasan2/NetSpeedBoss/releases/latest/download/NetSpeedBoss-Setup.exe) and run it.

**Option 3: portable.** Download the `portable.zip` from [Releases](https://github.com/merchanthasan2/NetSpeedBoss/releases), unzip it anywhere, and run `NetSpeedBoss.exe`.

Works on Windows 10 and 11 (x64 &amp; ARM64). **No administrator rights needed.**

> **"Windows protected your PC"?** New apps that aren't code-signed yet trigger SmartScreen. Click **More info → Run anyway**.
> Every release is built automatically from this source code by [GitHub Actions](.github/workflows/release.yml).

## Get started in 3 steps

| | |
|---|---|
| **1. Install** | Use one of the options above. NetSpeed Boss starts right away. |
| **2. Look at your taskbar** | Your live **▼ download** and **▲ upload** speed sits next to the clock. Drag it left or right to move it. |
| **3. Explore** | **Double-click** the meter to open the dashboard. **Right-click** it for every option (bold text, units, colors...). |

## Features

- **Taskbar speed meter.** Auto-positions cleanly in empty taskbar space (including beside the Windows 11 Weather bar) or drags anywhere freely, follows light and dark mode, and hides for full-screen games.
- **Make it yours.** Bold text, any font and size, custom colors, bits (`Mbps`/`Kbps`, default) or bytes (`MB/s`/`KB/s`), and a refresh rate from 0.5 to 5 s.
- **Live speed graph & lag spikes.** The last 1–10 minutes, with a hover read-out, rolling ping/jitter/packet-loss pill, spike markers, and peak/session totals.
- **Top downloading apps.** The five busiest apps sit right under the graph. Tick one to draw its own line on the graph.
- **Apps, Firewall & Listening Ports.** Live per-app speeds, remote host resolution, first-time app network alerts, 1-click Windows Firewall block/unblock, and a **Listening ports** toggle with 1-click process termination.
- **Unified Network tab.** Multi-stream Speedtest + saved history, **Bufferbloat & Lag Doctor** (Grade `A+`–`F` + hop RTT diagnosis), **DNS Benchmark**, **Wireless (Wi-Fi)** card + **Wi-Fi Channel Congestion Analyzer**, **ISP Uptime & Micro-Outage Logger** (`outages.csv` + Plan SLA %), and an **Angry-IP-style LAN/ARP Scanner** with Trusted Device Watchdog & Wake-on-LAN.
- **Data usage & Per-SSID caps.** Click any daily row to see top apps for that exact day, export date ranges to Excel-ready CSVs (`Documents\NetSpeed Boss\`), and configure global or per-Wi-Fi network monthly data caps + background bandwidth-hog alerts.
- **Dual-WAN load balancing.** Automatically sums multiple gateway adapters, shows live per-adapter speeds and % traffic share, and alerts on WAN failover drops/recoveries.
- **Gaming HUD v2.** Background-only transparent overlay (Vertical Card or Compact Horizontal Top-Strip) with 1% Low FPS, DXGI/D3D9 FPS, NVIDIA NVML & AMD ADL sensors, VRAM, RAG thresholds, global hotkeys (`Ctrl+Shift+H` HUD, `+L` click-through lock, `+M` mini meter), and `game-sessions.csv` summaries.
- **Displays tab.** Native Win32 DDC/CI hardware **Brightness & Contrast** sliders, **Refresh Rate (Hz)** switcher, and **Input Source** (`HDMI`/`DisplayPort`/`USB-C`) selector across external and laptop screens.
- **System, About & Quick Profiles.** Hardware/OS/ISP cards with vendor logos, **Standard / Gaming / Travel** tray presets, diagnostics export, and an **About** tab with 1-click Store/GitHub/CLI links.
- **Private and lightweight.** Zero third-party dependencies, no account, no telemetry, no ads. Published by Quantum Step.

### Per-app speeds (optional admin mode)
Windows only lets administrators read live per-app network traffic. Right-click the meter and choose
**Restart as administrator** to see exact download and upload speeds per app, covering TCP and UDP (games, calls, streaming).
To make that permanent without a prompt at every sign-in, tick **Settings → Start with administrator rights at sign-in**.
Without admin, the Apps tab still lists which apps are connected and where to.

## FAQ

<details><summary><b>Will it slow down my PC or internet?</b></summary>

No. It reads the traffic counters Windows already keeps, about once a second. It never sends or downloads anything itself.
</details>

<details><summary><b>Where are my settings and history stored?</b></summary>

Only on your PC. Usage logs (daily, hourly and per-app CSV files) are in `Documents\NetSpeed Boss\`.
Settings are in `%APPDATA%\NetSpeedBoss\settings.ini`, with an `error.log` there only if something goes wrong.
</details>

<details><summary><b>How do I update or uninstall?</b></summary>

`winget upgrade Net.NetSpeedBoss` or run a newer installer.
Uninstall from **Settings → Apps → NetSpeed Boss**, or `winget uninstall Net.NetSpeedBoss`.
</details>

<details><summary><b>The meter disappeared</b></summary>

Right-click the tray icon (the ▼▲ arrows by the clock) and make sure **Show speed on taskbar** is ticked. Use **Reset positions** if you dragged it somewhere odd.
</details>

## Build from source

No SDK or Visual Studio needed. It uses the C# compiler that ships with Windows.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1                       # -> bin\NetSpeedBoss.exe
powershell -ExecutionPolicy Bypass -File build.ps1 -Version 1.4.0 -Installer   # + dist\ installer & zip (needs Inno Setup 6)
```

| File | What it does |
|---|---|
| `src/TaskbarMeter.cs` | Taskbar read-out with Windows 11 Weather-bar empty-space auto-placement & free dragging |
| `src/FloatingMeter.cs` | Floating always-on-top meter |
| `src/GameHud.cs`, `src/GameTelemetry.cs` | Gaming HUD v2 (Card & Strip), 1% Low FPS, RAG colors, DXGI/D3D9, NVML & AMD ADL telemetry |
| `src/NetMonitor.cs`, `src/SpeedTester.cs` | Multi-WAN adapter counters & multi-stream HTTPS Speedtest + history |
| `src/LanScanner.cs`, `src/LagDoctor.cs` | Angry-IP-style LAN/ARP scanner, Wake-on-LAN, Bufferbloat doctor, Outage SLA tracker & Guard |
| `src/DisplayControl.cs` | DDC/CI & WMI multi-monitor brightness, contrast, refresh Hz, and input switcher |
| `src/ProcessMonitor.cs` | Per-app traffic (ETW `Microsoft-Windows-Kernel-Network`) & TCP/UDP listening port table |
| `src/UsageStore.cs` | Daily & per-Wi-Fi profile usage history and caps |
| `src/TrayApp.cs` | Tray icon, menu, global hotkeys, quick profiles, alerts, update loop |
| `src/Dashboard.cs`, `src/DashboardTabs.cs` | Dashboard UI (Live, Apps, Network, Usage, System, Gaming HUD, Displays, Settings, About) |
| `tools/sync-logos.ps1` | Syncs official SVG brand logos to embedded PNGs in `assets/logos/` |
| `installer/NetSpeedBoss.iss` | Inno Setup installer (per-user, no UAC) |
| `winget/make-manifests.ps1` | Generates the winget manifest for a release |
| `docs/` | The project website (GitHub Pages) |

## Releasing (maintainers)

1. Push a tag: `git tag v1.0.1 && git push origin v1.0.1` (or run the **Release** workflow manually).
2. GitHub Actions builds the app, installer and portable zip, publishes the Release, and generates the winget manifests.
3. If the `WINGET_TOKEN` repository secret is set (a classic GitHub token with `public_repo` scope), the new version is submitted to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) automatically.

## License

[MIT](LICENSE). Free to use, modify and share.
