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
    private static readonly string SettingsDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    private static readonly string TempSettingsPath = Path.Combine(SettingsDirectory, "settings.json.tmp");
    private static readonly Lock SyncLock = new();

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
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, CachedJsonOptions);
                if (settings != null)
                {
                    return settings.SendAnonymousCrashReports;
                }
            }
        }
        catch (JsonException jsonEx)
        {
            Debug.WriteLine($"[AppSettings] Json error checking crash reporting: {jsonEx.Message}");
        }
        catch (IOException ioEx)
        {
            Debug.WriteLine($"[AppSettings] IO error checking crash reporting: {ioEx.Message}");
        }
        return false;
    }

    public static AppSettings LoadFromDisk()
    {
        lock (SyncLock)
        {
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
                            SaveToDisk(settings);
                        }
                        return settings;
                    }
                }
                catch (JsonException jsonEx)
                {
                    Debug.WriteLine($"[AppSettings] JSON parse error in settings: {jsonEx.Message}");
                }
                catch (IOException ioEx)
                {
                    Debug.WriteLine($"[AppSettings] IO error loading settings: {ioEx.Message}");
                }
                catch (UnauthorizedAccessException authEx)
                {
                    Debug.WriteLine($"[AppSettings] Access denied loading settings: {authEx.Message}");
                }
            }

            var freshSettings = new AppSettings
            {
                InstallationId = HardwareIdentityService.GetOrCreateHardwareId()
            };
            SaveToDisk(freshSettings);
            return freshSettings;
        }
    }

    public static void SaveToDisk(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (string.IsNullOrWhiteSpace(settings.InstallationId))
        {
            settings.InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
        }

        lock (SyncLock)
        {
            try
            {
                if (!Directory.Exists(SettingsDirectory))
                {
                    Directory.CreateDirectory(SettingsDirectory);
                }
                var json = JsonSerializer.Serialize(settings, CachedJsonOptions);
                File.WriteAllText(TempSettingsPath, json);
                if (File.Exists(SettingsPath))
                {
                    File.Delete(SettingsPath);
                }
                File.Move(TempSettingsPath, SettingsPath, overwrite: true);
            }
            catch (IOException ioEx)
            {
                Debug.WriteLine($"[AppSettings] IO error saving settings: {ioEx.Message}");
            }
            catch (UnauthorizedAccessException authEx)
            {
                Debug.WriteLine($"[AppSettings] Permission error saving settings: {authEx.Message}");
            }
        }
    }
}