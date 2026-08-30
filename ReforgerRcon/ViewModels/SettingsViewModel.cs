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
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
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

    public ObservableCollection<string> ThemeOptions { get; } = ["System Default", "Dark Mode", "Light Mode"];
    public ObservableCollection<string> PlayersSortOptions { get; } = ["Default", "Status", "Country", "Name", "BattlEye GUID", "IP:Port", "Ping", "Comment"];
    public ObservableCollection<string> BansSortOptions { get; } = ["Default", "GUID / IP Address", "Minutes Left", "Reason"];
    public ObservableCollection<string> DatabaseSortOptions { get; } = ["Default", "Status", "Country", "Name", "BattlEye GUID", "IP:Port", "Ping", "Comment"];
    public ObservableCollection<string> SortDirections { get; } = ["Ascending", "Descending"];

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
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[SettingsViewModel] ApplyWindowGlassState notice: {ex.Message}");
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
            return;
        }

        try
        {
            var json = File.ReadAllText(SettingsFile);
            Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            InstallationId = !string.IsNullOrWhiteSpace(Settings.InstallationId) ? Settings.InstallationId : HardwareIdentityService.GetOrCreateHardwareId();
        }
        catch (JsonException jsonEx)
        {
            AppLogger.Warn($"[SettingsViewModel] Settings JSON format warning: {jsonEx.Message}. Restoring defaults.", jsonEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
        }
        catch (IOException ioEx)
        {
            AppLogger.Warn($"[SettingsViewModel] Disk I/O warning reading settings: {ioEx.Message}", ioEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Warn($"[SettingsViewModel] Access denied reading settings: {authEx.Message}", authEx);
            Settings = new AppSettings();
            InstallationId = HardwareIdentityService.GetOrCreateHardwareId();
        }
    }

    public void OnThemeSettingChanged(string selectedOption)
    {
        ExecuteSafe(() =>
        {
            var mode = selectedOption switch
            {
                "Dark Mode" => "Dark",
                "Light Mode" => "Light",
                _ => "System"
            };

            Settings.ThemeMode = mode;
            AppSettings.ApplyThemeMode(mode);
            _ = SaveSettingsAsync();
        });
    }

    public void OnSortSettingChanged()
    {
        ExecuteSafe(() =>
        {
            _dashboard?.PlayersTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.BansTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.DatabaseTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
        });
    }

    [RelayCommand]
    public Task<bool> CopyInstallationIdAsync() => ExecuteSafeAsync(async () =>
    {
        await ClipboardService.SetTextAsync(InstallationId).ConfigureAwait(false);
        ToastNotificationService.Instance.ShowToast("Copied Hardware ID", $"Hardware identity copied: {InstallationId}");
    });

    [RelayCommand]
    public Task<bool> RefreshDatabaseStatsAsync() => ExecuteSafeAsync(async () =>
    {
        var stats = await PlayerDatabaseStorageService.GetDatabaseStatisticsAsync().ConfigureAwait(false);
        var sizeText = $"{stats.DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB (WAL: {stats.WalSizeBytes / 1024.0:F1} KB)";
        var recordsText = $"{stats.TotalReforgerPlayers:N0} Reforger / {stats.TotalBattlEyePlayers:N0} BattlEye Players";

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DatabaseSizeText = sizeText;
            DatabaseRecordsText = recordsText;
        });
    }, "Failed to query SQLite database telemetry stats.");

    [RelayCommand]
    public void ToggleLicenseKeyReveal()
    {
        ExecuteSafe(() =>
        {
            IsLicenseKeyRevealed = !IsLicenseKeyRevealed;
            OnPropertyChanged(nameof(LicenseKeyMaskChar));
            OnPropertyChanged(nameof(LicenseKeyIconKind));
        });
    }

    [RelayCommand]
    public Task<bool> SaveSettingsAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
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
            OnSortSettingChanged();

            ToastNotificationService.Instance.ShowToast("Settings Saved", "Preferences and sorting updated.");
        });
    }

    [RelayCommand]
    public Task<bool> UpdateGeoIpDatabasesAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(Settings.MaxMindAccountId) || string.IsNullOrWhiteSpace(Settings.MaxMindLicenseKey))
            {
                ToastNotificationService.Instance.ShowToast(
                    "Credentials Required for Updates",
                    "Enter your MaxMind Account ID & License Key in Settings to update.",
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
            _dashboard?.ShowDialog(new ConfirmDialogViewModel(
                "Clear SQLite Database",
                "Are you sure you want to permanently clear all historical player records from both Reforger and BattlEye tables?",
                "Clear Database",
                true,
                async () =>
                {
                    await PlayerDatabaseStorageService.ClearDatabaseAsync(null).ConfigureAwait(false);
                    await RefreshDatabaseStatsAsync().ConfigureAwait(false);
                    ToastNotificationService.Instance.ShowToast("Database Cleared", "SQLite historical database purged.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }
}