using Avalonia;
using Avalonia.Styling;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using ReforgerRcon.Services;

namespace ReforgerRcon.Models;

public class AppSettings
{
    private static readonly JsonSerializerOptions CachedJsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsDirectory = AppPaths.AppDataDirectory;
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string TempSettingsPath = Path.Combine(SettingsDirectory, "settings.json.tmp");
    private static readonly Lock SyncLock = new();

    private static volatile AppSettings? _cachedSettings;
    private static Timer? _saveDebounceTimer;

    public string InstallationId { get; set; } = string.Empty;

    public bool AudioAlerts { get; set; }
    public bool ToastNotifications { get; set; } = true;
    public bool PushNotifications { get; set; }

    public bool AlertOnJoin { get; set; }
    public bool AlertOnLeave { get; set; }
    public bool AlertOnWatchlistJoin { get; set; }
    public bool AlertOnWatchlistLeave { get; set; }

    public int RefreshIntervalSeconds { get; set; } = 15;
    public bool AutoRefreshBans { get; set; } = true;
    public bool RunInBackground { get; set; } = true;

    public bool EnableWindowGlass { get; set; }
    public string ThemeMode { get; set; } = "System";
    public bool SendAnonymousCrashReports { get; set; }
    public bool HasPromptedTelemetry { get; set; }

    public string MaxMindAccountId { get; set; } = string.Empty;
    public string MaxMindLicenseKey { get; set; } = string.Empty;
    public bool AutoUpdateGeoIpOnStartup { get; set; } = true;

    public string PlayersSortBy { get; set; } = "Default";
    public bool PlayersSortAscending { get; set; } = true;

    public string BansSortBy { get; set; } = "Default";
    public bool BansSortAscending { get; set; } = true;

    public string DatabaseSortBy { get; set; } = "Default";
    public bool DatabaseSortAscending { get; set; } = true;

    public string ReforgerBanFetchMode { get; set; } = "All Pages";
    public int ReforgerBanCustomPageLimit { get; set; } = 3;

    public static void ApplyThemeMode(string mode)
    {
        if (Application.Current == null) return;

        Application.Current.RequestedThemeVariant = mode switch
        {
            "Dark" or "Dark Mode" => ThemeVariant.Dark,
            "Light" or "Light Mode" => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    public static string GetOrCreateInstallationId()
    {
        var settings = LoadFromDisk();
        if (string.IsNullOrWhiteSpace(settings.InstallationId))
        {
            settings.InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            SaveToDisk(settings);
        }
        return settings.InstallationId;
    }

    public static bool IsCrashReportingEnabled()
    {
        var cached = _cachedSettings;
        return cached?.SendAnonymousCrashReports ?? LoadFromDisk().SendAnonymousCrashReports;
    }

    public static AppSettings LoadFromDisk()
    {
        var cached = _cachedSettings;
        if (cached != null)
        {
            return cached;
        }

        lock (SyncLock)
        {
            if (_cachedSettings != null)
            {
                return _cachedSettings;
            }

            if (File.Exists(SettingsPath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsPath);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json, CachedJsonOptions);
                    if (settings != null)
                    {
                        if (string.IsNullOrWhiteSpace(settings.InstallationId))
                        {
                            settings.InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
                        }
                        _cachedSettings = settings;
                        return _cachedSettings;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AppSettings] Notice reading settings: {ex.Message}");
                }
            }

            var freshSettings = new AppSettings
            {
                InstallationId = HardwareIdentityService.GetOrCreateHardwareId()
            };
            _cachedSettings = freshSettings;
            return freshSettings;
        }
    }

    public static void SaveToDisk(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _cachedSettings = settings;

        lock (SyncLock)
        {
            _saveDebounceTimer?.Dispose();
            _saveDebounceTimer = new Timer(static state =>
            {
                var targetSettings = (AppSettings?)state;
                if (targetSettings == null) return;

                lock (SyncLock)
                {
                    try
                    {
                        if (!Directory.Exists(SettingsDirectory))
                        {
                            Directory.CreateDirectory(SettingsDirectory);
                        }
                        var json = JsonSerializer.Serialize(targetSettings, CachedJsonOptions);
                        File.WriteAllText(TempSettingsPath, json);
                        if (File.Exists(SettingsPath))
                        {
                            File.Delete(SettingsPath);
                        }
                        File.Move(TempSettingsPath, SettingsPath, overwrite: true);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[AppSettings] Error saving settings: {ex.Message}");
                    }
                }
            }, settings, 250, Timeout.Infinite);
        }
    }
}