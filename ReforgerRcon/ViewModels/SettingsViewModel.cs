using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
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
using System.Diagnostics;
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
    private static readonly string SettingsFile = Path.Combine(AppPaths.AppDataDirectory, "settings.json");
    private static readonly string TempSettingsFile = Path.Combine(AppPaths.AppDataDirectory, "settings.json.tmp");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly SemaphoreSlim FileLock = new(1, 1);

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

    // Velopack Auto-Update Observables
    [ObservableProperty] public partial string UpdateStatusText { get; set; } = UpdateService.Instance.StatusDetails;
    [ObservableProperty] public partial bool IsCheckingForUpdates { get; set; }
    [ObservableProperty] public partial bool IsUpdateAvailable { get; set; }
    [ObservableProperty] public partial bool IsUpdateReadyToRestart { get; set; }
    [ObservableProperty] public partial int UpdateDownloadProgress { get; set; }

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
                var prevMode = Settings.ThemeMode;
                Settings.ThemeMode = mode;
                OnPropertyChanged(nameof(SelectedThemeOption));
                AppSettings.ApplyThemeMode(mode);
                AppLogger.Info($"[SettingsViewModel:Theme] Theme mode changed: '{prevMode}' -> '{mode}'");

                AppLogger.TrackEvent("theme_choice_updated", new Dictionary<string, object>
                {
                    ["previous_theme"] = prevMode,
                    ["theme_mode"] = mode,
                    ["is_startup"] = false
                });

                _ = SaveSettingsAsync(showToast: false);
            }
        }
    }

    public char LicenseKeyMaskChar => IsLicenseKeyRevealed ? '\0' : '•';
    public MaterialIconKind LicenseKeyIconKind => IsLicenseKeyRevealed ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public SettingsViewModel(DashboardViewModel? dashboard = null)
    {
        var start = Stopwatch.GetTimestamp();
        _dashboard = dashboard;
        AppLogger.Debug("[SettingsViewModel:Init] Initializing SettingsViewModel...");
        LoadSettingsFast();

        // Wire UpdateService reactive state changes to the UI
        UpdateService.Instance.StateChanged += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                UpdateStatusText = UpdateService.Instance.StatusDetails;
                IsCheckingForUpdates = UpdateService.Instance.CurrentStatus == UpdateStatus.Checking ||
                                       UpdateService.Instance.CurrentStatus == UpdateStatus.Downloading;
                IsUpdateAvailable = UpdateService.Instance.CurrentStatus == UpdateStatus.UpdateAvailable;
                IsUpdateReadyToRestart = UpdateService.Instance.CurrentStatus == UpdateStatus.ReadyToRestart;
                UpdateDownloadProgress = UpdateService.Instance.DownloadPercentage;
            });
        };

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Trace($"[SettingsViewModel:Init] Initialized in {elapsedMs:F2}ms.");
    }

    [RelayCommand]
    public static Task CheckForUpdatesManualAsync()
    {
        AppLogger.Info("[SettingsViewModel:Update] User clicked Check For Updates.");
        return UpdateService.Instance.CheckForUpdatesAsync(isManual: true);
    }

    [RelayCommand]
    public static Task DownloadUpdateManualAsync()
    {
        AppLogger.Info("[SettingsViewModel:Update] User clicked Download Update.");
        return UpdateService.Instance.DownloadUpdateAsync();
    }

    [RelayCommand]
    public static void RestartToApplyUpdate()
    {
        AppLogger.Info("[SettingsViewModel:Update] User clicked Restart & Apply.");
        UpdateService.Instance.RestartAndApply();
    }

    public static void ApplyWindowGlassState(bool enableWindowGlass)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyWindowGlassState(enableWindowGlass));
            return;
        }

        var start = Stopwatch.GetTimestamp();
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
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[SettingsViewModel:Glass] Window glass backdrop applied in {elapsedMs:F2}ms (UseWindowGlass={enableWindowGlass}).");
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Warn($"[SettingsViewModel:Glass] Invalid window state applying backdrop: {invEx.Message}", invEx);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[SettingsViewModel:Glass] Unexpected error: {ex.Message}", ex);
        }
    }

    private void LoadSettingsFast()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            Settings = AppSettings.LoadFromDisk();
            InstallationId = !string.IsNullOrWhiteSpace(Settings.InstallationId)
                ? Settings.InstallationId
                : HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[SettingsViewModel:Load] Loaded settings in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[SettingsViewModel:Load] Settings load error: {ex.Message}. Restoring defaults.", ex);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
        }
    }

    public void OnSortSettingChanged()
    {
        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var context = new Dictionary<string, object?>
            {
                ["players_sort"] = Settings.PlayersSortBy,
                ["players_asc"] = Settings.PlayersSortAscending,
                ["bans_sort"] = Settings.BansSortBy,
                ["bans_asc"] = Settings.BansSortAscending,
                ["db_sort"] = Settings.DatabaseSortBy,
                ["db_asc"] = Settings.DatabaseSortAscending
            };
            AppLogger.Debug("[SettingsViewModel:Sort] Sorting preferences changed. Re-filtering all active tabs...", context);
            _dashboard?.PlayersTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.BansTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.DatabaseTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Trace($"[SettingsViewModel:Sort] Re-filter completed in {elapsedMs:F2}ms.");
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

            AppLogger.Info($"[SettingsViewModel:AudioTest] Playing alert: {alert}");
            SoundNotificationService.PlayAlert(alert);
            ToastNotificationService.Instance.ShowToast("Audio Alert Test", $"Playing {alert} audio alert.");
        });
    }

    [RelayCommand]
    public void TestPushNotification()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[SettingsViewModel:PushTest] Triggering test push notification.");
            PushNotificationService.SendNotification("ARRT Test Alert", "Native OS Push Notification channel is operational.", "default");
            ToastNotificationService.Instance.ShowToast("Push Notification Dispatched", "Test notification sent.");
        });
    }

    [RelayCommand]
    public Task<bool> CopyInstallationIdAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        await ClipboardService.SetTextAsync(InstallationId).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[SettingsViewModel:Clipboard] Copied installation ID in {elapsedMs:F2}ms: '{InstallationId}'");
        ToastNotificationService.Instance.ShowToast("Copied Hardware ID", $"Hardware identity copied: {InstallationId}");
    });

    [RelayCommand]
    public Task<bool> RefreshDatabaseStatsAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Debug("[SettingsViewModel:Stats] Querying SQLite statistics...");
        var stats = await PlayerDatabaseStorageService.GetDatabaseStatisticsAsync().ConfigureAwait(false);
        var sizeText = $"{stats.DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB (WAL: {stats.WalSizeBytes / 1024.0:F1} KB)";
        var recordsText = $"{stats.TotalReforgerPlayers:N0} Reforger / {stats.TotalBattlEyePlayers:N0} BattlEye Players";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DatabaseSizeText = sizeText;
            DatabaseRecordsText = recordsText;
        });

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[SettingsViewModel:Stats] SQLite stats updated in {elapsedMs:F2}ms: {recordsText}, Size: {sizeText}");
    }, "Failed to query SQLite database telemetry stats.");

    [RelayCommand]
    public void ToggleLicenseKeyReveal()
    {
        ExecuteSafe(() =>
        {
            IsLicenseKeyRevealed = !IsLicenseKeyRevealed;
            OnPropertyChanged(nameof(LicenseKeyMaskChar));
            OnPropertyChanged(nameof(LicenseKeyIconKind));
            AppLogger.Trace($"[SettingsViewModel:LicenseKey] Reveal state: {IsLicenseKeyRevealed}");
        });
    }

    [RelayCommand]
    public Task<bool> SaveSettingsAsync() => SaveSettingsAsync(showToast: true);

    public Task<bool> SaveSettingsAsync(bool showToast)
    {
        return ExecuteSafeAsync(async () =>
        {
            var start = Stopwatch.GetTimestamp();
            using var timing = AppLogger.Measure("SettingsViewModel.SaveSettingsAsync");

            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Settings.InstallationId = InstallationId;
            var json = JsonSerializer.Serialize(Settings, JsonOptions);

            await FileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await File.WriteAllTextAsync(TempSettingsFile, json).ConfigureAwait(false);
                if (File.Exists(SettingsFile))
                {
                    File.Delete(SettingsFile);
                }
                File.Move(TempSettingsFile, SettingsFile, overwrite: true);
            }
            finally
            {
                FileLock.Release();
            }

            AppLogger.TrackEvent("settings_preferences_saved", new Dictionary<string, object>
            {
                ["theme_mode"] = Settings.ThemeMode,
                ["audio_alerts_enabled"] = Settings.AudioAlerts,
                ["toast_notifications_enabled"] = Settings.ToastNotifications,
                ["push_notifications_enabled"] = Settings.PushNotifications,
                ["alert_on_join"] = Settings.AlertOnJoin,
                ["alert_on_leave"] = Settings.AlertOnLeave,
                ["alert_on_watchlist_join"] = Settings.AlertOnWatchlistJoin,
                ["alert_on_watchlist_leave"] = Settings.AlertOnWatchlistLeave,
                ["refresh_interval_seconds"] = Settings.RefreshIntervalSeconds,
                ["window_glass_enabled"] = Settings.EnableWindowGlass
            });

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AppSettings.ApplyThemeMode(Settings.ThemeMode);
                ApplyWindowGlassState(Settings.EnableWindowGlass);
                OnPropertyChanged(nameof(SelectedThemeOption));
                OnSortSettingChanged();
            });

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[SettingsViewModel:Save] Preferences saved in {elapsedMs:F2}ms (AudioAlerts={Settings.AudioAlerts}, Push={Settings.PushNotifications}, Glass={Settings.EnableWindowGlass}).");

            if (showToast)
            {
                ToastNotificationService.Instance.ShowToast("Settings Saved", "Preferences updated.");
            }
        });
    }

    [RelayCommand]
    public Task<bool> UpdateGeoIpDatabasesAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(Settings.MaxMindAccountId) || string.IsNullOrWhiteSpace(Settings.MaxMindLicenseKey))
            {
                AppLogger.Warn("[SettingsViewModel:GeoIP] Update aborted: credentials missing.");
                ToastNotificationService.Instance.ShowToast(
                    "Credentials Required for Updates",
                    "Enter your MaxMind Account ID and License Key in Settings to update.",
                    "GEOIP_NOTICE"
                );
                return;
            }

            await SaveSettingsAsync(showToast: false).ConfigureAwait(false);

            if (_dashboard != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AppLogger.Info("[SettingsViewModel:GeoIP] Opening GeoIpUpdateDialog in dashboard...");
                    _dashboard.ShowDialog(new GeoIpUpdateDialogViewModel(() => _dashboard.CloseDialog()));
                });
            }
            else
            {
                IsGeoIpUpdating = true;
                try
                {
                    AppLogger.Info("[SettingsViewModel:GeoIP] Starting GeoIP database update...");
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
            AppLogger.Info("[SettingsViewModel:DatabasePurge] Prompting confirmation for database purge.");
            _dashboard?.ShowDialog(new ConfirmDialogViewModel(
                "Clear SQLite Database",
                "Are you sure you want to permanently clear all historical player records?",
                "Clear Database",
                true,
                async () =>
                {
                    var start = Stopwatch.GetTimestamp();
                    AppLogger.Info("[SettingsViewModel:DatabasePurge] Executing database purge...");
                    await PlayerDatabaseStorageService.ClearDatabaseAsync(null).ConfigureAwait(false);
                    await RefreshDatabaseStatsAsync().ConfigureAwait(false);
                    var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    AppLogger.Info($"[SettingsViewModel:DatabasePurge] Purge complete in {elapsedMs:F2}ms.");
                    ToastNotificationService.Instance.ShowToast("Database Cleared", "SQLite database purged.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }
}