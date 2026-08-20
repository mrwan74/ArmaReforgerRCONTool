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

---

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
| **Identifier**          | Player UID (GUID format)        | BattlEye GUID / IP Address        |
| **Ban Duration Format** | Duration in **Seconds**         | Duration in **Minutes**           |
| **Server Controls**     | `#restart`, `#shutdown`, `#say` | `loadBans`, `writeBans`, `admins` |
| **Player List Query**   | `#players`                      | `players`                         |
| **Ban List Query**      | `#ban list`                     | `bans`                            |

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

- **[BattleNET](https://github.com/marceldev89/BattleNET)** by **[@marceldev89](https://github.com/marceldev89)** – a C# library for the BattlEye protocol that enables the implementation of the BattlEye RCON network.
  LuminaUI by @j4587698 – a modern and fluent UI component and theming library for Avalonia.
- **[Avalonia UI](https://avaloniaui.net/)** is a cross-platform desktop XAML UI framework.
- CommunityToolkit.Mvvm – Source generators and an architecture for modern and fast MVVM.
  – The IP Geolocation engine from MaxMind GeoIP2.
- **Serilog** – a structured logging framework.
  – The Sentry .NET SDK provides application diagnostics and crash analytics.

---

## 📜 License

The project is covered by the **MIT License**; for more details see the [LICENSE](LICENSE) file.

---
