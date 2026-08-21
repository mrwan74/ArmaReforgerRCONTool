# ARMA REFORGER RCON TOOL (ARRT)

[![AI Assisted](https://img.shields.io/badge/Development-AI--Assisted-8A2BE2?style=flat&logo=openai&logoColor=white)](#)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat&logo=dotnet)](https://dotnet.microsoft.com/)
[![Avalonia UI](https://img.shields.io/badge/UI-Avalonia_12-8A2BE2?style=flat&logo=avalonia)](https://avaloniaui.net/)
[![Release](https://img.shields.io/badge/Version-v0.6.2-blue?style=flat)](https://github.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-0078D6?style=flat&logo=windows)](https://github.com/)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

> [!NOTE]
> 🤖 **AI-Assisted Development Notice**  

---

The ARMA REFORGER RCON TOOL (ARRT) is a desktop administration tool developed using C# and Avalonia UI. It supports both the native ARMA Reforger Built-in RCON and the BattlEye RCON protocols and offers real-time player moderation, batch actions, an interactive live console, offline player tracking, and MaxMind GeoIP2 geolocation lookups.

This readme is a work in progress..
---
## 📥 Download

Get the latest release of **ARMA Reforger RCON Tool (ARRT)**:

[](https://github.com/mrwan74/ArmaReforgerRCONTool/releases/tag/v0.6.2-alpha)

👉 Download the ARMA Reforger RCON Tool v0.6.2-alpha (.zip) from https://github.com/mrwan74/ArmaReforgerRCONTool/releases/tag/v0.6.2-alpha

### 🚀 Quick Start

1. Obtain the `.zip` file from the **[v0.6.2-alpha Release Page](https://github.com/mrwan74/ArmaReforgerRCONTool/releases/tag/v0.6.2-alpha)**.
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
## Key Features

- **Dual Protocol Support:** You can smoothly switch between the built in RCON provided natively by ARMA Reforger and the standard BattlEye RCON.
- **Smart Ban Calculations:**
  - **Reforger:** It is able to convert any duration breakdown (given in years, days, hours, or minutes) into an exact number of seconds
  - BattlEye: It automatically converts durations into minutes
- **Live Moderation and Multi-Select:** You can carry out batch kicks, batch bans, toggle the watchlist, add player notes or comments, and carry out quick permanent bans all with a single-click header selection.
- **Historical Player Database:** This system automatically logs player history, including the IP addresses, country, and any changes to aliases.
- **MaxMind GeoLite2 Geolocation:** An offline geolocation database resolution (covering Country, Region, City, and ISP) that includes optional automated weekly background updates.
- **Detachable and Fullscreen Terminal:** An interactive RCON console that features multi-channel filtering (ALL, RCON, System), command history, the ability to copy, and support for multi-window detachment.
- **100% Portable (`appdata/`):** The various connection profiles, database entries, column layouts, window positions, and logs are all stored within the local `appdata/` directory
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

full list of reforger commands : 
https://community.bistudio.com/wiki/Arma_Reforger:Server_Management

full list of battleye commands : 
https://www.battleye.com/support/documentation/

---

## 🚀 Getting Started

### Prerequisites

- Windows 10 / Windows 11 (x64)
  The [.NET 10.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (or obtain the Self-Contained version, which has no prerequisites)

### Running the App

1. Get the latest release .zip file from the **[Releases](../../releases)** page.
2. You can place the folder anywhere (for example, on the desktop, on a USB drive, or in the server tools directory).
3. Run **`ReforgerRcon.exe`**.

---

## 🌍 Optional: MaxMind GeoIP Configuration

ARRT can function offline by using MaxMind databases that are bundled with it. If you want automatic weekly updates to the database directly from MaxMind then:
Get a free account at MaxMind.com.
In ARRT go to the Settings tab.
3. Fill in your Account ID and License Key, then click Update Databases.
The credentials are stored locally in a file called appdata\settings.json (this file is ignored by Git).

---

## 📁 Portable Directory Structure

When running, the app maintains the following structure in its folder:

```text
ReforgerRcon/
│
# Main Application Executable   ReforgerRcon.exe
│
└── appdata/                         # All persistent data stays here
    ├              
    settings.json  # Contains the user's preferences and the MaxMind keys
    profiles.json stores saved server connection profiles.
    ├── player_database.json           # Contains the historical player records and notes
    grid_columns.json # A cache of the column layout and widths
    window_state.json            # Saves window position and size
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
# 1. Copy the repository
git clone https://github.com/yourusername/ReforgerRcon.git
cd ReforgerRcon

# 2. Restore NuGet dependencies

Restore NuGet dependencies to make sure your project has all the packages it needs. Open a terminal or command prompt in your project folder. Run the following command:
```

dotnet restore

```
This command downloads and installs missing packages listed in your project file. If you use Visual Studio, you can also restore packages by right-clicking the solution in Solution Explorer and choosing "Restore NuGet Packages."
dotnet restore

# 3. Build using the Release configuration
dotnet build -c Release

# Publish a single-folder portable ReadyToRun binary
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o ./publish
```

---

## 💖 Special Thanks & Credits

Special thanks to the authors of the open-source projects and libraries used in this tool:

* **[BattleNET](https://github.com/marceldev89/BattleNET)** by **[@marceldev89](https://github.com/marceldev89)** – C# BattlEye protocol client library powering the BattlEye RCON network implementation.
* **[LuminaUI](https://github.com/j4587698/LuminaUI)** by **[@j4587698](https://github.com/j4587698)** – UI component and theming library for Avalonia.
* **[Avalonia UI](https://github.com/AvaloniaUI/Avalonia)** by **[@AvaloniaUI](https://github.com/AvaloniaUI)** – Avalonia is a cross-platform UI framework for .NET providing a flexible styling system and supporting a wide range of Operating Systems such as Windows, Linux, macOS and with experimental support for Android, iOS and WebAssembly.
* **[MaxMind GeoIP2](https://github.com/maxmind/GeoIP2-dotnet)** by **[@maxmind](https://github.com/maxmind)** – IP Geolocation engine.
* **[Serilog](https://github.com/serilog/serilog)** by **[@serilog](https://github.com/serilog)** – Structured logging framework.
* **[Sentry .NET SDK](https://github.com/getsentry/sentry-dotnet)** by **[@getsentry](https://github.com/getsentry)** – Application diagnostics and crash analytics.

 among many others 

---

## 📜 License

The project is covered by the **MIT License**; for more details see the [LICENSE](LICENSE) file.

---

## ⚠️ Known Issues

* Comments from Administrators require a manual refresh: the notes or comments added to players are only displayed in the grid after you manually click **Refresh**.
* Autoscroll on the console is inactive: the terminal's autoscroll option does not currently cause the scroll viewer to jump to the bottom when a new log entry arrives.
* **Incorrect hint regarding the #say command:** The console placeholder indicates #say, but this is not a valid command in vanilla ARMA Reforger RCON.
* Creation of a Forger ban (change in syntax since v1.8 to #ban create):
  Since the release of ARMA Reforger 1.8, the `#ban create` command has stopped accepting the long Reforger Identity ID (UID) for online players and now requires the session **`Player#`** obtained from the `#players` command (for example, `#ban create <playerId> <duration> <reason>`).
  Note that the command #ban remove <identityId> still requires the Reforger Identity ID.

---

## 🗺️ Roadmap / TODO

- [ ] **Audio Alerts & Push Notifications:** Implement sound chimes and external notification channels ( desktop alerts) for user-configured triggers (player joins/leaves, watchlisted player joins/leaves, server disconnection...etc).
- [ ] **Auto App Updater:** Checks for new GitHub releases within the app and provides a one-click download & update.
- [ ] **Custom Commands & Plugin System:**  register custom RCON commands and mod actions as clickable toolbar buttons.( for reforger)
- [ ] **Synchronisation of the Global Player Database:** A cloud syncing feature which enables administrators to synchronise the player database list with a master database(for all communities ).
- [ ] **Session and Playtime Tracking:** Record the total number of play hours, the duration of each session, and the historical data regarding players' joining and leavings and other stats.
---
