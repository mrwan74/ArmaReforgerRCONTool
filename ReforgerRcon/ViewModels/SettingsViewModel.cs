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
    private static readonly string SettingsFile = Path.Combine(AppContext.BaseDirectory, "appdata", "settings.json");
    private static readonly string TempSettingsFile = Path.Combine(AppContext.BaseDirectory, "appdata", "settings.json.tmp");
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
                Settings.ThemeMode = mode;
                OnPropertyChanged(nameof(SelectedThemeOption));
                AppSettings.ApplyThemeMode(mode);
                AppLogger.Info($"[SettingsViewModel:Theme] Theme mode set to: {mode}");
                _ = SaveSettingsAsync();
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
        AppLogger.Trace($"[SettingsViewModel:Init] Initialized in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
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
            AppLogger.Debug($"[SettingsViewModel:Glass] Window glass state applied in {elapsedMs:F2}ms (UseWindowGlass={enableWindowGlass}).");
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
        if (!File.Exists(SettingsFile))
        {
            var (acc, key) = GeoIpService.ResolveCredentials();
            Settings.MaxMindAccountId = acc;
            Settings.MaxMindLicenseKey = key;
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[SettingsViewModel:Load] Loaded fresh default settings in {elapsedMs:F2}ms.");
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFile);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            InstallationId = !string.IsNullOrWhiteSpace(Settings.InstallationId) ? Settings.InstallationId : HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[SettingsViewModel:Load] Loaded settings in {elapsedMs:F2}ms (AudioAlerts={Settings.AudioAlerts}, PushNotifications={Settings.PushNotifications}, ThemeMode='{Settings.ThemeMode}').");
        }
        catch (JsonException jsonEx)
        {
            AppLogger.Warn($"[SettingsViewModel:Load] Settings JSON corrupted: {jsonEx.Message}. Restoring defaults.", jsonEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            ToastNotificationService.Instance.ShowWarning("Settings Corrupted", "Default settings restored.");
        }
        catch (FileNotFoundException fnfEx)
        {
            AppLogger.Trace($"[SettingsViewModel:Load] Settings file missing: {fnfEx.Message}");
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Warn($"[SettingsViewModel:Load] Access denied reading '{SettingsFile}': {authEx.Message}", authEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
            OnPropertyChanged(nameof(SelectedThemeOption));
            ToastNotificationService.Instance.ShowError("Access Denied", "Operating system denied read permissions to settings file.");
        }
        catch (IOException ioEx)
        {
            AppLogger.Warn($"[SettingsViewModel:Load] Disk I/O warning: {ioEx.Message}", ioEx);
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
            AppLogger.Debug($"[SettingsViewModel:Sort] Sorting preferences changed (Players: {Settings.PlayersSortBy}, Bans: {Settings.BansSortBy}, DB: {Settings.DatabaseSortBy}). Re-filtering...");
            _dashboard?.PlayersTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.BansTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.DatabaseTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            AppLogger.Trace($"[SettingsViewModel:Sort] Re-filter complete in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
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
    public Task<bool> SaveSettingsAsync()
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

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AppSettings.ApplyThemeMode(Settings.ThemeMode);
                ApplyWindowGlassState(Settings.EnableWindowGlass);
                OnPropertyChanged(nameof(SelectedThemeOption));
                OnSortSettingChanged();
            });

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[SettingsViewModel:Save] Preferences saved in {elapsedMs:F2}ms (AudioAlerts={Settings.AudioAlerts}, Push={Settings.PushNotifications}, Glass={Settings.EnableWindowGlass}).");
            ToastNotificationService.Instance.ShowToast("Settings Saved", "Preferences updated.");
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

            await SaveSettingsAsync().ConfigureAwait(false);

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