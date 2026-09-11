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
using System.Diagnostics;
using System.IO;
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

                AppLogger.Info($"[SettingsViewModel:Theme] Theme mode changed: '{prevMode}' -> '{mode}' (Thread=T{Environment.CurrentManagedThreadId:D2})");

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
        var startTimestamp = Stopwatch.GetTimestamp();
        _dashboard = dashboard;
        AppLogger.Debug($"[SettingsViewModel:Init] Instantiating SettingsViewModel (Thread=T{Environment.CurrentManagedThreadId:D2})...");
        LoadSettingsFast();
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Trace($"[SettingsViewModel:Init] Initialized in {elapsedMs:F2}ms.");
    }

    public static void ApplyWindowGlassState(bool enableWindowGlass)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyWindowGlassState(enableWindowGlass));
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AppLogger.Debug($"[SettingsViewModel:Glass] Applying window glass backdrop (Mica/Acrylic) -> {enableWindowGlass}...");
            int modifiedWindows = 0;

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    if (window is LuminaWindow luminaWin)
                    {
                        luminaWin.UseWindowGlass = enableWindowGlass;
                        modifiedWindows++;
                    }
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[SettingsViewModel:Glass] Backdrop applied to {modifiedWindows} window(s) in {elapsedMs:F2}ms (UseWindowGlass={enableWindowGlass}).");
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Warn($"[SettingsViewModel:Glass] Invalid window state applying backdrop: {invEx.Message}", invEx);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[SettingsViewModel:Glass] Unexpected error applying window glass: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowWarning("Window Backdrop Notice", "Failed to toggle glass transparency: " + ex.Message);
        }
    }

    private void LoadSettingsFast()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AppLogger.Trace("[SettingsViewModel:Load] Reading settings from persistent JSON storage...");
            Settings = AppSettings.LoadFromDisk();
            InstallationId = !string.IsNullOrWhiteSpace(Settings.InstallationId)
                ? Settings.InstallationId
                : HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[SettingsViewModel:Load] Preferences loaded successfully in {elapsedMs:F2}ms (Theme: '{Settings.ThemeMode}', AudioAlerts: {Settings.AudioAlerts}, Push: {Settings.PushNotifications}).");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[SettingsViewModel:Load] Settings load error: {ex.Message}. Falling back to clean defaults.", ex);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
        }
    }

    public void OnSortSettingChanged()
    {
        ExecuteSafe(() =>
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            var context = new Dictionary<string, object?>
            {
                ["players_sort"] = Settings.PlayersSortBy,
                ["players_asc"] = Settings.PlayersSortAscending,
                ["bans_sort"] = Settings.BansSortBy,
                ["bans_asc"] = Settings.BansSortAscending,
                ["db_sort"] = Settings.DatabaseSortBy,
                ["db_asc"] = Settings.DatabaseSortAscending,
                ["thread_id"] = Environment.CurrentManagedThreadId
            };

            AppLogger.Debug("[SettingsViewModel:Sort] Sorting preferences altered. Updating view models...", context);
            _dashboard?.PlayersTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.BansTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.DatabaseTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[SettingsViewModel:Sort] Re-filter across all active tabs completed in {elapsedMs:F2}ms.");
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

            AppLogger.Info($"[SettingsViewModel:AudioTest] Triggering test audio alert: {alert}");
            SoundNotificationService.PlayAlert(alert);
            ToastNotificationService.Instance.ShowToast("Audio Alert Test", $"Playing {alert} audio alert.");
        });
    }

    [RelayCommand]
    public void TestPushNotification()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[SettingsViewModel:PushTest] Triggering test native push notification to OS tray...");
            PushNotificationService.SendNotification("ARRT Test Alert", "Native OS Push Notification channel is operational.", "default");
            ToastNotificationService.Instance.ShowToast("Push Notification Dispatched", "Test notification sent.");
        });
    }

    [RelayCommand]
    public Task<bool> CopyInstallationIdAsync() => ExecuteSafeAsync(async () =>
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await ClipboardService.SetTextAsync(InstallationId).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[SettingsViewModel:Clipboard] Copied hardware installation ID in {elapsedMs:F2}ms: '{InstallationId}'");
        ToastNotificationService.Instance.ShowToast("Copied Hardware ID", $"Hardware identity copied: {InstallationId}");
    });

    [RelayCommand]
    public Task<bool> RefreshDatabaseStatsAsync() => ExecuteSafeAsync(async () =>
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        AppLogger.Debug("[SettingsViewModel:Stats] Querying SQLite relational database telemetry statistics...");

        var stats = await PlayerDatabaseStorageService.GetDatabaseStatisticsAsync().ConfigureAwait(false);
        var sizeText = $"{stats.DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB (WAL: {stats.WalSizeBytes / 1024.0:F1} KB)";
        var recordsText = $"{stats.TotalReforgerPlayers:N0} Reforger / {stats.TotalBattlEyePlayers:N0} BattlEye Players";

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            DatabaseSizeText = sizeText;
            DatabaseRecordsText = recordsText;
        });

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[SettingsViewModel:Stats] SQLite stats queried in {elapsedMs:F2}ms: {recordsText}, Size: {sizeText}");
    }, "Failed to query SQLite database telemetry stats.");

    [RelayCommand]
    public void ToggleLicenseKeyReveal()
    {
        ExecuteSafe(() =>
        {
            IsLicenseKeyRevealed = !IsLicenseKeyRevealed;
            OnPropertyChanged(nameof(LicenseKeyMaskChar));
            OnPropertyChanged(nameof(LicenseKeyIconKind));
            AppLogger.Trace($"[SettingsViewModel:LicenseKey] Reveal state mutated: {IsLicenseKeyRevealed}");
        });
    }

    [RelayCommand]
    public Task<bool> SaveSettingsAsync() => SaveSettingsAsync(showToast: true);

    public Task<bool> SaveSettingsAsync(bool showToast)
    {
        return ExecuteSafeAsync(async () =>
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            using var timing = AppLogger.Measure("SettingsViewModel.SaveSettingsAsync");
            var context = new Dictionary<string, object?>
            {
                ["theme_mode"] = Settings.ThemeMode,
                ["audio_enabled"] = Settings.AudioAlerts,
                ["toast_enabled"] = Settings.ToastNotifications,
                ["push_enabled"] = Settings.PushNotifications,
                ["thread_id"] = Environment.CurrentManagedThreadId
            };

            AppLogger.Info("[SettingsViewModel:Save] Persisting updated application preferences to disk...", context);

            var dir = Path.GetDirectoryName(SettingsFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                AppLogger.Trace($"[SettingsViewModel:Save] Creating settings storage directory: '{dir}'");
                Directory.CreateDirectory(dir);
            }

            Settings.InstallationId = InstallationId;
            var json = JsonSerializer.Serialize(Settings, JsonOptions);

            await FileLock.WaitAsync().ConfigureAwait(false);
            try
            {
                AppLogger.Trace($"[SettingsViewModel:Save] Writing {json.Length} characters to temporary file '{TempSettingsFile}'...");
                await File.WriteAllTextAsync(TempSettingsFile, json).ConfigureAwait(false);

                if (File.Exists(SettingsFile))
                {
                    File.Delete(SettingsFile);
                }

                File.Move(TempSettingsFile, SettingsFile, overwrite: true);
                AppLogger.Trace("[SettingsViewModel:Save] Atomic file swap complete.");
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

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context["elapsed_ms"] = elapsedMs;
            AppLogger.Info($"[SettingsViewModel:Save] Preferences successfully persisted in {elapsedMs:F2}ms.", context);

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
            var context = new Dictionary<string, object?>
            {
                ["has_account_id"] = !string.IsNullOrWhiteSpace(Settings.MaxMindAccountId),
                ["has_license_key"] = !string.IsNullOrWhiteSpace(Settings.MaxMindLicenseKey),
                ["thread_id"] = Environment.CurrentManagedThreadId
            };

            if (string.IsNullOrWhiteSpace(Settings.MaxMindAccountId) || string.IsNullOrWhiteSpace(Settings.MaxMindLicenseKey))
            {
                AppLogger.Warn("[SettingsViewModel:GeoIP] GeoIP update aborted: MaxMind credentials missing.", null, context);
                ToastNotificationService.Instance.ShowToast(
                    "Credentials Required for Updates",
                    "Enter your MaxMind Account ID and License Key in Settings to update.",
                    "GEOIP_NOTICE"
                );
                return;
            }

            AppLogger.Info("[SettingsViewModel:GeoIP] Saving credentials and launching GeoIP download...", context);
            await SaveSettingsAsync(showToast: false).ConfigureAwait(false);

            if (_dashboard != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AppLogger.Debug("[SettingsViewModel:GeoIP] Opening GeoIpUpdateDialog modal overlay in Dashboard...");
                    _dashboard.ShowDialog(new GeoIpUpdateDialogViewModel(() => _dashboard.CloseDialog()));
                });
            }
            else
            {
                IsGeoIpUpdating = true;
                try
                {
                    AppLogger.Info("[SettingsViewModel:GeoIP] Executing direct background database update...");
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
            AppLogger.Info("[SettingsViewModel:DatabasePurge] User prompted for database purge confirmation.");
            _dashboard?.ShowDialog(new ConfirmDialogViewModel(
                "Clear SQLite Database",
                "Are you sure you want to permanently clear all historical player records?",
                "Clear Database",
                true,
                async () =>
                {
                    var startTimestamp = Stopwatch.GetTimestamp();
                    AppLogger.Warn("[SettingsViewModel:DatabasePurge] Executing confirmed SQLite database purge...");
                    await PlayerDatabaseStorageService.ClearDatabaseAsync(null).ConfigureAwait(false);
                    await RefreshDatabaseStatsAsync().ConfigureAwait(false);
                    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    AppLogger.Info($"[SettingsViewModel:DatabasePurge] Historical database purge complete in {elapsedMs:F2}ms.");
                    ToastNotificationService.Instance.ShowToast("Database Cleared", "SQLite database purged.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }
}