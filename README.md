# ARMA REFORGER RCON TOOL (ARRT)

[![AI Assisted](https://img.shields.io/badge/Development-AI--Assisted-8A2BE2?style=flat&logo=openai&logoColor=white)](#)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/UI-Avalonia_12-8A2BE2?style=flat&logo=avalonia)](https://avaloniaui.net/)
[![Release](https://img.shields.io/badge/Version-v0.6.2-blue?style=flat)](https://github.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?style=flat&logo=windows)](https://github.com/)
[![License: AGPL v3](https://img.shields.io/badge/License-AGPL_v3-blue.svg)](LICENSE)

> [!NOTE]
> 🤖 **AI-Assisted Development Notice**  

---

The Arma Reforger RCON Tool (ARRT) is a desktop administration tool for arma reforger developed using C# and Avalonia UI. It supports both the native ARMA Reforger Built-in RCON and the BattlEye RCON protocols and offers real-time player moderation, batch actions, an interactive live console, offline player tracking, and MaxMind GeoIP2 geolocation lookups.
<img width="1696" height="937" alt="ReforgerRcon_vS2DqXgTRv" src="https://github.com/user-attachments/assets/84a3deca-fd85-4175-8a37-0090701cf3ce" />
<img width="1696" height="937" alt="ReforgerRcon_WIyVzBxVg5" src="https://github.com/user-attachments/assets/ec0c7836-29f2-44e4-808b-c1346483052b" />
<img width="1696" height="937" alt="ReforgerRcon_FfKvJeitPZ" src="https://github.com/user-attachments/assets/e042d1ef-0e4f-4783-84c6-0f0e86104fdf" />
<img width="1696" height="937" alt="ReforgerRcon_3Q42eui38q" src="https://github.com/user-attachments/assets/ba7cd4dc-2644-4788-8172-40ec33d80ca7" />


This readme is a work in progress..

---

## 📥 Download

Get the latest release of **ARMA Reforger RCON Tool (ARRT)**:

👉 Download the ARMA Reforger RCON Tool v0.6.2-alpha (.zip) from the **[Releases Page](https://github.com/mrwan74/ArmaReforgerRCONTool/releases)**

### 🚀 Quick Start

1. Obtain the `.zip` file from the **[Releases Page](https://github.com/mrwan74/ArmaReforgerRCONTool/releases)**.
2. Copy the files anywhere on your PC (for example, to the Desktop, a USB drive, or the server tools folder).
3. Double-click **`ReforgerRcon.exe`** to start. You do not need to install anything or have administrator rights.

> [!CAUTION]
> ### ⚠️ SECURITY WARNING
> **Log files (`appdata/logs/`) and crash reports/dumps (`appdata/crash_reports/`) contain runtime diagnostic traces and IP addresses.**  
> * **NEVER** share raw log files, crash reports (`.txt`), or memory dumps (`.dmp`) publicly.
> * **ONLY** share logs or crash dumps with a trusted person for debugging purposes.
> * **ALWAYS CHANGE YOUR SERVER RCON PASSWORD FIRST** before sharing any log or crash dump file with anyone for support!
> * **Credentials Stored in Plaintext:**
>    * `appdata/profiles.json` (RCON server passwords) and `appdata/settings.json` (MaxMind license keys) are saved as plain JSON files with no OS-level protection for now. Anyone with local file access to your computer or USB drive can read them directly.

---

## Key Features

- **Dual Protocol Support:** You can smoothly switch between the built in RCON provided natively by ARMA Reforger and the standard BattlEye RCON.
- **Smart Ban Calculations:**
  - **Reforger:** It is able to convert any duration breakdown (given in years, days, hours, or minutes) into an exact number of seconds.
  - **BattlEye:** It automatically converts durations into minutes.
- **Live Moderation and Multi-Select:** You can carry out batch kicks, batch bans, toggle the watchlist, add player notes or comments, and carry out quick permanent bans all with a single-click header selection.
- **Historical Player Database:** This system automatically logs player history, including the IP addresses, country, and any changes to aliases.
- **MaxMind GeoLite2 Geolocation:** An offline geolocation database resolution (covering Country, Region, City, and ISP) that includes optional automated weekly background updates.
- **Detachable and Fullscreen Terminal:** An interactive RCON console that features multi-channel filtering (ALL, RCON, System), command history, the ability to copy, and support for multi-window detachment.
- **100% Portable (`appdata/`):** The various connection profiles, database entries, column layouts, window positions, and logs are all stored within the local `appdata/` directory.
- **Offline Demo Mode:** There is a built-in simulation mode which enables you to test and investigate all the features without having an active connection to a server.

---

## 🖥️ Supported Protocols Comparison

| Feature                 | Reforger Built-In RCON          | BattlEye RCON                     |
| ----------------------- | ------------------------------- | --------------------------------- |
| **Default Port**        | `19999` (UDP)                   | `20007` (UDP) (just an example)   |
| **Identifier**          | Player UID                      | BattlEye GUID / IP Address        |
| **Ban Duration Format** | Duration in **Seconds**         | Duration in **Minutes**           |
| **Server Controls**     | `#restart`, `#shutdown`         | `loadBans`, `writeBans`, `admins` |
| **Player List Query**   | `#players`                      | `players`                         |
| **Ban List Query**      | `#ban list`                     | `bans`                            |

Full list of Reforger commands:  
https://community.bistudio.com/wiki/Arma_Reforger:Server_Management

Full list of BattlEye commands:  
https://www.battleye.com/support/documentation/

---

## 🚀 Getting Started

### Prerequisites

- Windows 10 / Windows 11 (x64)
- The [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or obtain the Self-Contained version, which has no prerequisites)

### Running the App

1. Get the latest release `.zip` file from the **[Releases](../../releases)** page.
2. You can place the folder anywhere (for example, on the desktop, on a USB drive, or in the server tools directory).
3. Run **`ReforgerRcon.exe`**.

---

## 🌍 Optional: MaxMind GeoIP Configuration

ARRT can function offline by using MaxMind databases that are bundled with it. If you want automatic weekly updates to the database directly from MaxMind:
1. Get a free account at [MaxMind.com](https://www.maxmind.com).
2. In ARRT, go to the **Settings** tab.
3. Fill in your **Account ID** and **License Key**, then click **Update Databases**.

*The credentials are stored locally in `appdata/settings.json` (this file is ignored by Git).*

---

## 📁 Portable Directory Structure

When running, the app maintains the following structure in its folder:

```text
ReforgerRcon/
│
├── ReforgerRcon.exe                 # Main Application Executable
│
└── appdata/                         # All persistent data stays here
    ├── settings.json                # Contains user preferences and MaxMind keys
    ├── profiles.json                # Stores saved server connection profiles
    ├── player_database.json         # Contains historical player records and notes
    ├── grid_columns.json            # A cache of the column layout and widths
    ├── window_state.json            # Saves window position and size
    │
    ├── geoip/                       # Downloaded GeoLite2 binary databases
    │   ├── GeoLite2-City.mmdb
    │   └── GeoLite2-Country.mmdb
    │
    ├── logs/                        # Application logs
    │   └── reforger_rcon_YYYYMMDD.log
    │
    └── crash_reports/               # Local crash text reports & memory dumps
        └── crash_YYYYMMDD_HHMMSS_XXXXXX.txt
```

---

## 🛠️ Building from Source

```bash
# 1. Clone the repository
git clone https://github.com/mrwan74/ArmaReforgerRCONTool.git
cd ArmaReforgerRCONTool

# 2. Restore NuGet dependencies
dotnet restore

# 3. Build using the Release configuration
dotnet build -c Release

# 4. Publish a single-folder portable ReadyToRun binary
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o ./publish
```

---

## 💖 Special Thanks & Credits

Special thanks to the authors of the open-source projects and libraries used in this tool:

* **[BattleNET](https://github.com/marceldev89/BattleNET)** by **[@marceldev89](https://github.com/marceldev89)** – C# BattlEye protocol client library powering the BattlEye RCON network implementation.
* **[LuminaUI](https://github.com/j4587698/LuminaUI)** by **[@j4587698](https://github.com/j4587698)** – UI component and theming library for Avalonia.
* **[Avalonia UI](https://github.com/AvaloniaUI/Avalonia)** by **[@AvaloniaUI](https://github.com/AvaloniaUI)** – Avalonia is a cross-platform UI framework for .NET providing a flexible styling system and supporting a wide range of operating systems.
* **[MaxMind GeoIP2](https://github.com/maxmind/GeoIP2-dotnet)** by **[@maxmind](https://github.com/maxmind)** – IP Geolocation engine.
* **[Serilog](https://github.com/serilog/serilog)** by **[@serilog](https://github.com/serilog)** – Structured logging framework.
* **[Sentry .NET SDK](https://github.com/getsentry/sentry-dotnet)** by **[@getsentry](https://github.com/getsentry)** – Application diagnostics and crash analytics.

...among many others.

---

## 📜 License

This project is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**. For more details, see the [LICENSE](LICENSE) file.

---

## ⚠️ Known Issues

* **Comments Require Manual Refresh:** Notes or comments added to players are only displayed in the grid after manually clicking **Refresh**.
* **Autoscroll on Console:** Autoscroll on the console is currently non-functional.
* **Incorrect Placeholder Hint:** The console placeholder indicates `#say`, but this is not a valid command in Reforger RCON.
* **Reforger Ban Syntax (v1.8+):** Since ARMA Reforger 1.8, `#ban create` requires the session **`Player#`** obtained from `#players` (e.g., `#ban create <playerId> <duration> <reason>`) rather than the long Reforger Identity ID. Note that `#ban remove <identityId>` still uses the Identity ID.

---

## 🗺️ Roadmap / TODO

- [ ] **Audio Alerts & Push Notifications:** Implement sound chimes and external notification channels (desktop alerts) for user-configured triggers (player joins/leaves, watchlisted player joins/leaves, server disconnection, etc.).
- [ ] **Auto App Updater:** Check for new GitHub releases directly within the app and provide a one-click download & update.
- [ ] **Custom Commands & Plugin System:** Register custom RCON commands and mod actions as clickable toolbar buttons (for Reforger).
- [ ] **Global Player Database Synchronization:** A cloud syncing feature enabling administrators to synchronise the player database list with a master community database.
- [ ] **Session and Playtime Tracking:** Record total play hours, session durations, and historical join/leave event tracking.
