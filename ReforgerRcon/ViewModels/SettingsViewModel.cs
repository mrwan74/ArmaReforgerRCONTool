using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Controls;
using Material.Icons;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private const string SystemDefaultTheme = "System Default";
    private const string DarkModeTheme = "Dark Mode";
    private const string LightModeTheme = "Light Mode";
    private const string DarkThemeValue = "Dark";
    private const string LightThemeValue = "Light";
    private const string SystemThemeValue = "System";
    private const string DefaultSortOption = "Default";

    private readonly DashboardViewModel? _dashboard;
    private static readonly string SettingsFile = Path.Combine(AppContext.BaseDirectory, "appdata", "settings.json");
    private static readonly string TempSettingsFile = Path.Combine(AppContext.BaseDirectory, "appdata", "settings.json.tmp");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lock FileLock = new();

    [ObservableProperty] public partial AppSettings Settings { get; set; } = new();
    [ObservableProperty] public partial string InstallationId { get; set; } = HardwareIdentityService.GetOrCreateHardwareId();
    [ObservableProperty] public partial bool IsGeoIpUpdating { get; set; }
    [ObservableProperty] public partial string GeoIpCityStatusText { get; set; } = "Active (Pre-bundled GeoLite2-City.mmdb)";
    [ObservableProperty] public partial string GeoIpCountryStatusText { get; set; } = "Active (Pre-bundled GeoLite2-Country.mmdb)";
    [ObservableProperty] public partial string GeoIpLastUpdatedText { get; set; } = "Bundled / Initial";
    [ObservableProperty] public partial bool IsLicenseKeyRevealed { get; set; }

    [ObservableProperty] public partial string DatabaseEngineText { get; set; } = "SQLite 3 (WAL Mode Active)";
    [ObservableProperty] public partial string DatabaseSizeText { get; set; } = "Ready";
    [ObservableProperty] public partial string DatabaseRecordsText { get; set; } = "Ready";

    public ObservableCollection<string> ThemeOptions { get; } = [SystemDefaultTheme, DarkModeTheme, LightModeTheme];
    public ObservableCollection<string> PlayersSortOptions { get; } = [DefaultSortOption, "Status", "Country", "Name", "BattlEye GUID", "IP:Port", "Ping", "Comment"];
    public ObservableCollection<string> BansSortOptions { get; } = [DefaultSortOption, "GUID / IP Address", "Minutes Left", "Reason"];
    public ObservableCollection<string> DatabaseSortOptions { get; } = [DefaultSortOption, "Status", "Country", "Name", "BattlEye GUID", "IP:Port", "Ping", "Comment"];
    public ObservableCollection<string> SortDirections { get; } = ["Ascending", "Descending"];

    public string SelectedThemeOption
    {
        get => Settings.ThemeMode switch
        {
            DarkThemeValue or DarkModeTheme => DarkModeTheme,
            LightThemeValue or LightModeTheme => LightModeTheme,
            _ => SystemDefaultTheme
        };
        set
        {
            var mode = value switch
            {
                DarkModeTheme => DarkThemeValue,
                LightModeTheme => LightThemeValue,
                _ => SystemThemeValue
            };

            if (Settings.ThemeMode != mode)
            {
                Settings.ThemeMode = mode;
                OnPropertyChanged(nameof(SelectedThemeOption));
                AppSettings.ApplyThemeMode(mode);
                _ = SaveSettingsAsync();
            }
        }
    }

    public char LicenseKeyMaskChar => IsLicenseKeyRevealed ? '\0' : '•';
    public MaterialIconKind LicenseKeyIconKind => IsLicenseKeyRevealed ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public SettingsViewModel(DashboardViewModel? dashboard = null)
    {
        _dashboard = dashboard;
        LoadSettingsFast();
    }

    public static void ApplyWindowGlassState(bool enableWindowGlass)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    if (window is LuminaWindow luminaWin)
                    {
                        luminaWin.UseWindowGlass = enableWindowGlass;
                    }
                }
            }
            AppLogger.Debug($"[SettingsViewModel] Applied window glass backdrop state: {enableWindowGlass}");
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Warn($"[SettingsViewModel] Invalid window state applying backdrop effect: {invEx.Message}", invEx);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[SettingsViewModel] Unexpected error in ApplyWindowGlassState: {ex.Message}", ex);
        }
    }

    private void LoadSettingsFast()
    {
        if (!File.Exists(SettingsFile))
        {
            var (acc, key) = GeoIpService.ResolveCredentials();
            Settings.MaxMindAccountId = acc;
            Settings.MaxMindLicenseKey = key;
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            AppLogger.Info("[SettingsViewModel] Initialized fresh default application settings.");
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFile);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            InstallationId = !string.IsNullOrWhiteSpace(Settings.InstallationId) ? Settings.InstallationId : HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            AppLogger.Info($"[SettingsViewModel] Loaded settings from '{SettingsFile}' (AudioAlerts={Settings.AudioAlerts}, PushNotifications={Settings.PushNotifications}, ThemeMode='{Settings.ThemeMode}').");
        }
        catch (JsonException jsonEx)
        {
            AppLogger.Warn($"[SettingsViewModel] Settings JSON format warning: {jsonEx.Message}. Restoring defaults.", jsonEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            ToastNotificationService.Instance.ShowWarning("Settings Corrupted", "Settings file contained invalid JSON. Default settings restored.");
        }
        catch (FileNotFoundException fnfEx)
        {
            AppLogger.Trace($"[SettingsViewModel] Settings file was not found: {fnfEx.Message}");
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Warn($"[SettingsViewModel] Access denied reading settings from '{SettingsFile}': {authEx.Message}", authEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            ToastNotificationService.Instance.ShowError("Access Denied", "Operating system denied read permissions to settings file.");
        }
        catch (IOException ioEx)
        {
            AppLogger.Warn($"[SettingsViewModel] Disk I/O warning reading settings: {ioEx.Message}", ioEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
        }
    }

    public void OnSortSettingChanged()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[SettingsViewModel] Sorting preferences updated (Players: {Settings.PlayersSortBy}, Bans: {Settings.BansSortBy}, DB: {Settings.DatabaseSortBy}). Re-filtering tabs...");
            _dashboard?.PlayersTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.BansTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.DatabaseTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
        });
    }

    [RelayCommand]
    public void TestAudioAlert(string? soundType)
    {
        ExecuteSafe(() =>
        {
            var alert = soundType switch
            {
                "Join" => SoundAlertType.PlayerJoined,
                "Leave" => SoundAlertType.PlayerLeft,
                "Watchlist" => SoundAlertType.WatchlistAlert,
                "Critical" => SoundAlertType.CriticalError,
                _ => SoundAlertType.DefaultNotification
            };

            AppLogger.Info($"[SettingsViewModel] User triggered manual Audio Alert test for: {alert}");
            SoundNotificationService.PlayAlert(alert);
            ToastNotificationService.Instance.ShowToast("Audio Alert Test", $"Playing {alert} audio alert.");
        });
    }

    [RelayCommand]
    public void TestPushNotification()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[SettingsViewModel] User triggered manual Native Push Notification test.");
            PushNotificationService.SendNotification("ARRT Test Alert", "Native OS Push Notification channel is operational.", "default");
            ToastNotificationService.Instance.ShowToast("Push Notification Dispatched", "Dispatched test notification to system notification tray.");
        });
    }

    [RelayCommand]
    public Task<bool> CopyInstallationIdAsync() => ExecuteSafeAsync(async () =>
    {
        await ClipboardService.SetTextAsync(InstallationId).ConfigureAwait(false);
        AppLogger.Info($"[SettingsViewModel] Copied installation ID '{InstallationId}' to clipboard.");
        ToastNotificationService.Instance.ShowToast("Copied Hardware ID", $"Hardware identity copied: {InstallationId}");
    });

    [RelayCommand]
    public Task<bool> RefreshDatabaseStatsAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Debug("[SettingsViewModel] Querying SQLite historical database statistics...");
        var stats = await PlayerDatabaseStorageService.GetDatabaseStatisticsAsync().ConfigureAwait(false);
        var sizeText = $"{stats.DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB (WAL: {stats.WalSizeBytes / 1024.0:F1} KB)";
        var recordsText = $"{stats.TotalReforgerPlayers:N0} Reforger / {stats.TotalBattlEyePlayers:N0} BattlEye Players";

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DatabaseSizeText = sizeText;
            DatabaseRecordsText = recordsText;
        });
        AppLogger.Info($"[SettingsViewModel] SQLite statistics updated: {recordsText}, Size: {sizeText}");
    }, "Failed to query SQLite database telemetry stats.");

    [RelayCommand]
    public void ToggleLicenseKeyReveal()
    {
        ExecuteSafe(() =>
        {
            IsLicenseKeyRevealed = !IsLicenseKeyRevealed;
            OnPropertyChanged(nameof(LicenseKeyMaskChar));
            OnPropertyChanged(nameof(LicenseKeyIconKind));
            AppLogger.Trace($"[SettingsViewModel] Toggled license key reveal state: {IsLicenseKeyRevealed}");
        });
    }

    [RelayCommand]
    public Task<bool> SaveSettingsAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            using var timing = AppLogger.Measure("SettingsViewModel.SaveSettingsAsync");

            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Settings.InstallationId = InstallationId;
            var json = JsonSerializer.Serialize(Settings, JsonOptions);

            lock (FileLock)
            {
                File.WriteAllText(TempSettingsFile, json);
                if (File.Exists(SettingsFile))
                {
                    File.Delete(SettingsFile);
                }
                File.Move(TempSettingsFile, SettingsFile, overwrite: true);
            }

            AppSettings.ApplyThemeMode(Settings.ThemeMode);
            ApplyWindowGlassState(Settings.EnableWindowGlass);
            OnPropertyChanged(nameof(SelectedThemeOption));
            OnSortSettingChanged();

            AppLogger.Info($"[SettingsViewModel] Persisted preferences successfully: AudioAlerts={Settings.AudioAlerts}, PushNotifications={Settings.PushNotifications}, Glass={Settings.EnableWindowGlass}, AutoBans={Settings.AutoRefreshBans}");
            ToastNotificationService.Instance.ShowToast("Settings Saved", "Preferences and notification settings updated.");
        });
    }

    [RelayCommand]
    public Task<bool> UpdateGeoIpDatabasesAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(Settings.MaxMindAccountId) || string.IsNullOrWhiteSpace(Settings.MaxMindLicenseKey))
            {
                AppLogger.Warn("[SettingsViewModel] GeoIP update aborted: MaxMind Account ID or License Key is empty.");
                ToastNotificationService.Instance.ShowToast(
                    "Credentials Required for Updates",
                    "Enter your MaxMind Account ID and License Key in Settings to update.",
                    "GEOIP_NOTICE"
                );
                return;
            }

            await SaveSettingsAsync().ConfigureAwait(false);

            if (_dashboard != null)
            {
                _dashboard.ShowDialog(new GeoIpUpdateDialogViewModel(() => _dashboard.CloseDialog()));
            }
            else
            {
                IsGeoIpUpdating = true;
                try
                {
                    AppLogger.Info("[SettingsViewModel] Starting standalone GeoIP database update...");
                    await GeoIpService.UpdateDatabasesAsync(force: true).ConfigureAwait(false);
                }
                finally
                {
                    IsGeoIpUpdating = false;
                }
            }
        });
    }

    [RelayCommand]
    private void ClearDatabase()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[SettingsViewModel] Prompting confirmation for SQLite database purge.");
            _dashboard?.ShowDialog(new ConfirmDialogViewModel(
                "Clear SQLite Database",
                "Are you sure you want to permanently clear all historical player records from both Reforger and BattlEye tables?",
                "Clear Database",
                true,
                async () =>
                {
                    AppLogger.Info("[SettingsViewModel] Executing SQLite historical database purge...");
                    await PlayerDatabaseStorageService.ClearDatabaseAsync(null).ConfigureAwait(false);
                    await RefreshDatabaseStatsAsync().ConfigureAwait(false);
                    ToastNotificationService.Instance.ShowToast("Database Cleared", "SQLite historical database purged.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }
}