# ARMA Reforger RCON Tool (ARRT)

<p align="center">
  <img src="ReforgerRcon/Assets/app.ico" alt="ARRT Logo" width="128" height="128" />
</p>

<p align="center">
  <strong>A cross-platform administration tool for ARMA Reforger dedicated servers.</strong><br>
  Built with <strong>.NET 10</strong>, <strong>C# 13</strong>, <strong>Avalonia UI</strong>, and <strong>LuminaUI</strong>.
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 10" />
  <img src="https://img.shields.io/badge/AvaloniaUI-12.1.2-8E44AD?style=flat-square" alt="Avalonia UI" />
  <img src="https://img.shields.io/badge/LuminaUI-0.6.9-0EA5E9?style=flat-square" alt="LuminaUI" />
  <img src="https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-22C55E?style=flat-square" alt="Cross-Platform" />
  <img src="https://img.shields.io/badge/License-GNU AGPLv3-blue?style=flat-square" alt="MIT License" />
  <img src="https://img.shields.io/badge/Architecture-x64-F59E0B?style=flat-square" alt="Architecture" />
</p>

> [!NOTE]
> 🤖 **AI-Assisted Development Notice**  

---

> ### Latest Release: [`v0.9.0-alpha`](https://github.com/mrwan74/ArmaReforgerRCONTool/releases/tag/v0.9.0-alpha)
> **Released on:** Sept 9, 2026, 00:50 PM (UTC) 

---

## Table of Contents

- [Overview](#overview)
  - [Highlights](#highlights)
- [Screenshots](#screenshots)
- [For Users](#for-users)
  - [Download & Quick Start](#download--quick-start)
- [For Server Owners](#for-server-owners)
  - [Server-Side Configuration Guide](#server-side-configuration-guide)
    - [1. Reforger Built-in RCON Setup](#1-reforger-built-in-rcon-setup-json)
    - [2. BattlEye RCON Setup](#2-battleye-rcon-setup-beserver_x64cfg)
  - [Application Walkthrough](#application-walkthrough)
    - [Login & Profiles Management](#login--profiles-management)
    - [Main Dashboard & Live Monitoring](#main-dashboard--live-monitoring)
    - [Players Management & Moderation](#players-management--moderation)
    - [Bans Management](#bans-management)
    - [Historical Player Database (SQLite)](#historical-player-database-sqlite)
    - [Split, Detachable & Fullscreen RCON Console](#split-detachable--fullscreen-rcon-console)
    - [Settings & Geolocation Configuration](#settings--geolocation-configuration)
- [For Developers](#for-developers)
  - [Tech Stack & Architecture](#tech-stack--architecture)
  - [Repository & Solution Structure](#repository--solution-structure)
  - [Building from Source](#building-from-source)
    - [Prerequisites](#prerequisites)
    - [Clone & Compile](#clone--compile)
  - [Publishing Standalone / Single-File Builds](#publishing-standalone--single-file-builds)
    - [Windows (x64 Self-Contained Single-File)](#windows-x64-self-contained-single-file)
    - [Linux (x64 Self-Contained Single-File)](#linux-x64-self-contained-single-file)
- [Roadmap & TODO](#roadmap--todo)
- [Special Thanks & Credits](#special-thanks--credits)
- [License](#license)
- [SAST Tools](#sast-tools)

---

## Overview

**ARMA Reforger RCON Tool (ARRT)** is a desktop administration tool for arma reforger developed using C# and Avalonia UI. It supports both the native ARMA Reforger Built-in RCON and the BattlEye RCON protocols and offers real-time player moderation, batch actions, an interactive live console, offline player tracking, and MaxMind GeoIP2 geolocation lookups.

Unlike legacy tools designed for older arma titles, ARRT natively accommodates the modern reforger server landscape by supporting **both RCON protocols** ( BattlEye RCON and Reforger Built-in RCON ) 

### Highlights

- **Dual Protocol Switcher & Auto-Detection**: switching between BattlEye and Reforger RCON with live heuristic detection that warns you if your server is responding on a mismatched protocol.
- **Granular Player Moderation**: Instant Kick, Ban, and Quick Permanent Ban with rich duration presets (1h, 6h, 1d, 3d, 1w, 1m, custom breakdown, permanent).
- **Embedded SQLite Database (WAL Mode)**: Relational player tracking that logs historical IPs, ping averages, first seen / last seen timestamps, administrative comments, and automated **name change & alias detection**.
- **Offline Ban Capabilities**: Apply bans to players by GUID, Reforger UID, or IP even when they are disconnected.
- **Integrated MaxMind GeoLite2 Engine**: Automatic IP-to-Country flag resolution (using SkiaSharp-rasterized SVG vector flags) and precise city/subdivision formatting with local timezone calculations. Includes an in-app weekly database updater.
- **Live Action Toasts with Undo**: Every moderation command executed displays the raw command sent and features a single-click **Undo** button (e.g., reverting an accidental ban).
- **Split, Detachable & Fullscreen Console**: Real-time terminal with live categorized filtering (`ALL`, `RCON`, `System`), command history, auto-scroll toggle, and the ability to detach the console into a standalone secondary monitor window.
- **Multi-Select Batch Actions**: Batch kick, batch ban, and mass clipboard export across both live server lists and historical databases.
- **Desktop Alerts & Push Notifications**: Configurable custom audio alerts (`.mp3`/`.wav`), in-app toast overlays, and native OS desktop push notifications (via `Avalonia.Labs.Notifications`).
- **Portable & Crash-Resilient**: Runs portable with an isolated directory lock (`appdata/process.lock`) preventing concurrent instance file collisions. Features automated Windows Minidump generation (`.dmp`) and demystified stack traces.

---

## <a id="screenshots"></a> Screenshots

<img width="1696" height="937" alt="image" src="https://github.com/user-attachments/assets/e9eec2ab-ea01-4e13-aeae-55279d45816b" />
<img width="1696" height="937" alt="image" src="https://github.com/user-attachments/assets/a3b9d38c-5c6e-4bea-91bb-683bce3e94d3" />
<img width="1696" height="937" alt="image" src="https://github.com/user-attachments/assets/1711f6a0-4e7d-4f87-bde9-e7e53ee71037" />
<img width="1696" height="937" alt="image" src="https://github.com/user-attachments/assets/b4628ae3-b2e1-4da6-ae7e-a62623ddb80b" />


---

## For Users

### Download & Quick Start

1. Download the latest release binary for your operating system from the **[Releases](../../releases)** tab.
2. Extract the archive into a folder of your choice (e.g., `C:\Tools\ARRT\` or `~/tools/arrt/`).
3. Run `ReforgerRcon.exe` (or `./ReforgerRcon` on Linux/macOS).
4. Select your protocol, enter your server connection parameters, and click **Connect to Server**.

> [!TIP]
> **Demo Simulation Mode**: You can test all features of ARRT without an active server by clicking **Launch Demo Mode (Simulated Server)** on the login screen. This initializes a virtual server dataset with simulated players, geolocation, and ban records.

---

## For Server Owners

### Server-Side Configuration Guide

#### 1. Reforger Built-in RCON Setup (`JSON`)

Reforger Built-in RCON is native to the Enfusion Engine and is configured directly inside your server's `config.json` or `server.json` file.

```json
{
  "game": {
    "name": "My ARMA Reforger Dedicated Server",
    "password": "",
    "scenarioId": "{ECC61978923CA766}Missions/23_Campaign.conf",
    "playerCountLimit": 64,
    "autoReloadEngine": true
  },
  "rcon": {
    "enabled": true,
    "bindAddress": "0.0.0.0",
    "port": 19999,
    "password": "your_secure_password_here",
    "permission": "admin"
  }
}
```

- **Port Forwarding**: Ensure port `19999` (or your chosen port) is open for **UDP** inbound on your firewall/router.
- **Protocol in ARRT**: Select **Reforger Built-in RCON**.
Full list of Reforger commands:  
https://community.bistudio.com/wiki/Arma_Reforger:Server_Management
more info :
https://community.bistudio.com/wiki/Arma_Reforger:Server_Config#rcon

---

#### 2. BattlEye RCON Setup (`BEServer_x64.cfg`)

BattlEye RCON runs as part of the BattlEye Anti-Cheat server daemon. It requires a dedicated configuration file placed in your server's BattlEye directory.

1. Navigate to your server working directory: `[ServerRoot]/battleye/` 
2. Create or edit `BEServer_x64.cfg`:

```ini
// BEServer_x64.cfg
RConPort 20007
RConPassword your_secure_password_here
MaxPing 300
```

- **Port Forwarding**: Ensure port `20007` (or your custom `RConPort`) is open for **UDP** inbound.
- **Protocol in ARRT**: Select **BattlEye RCON**.
Full list of BattlEye commands:  
https://www.battleye.com/support/documentation/
more info :
https://community.bistudio.com/wiki/Arma_Reforger:Server_Hosting#BattlEye
---

### Application Walkthrough

#### Login & Profiles Management

- **Profile Persistence**: Save unlimited server connection profiles with distinct passwords, ports, and protocols.
- **Inline Renaming**: Rename profiles with quick keyboard shortcuts (<kbd>Enter</kbd> to save, <kbd>Esc</kbd> to cancel).
- **Auto-Connect**: Toggle auto-connection on startup on a per-profile basis.

#### Main Dashboard & Live Monitoring

- **Real-Time Heartbeat & Ping Smoothing**: Displays smoothed network round-trip latency and a pulsating indicator synced to server packet reception.
- **Live Search Bar**: Real-time filtering across active players, bans, and historical databases supporting fuzzy matching across names, UIDs, GUIDs, IPs, and custom comments.

#### Players Management & Moderation

- **Status Indicators**:
  - `👁` **Watchlist**: Player is flagged for administrator surveillance.
  - `⚠️` **Alias Alert**: Player has changed their nickname compared to previous sessions stored in SQLite.
- **Player Details Dialog**: Displays comprehensive player metadata (UID, GUID, IP:Port, city/region/country, local timezone, ping, comments, and aliases). Every field can be clicked to copy.
- **Multi-Select Toolbar**: Click **Multi-Select** or check individual players to activate the batch moderation bar: **Kick Selected**, **Ban Selected**, or **Copy All Info**.

#### Bans Management

- **BattlEye Ban Operations**: Dedicated toolbar buttons to trigger `loadBans` (reload `bans.txt`) and `writeBans` (persist active bans and purge expired bans from disk).
- **Ban Duration Converter**: Automatically transforms user selections (Hours, Days, Weeks, Months, or granular Year/Month/Day/Hour/Minute/Second breakdown) into exact second or minute metrics required by the underlying protocol.

#### Historical Player Database (SQLite)

- Automatically captures every player who joins the server.
- Stores historical IP addresses, ping metrics, first seen/last seen dates, and full nickname history.
- Perform **Offline Bans** on players who have left the server by selecting their historical record.

#### Split, Detachable & Fullscreen RCON Console

- **Category Tabs**: Filter stream traffic between `ALL`, `RCON` (inbound/outbound command traffic), and `System` messages.
- **Detachable Window**: Click **Detach** to pop the console into an independent, multi-monitor window. Re-docking is seamless via the **Reattach** button or simply closing the detached window.

#### Settings & Geolocation Configuration

- **MaxMind GeoLite2 Live Updates**: Download fresh weekly `.mmdb` database updates directly from MaxMind by providing your free Account ID and License Key.
- **Default Column Sorting**: Configure custom sorting rules and directions independently for the Players, Bans, and Database tabs.
- **Window Glass / Backdrop**: Toggle between OS-level acrylic/Mica glass blur effects and solid high-contrast backgrounds.

---

## 💻 For Developers

### Tech Stack & Architecture

- **Runtime**: [.NET 10.0](https://dotnet.microsoft.com/) (`net10.0`)
- **Language**: C# 13 (Latest Roslyn Analyzers & strict Nullable reference types enabled)
- **UI Framework**: [Avalonia UI 12.1.2](https://avaloniaui.net/)
- **Theme & Controls**: [LuminaUI 0.6.9](https://github.com/lumina-ui) & `LuminaUI.DataGrid`
- **MVVM Pattern**: [CommunityToolkit.Mvvm 8.4.2](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)
- **Embedded Database**: [Microsoft.Data.Sqlite 10.0.11](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) with `SQLitePCLRaw.bundle_e_sqlite3`
- **Geolocation**: [MaxMind.GeoIP2 6.1.0](https://github.com/maxmind/GeoIP2-dotnet) & [TimeZoneConverter 7.2.0](https://github.com/mattjohnsonpint/TimeZoneConverter)
- **Vector Rendering**: [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia) with [SkiaSharp](https://github.com/mono/SkiaSharp)
- **Audio Subsystem**: [NetCoreAudio 2.0.1](https://github.com/yan-f/NetCoreAudio)
- **Native Notifications**: `Avalonia.Labs.Notifications 12.0.2`
- **Logging & Diagnostics**: [Serilog 4.4.0](https://serilog.net/) (Async File Sinks, Demystifier, Thread/Process enrichers), [Sentry 6.10.0](https://sentry.io/), and [Aptabase.Avalonia](https://aptabase.com/)

---

### Repository & Solution Structure

```
ReforgerRcon/
│
├── Aptabase.Avalonia/              # In-memory & persistent local analytics pipeline
│   ├── AptabaseClient.cs           # Bounded Channel-based background event batch processor
│   ├── AptabasePersistentClient.cs # Disk-backed event/error queue (DotNext.Threading)
│   ├── AptabaseCrashReporter.cs    # AppDomain, TaskScheduler & Dispatcher safety hooks
│   └── SystemInfo.cs               # Hardware and OS metadata extractor
│
├── ReforgerRcon/
│   ├── Assets/                     # Application icons, flags (SVG), and audio files
│   │   ├── audio/                  # Custom audio alert assets (.mp3 / .wav)
│   │   └── flags/                  # ISO 3166-1 alpha-2 SVG country vector flags
│   │
│   ├── BattleNET/                  # BattlEye UDP Wire Protocol Engine
│   │   ├── BattlEyeClient.cs       # Async UDP socket client, keep-alives, multi-packet assembler
│   │   ├── BattlEyeCommand.cs      # BattlEye command enumerations
│   │   └── CRC32.cs                # IEEE 802.3 CRC32 checksum engine for BE packets
│   │
│   ├── Converters/                 # Avalonia XAML IValueConverter implementations
│   ├── Models/                     # Core domain entities, DTOs, and configuration models
│   │   ├── AppSettings.cs          # Local app preferences & JSON storage
│   │   ├── DatabasePlayerModel.cs  # SQLite entity for historical player records
│   │   ├── PlayerModel.cs          # Active player session model with GeoIP data
│   │   └── ServerProfile.cs        # Server connection profile model
│   │
│   ├── Services/                   # Application & background infrastructure services
│   │   ├── Parsers/
│   │   │   ├── BattlEyeResponseParser.cs   # Regex & heuristic parser for BattlEye responses
│   │   │   └── ReforgerResponseParser.cs  # Parser for Enfusion Reforger responses & anomalies
│   │   ├── AppLogger.cs            # High-throughput Serilog dispatcher & PII redactor
│   │   ├── CrashReportService.cs   # Windows dbghelp.dll MiniDump generator & diagnostic snapshot
│   │   ├── GeoIpService.cs         # MaxMind GeoLite2-City/Country reader & live update extractor
│   │   ├── HardwareIdentityService.cs # Persistent anonymized device fingerprint generator
│   │   ├── PlayerDatabaseStorageService.cs # SQLite WAL database repository & alias tracker
│   │   ├── RconService.cs          # Master IRconService orchestrator (BattlEye & Reforger)
│   │   └── SoundNotificationService.cs # Cross-platform audio player & Win32 MessageBeep fallback
│   │
│   ├── ViewModels/                 # CommunityToolkit MVVM ViewModels
│   │   ├── DashboardViewModel.cs   # Main application viewmodel & timer loop orchestrator
│   │   ├── LoginViewModel.cs       # Connection form & profile management
│   │   ├── PlayersViewModel.cs     # Active players tab viewmodel & batch moderation
│   │   ├── BansViewModel.cs        # Active bans tab viewmodel
│   │   ├── DatabaseViewModel.cs    # Historical database tab viewmodel
│   │   └── ConsoleViewModel.cs     # Real-time interactive terminal viewmodel
│   │
│   └── Views/                      # Avalonia XAML Views & Dialog Overlays
│       ├── Dialogs/                # Modal dialogs (Ban, Kick, Details, Admins, GeoIP Update)
│       ├── Tabs/                   # Tab views (Players, Bans, Database, Settings, Console)
│       ├── ConsoleWindow.axaml     # Detached standalone console window
│       └── MainWindow.axaml        # Primary application shell window
│
├── ReforgerRcon.slnx            
└── .editorconfig                   # Code analysis & Roslyn style rules
```

---

### Building from Source

#### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (SDK version `10.0.100` or newer).
- [Git](https://git-scm.com/).

#### Clone & Compile

```bash
# 1. Clone the repository
git clone https://github.com/your-username/ReforgerRcon.git
cd ReforgerRcon

# 2. Restore NuGet dependencies
dotnet restore ReforgerRcon.slnx

# 3. Build the solution in Release mode
dotnet build ReforgerRcon.slnx -c Release

# 4. Launch the application
dotnet run --project ReforgerRcon/ReforgerRcon.csproj -c Release
```

---

### Publishing Standalone / Single-File Builds

To create a self-contained, single-file executable that does not require the .NET 10 Runtime to be installed on the host machine:

#### Windows (x64 Self-Contained Single-File)

```bash
dotnet publish ReforgerRcon/ReforgerRcon.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o ./publish/win-x64
```

#### Linux (x64 Self-Contained Single-File)

```bash
dotnet publish ReforgerRcon/ReforgerRcon.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -o ./publish/linux-x64
```

---

## <a id="roadmap--todo"></a>🗺️ Roadmap & TODO

- [x] **Audio Alerts & Push Notifications:** Implement sound chimes and external notification channels (desktop alerts) for user-configured triggers     
- [ ] **RCON Macro & Scheduled Commands**: Automated recurring server announcements and scheduled `#restart` timers.
- [ ] **Discord Webhook Integration**: Real-time moderation and player join/leave dispatch to Discord channels.
- [ ] **Auto App Updater:** Check for new GitHub releases directly within the app and provide a one-click download & update.
- [ ] **Custom Commands & Plugin System:** Register custom RCON commands and mod actions as clickable toolbar buttons (for Reforger).
- [ ] **Global Player Database Synchronization:** A cloud syncing feature enabling administrators to synchronise the player database list with a master community database.
- [ ] **Session and Playtime Tracking:** Record total play hours, session durations, and historical join/leave event tracking.
- [ ] **Multi-Server Tabbed Dashboard**: Manage multiple active server connections simultaneously within a single window.

---

## <a id="special-thanks--credits"></a>💖 Special Thanks & Credits

Special thanks to the authors of the open-source projects and libraries used in this tool:

* **[DaRT (DayZ / ArmA RCon Tool)](https://github.com/DomiStyle/DaRT)** by **[@DomiStyle](https://github.com/DomiStyle)** – The legendary DayZ and ARMA administration tool that served as the primary design and functional inspiration for ARRT.
* **[BattleNET](https://github.com/marceldev89/BattleNET)** by **[@marceldev89](https://github.com/marceldev89)** – C# BattlEye protocol client library powering the BattlEye RCON network implementation (refactored and modernized for .NET 10).
* **[LuminaUI](https://github.com/j4587698/LuminaUI)** by **[@j4587698](https://github.com/j4587698)** – UI component and theming library for Avalonia.
* **[Avalonia UI](https://github.com/AvaloniaUI/Avalonia)** by **[@AvaloniaUI](https://github.com/AvaloniaUI)** – Avalonia is a cross-platform UI framework for .NET providing a flexible styling system and supporting a wide range of operating systems.
* **[MaxMind GeoIP2](https://github.com/maxmind/GeoIP2-dotnet)** by **[@maxmind](https://github.com/maxmind)** – IP Geolocation engine.
* **[MaxMind GeoIP Databases](https://dev.maxmind.com/geoip/docs/databases/city-and-country/)** by **[@maxmind](https://www.maxmind.com/en/home)** Geolocation Databases GeoIP Country And City Databases 

* **[Serilog](https://github.com/serilog/serilog)** by **[@serilog](https://github.com/serilog)** – Structured logging framework.
* **[Sentry .NET SDK](https://github.com/getsentry/sentry-dotnet)** by **[@getsentry](https://github.com/getsentry)** – Application diagnostics and crash analytics.
* **[flags-icons](https://github.com/lipis/flag-icons)** by **[@lipis](https://github.com/lipis)** – Country flags
* **[Aptabase](https://aptabase.com)** by **[@aptabase](https://github.com/aptabase)** — Open Source, Privacy-First Analytics for Mobile, Desktop, and Web Apps.
* The `Aptabase.Avalonia` client included in this repository is directly adapted and ported from Aptabase's official [.NET MAUI SDK](https://github.com/aptabase/aptabase-maui) to support Avalonia. All original SDK architecture and code remain the intellectual property of the Aptabase team.

among many others,for a full list of packages uses check the .csproj files 

---

## <a id="license"></a>📄 License

This project is licensed under the **GNU Affero General Public License v3.0**. See the [LICENSE](LICENSE) file for details.

---

## <a id="sast-tools"></a>SAST Tools

[PVS-Studio](https://pvs-studio.com/en/pvs-studio/?utm_source=website&utm_medium=github&utm_campaign=open_source) - static code analyzer for Enterprise (C, C++, C#, Go, and Java) and Web (JS and TS) development.
