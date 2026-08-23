using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Controls;
using Material.Icons;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly DashboardViewModel? _dashboard;
    private static readonly string SettingsFile = Path.Combine(AppContext.BaseDirectory, "appdata", "settings.json");
    private static readonly string TempSettingsFile = Path.Combine(AppContext.BaseDirectory, "appdata", "settings.json.tmp");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Lock FileLock = new();

    [ObservableProperty] public partial AppSettings Settings { get; set; } = new();
    [ObservableProperty] public partial bool IsGeoIpUpdating { get; set; }
    [ObservableProperty] public partial string GeoIpCityStatusText { get; set; } = "Not Loaded";
    [ObservableProperty] public partial string GeoIpCountryStatusText { get; set; } = "Not Loaded";
    [ObservableProperty] public partial string GeoIpLastUpdatedText { get; set; } = "Never";
    [ObservableProperty] public partial bool IsLicenseKeyRevealed { get; set; }

    [ObservableProperty] public partial string DatabaseEngineText { get; set; } = "SQLite 3 (WAL Mode Active)";
    [ObservableProperty] public partial string DatabaseSizeText { get; set; } = "Calculating...";
    [ObservableProperty] public partial string DatabaseRecordsText { get; set; } = "Calculating...";

    public ObservableCollection<string> PlayersSortOptions { get; } = ["Default", "Status", "Country", "Name", "BattlEye GUID", "IP:Port", "Ping", "Comment"];
    public ObservableCollection<string> BansSortOptions { get; } = ["Default", "GUID / IP Address", "Minutes Left", "Reason"];
    public ObservableCollection<string> DatabaseSortOptions { get; } = ["Default", "Status", "Country", "Name", "BattlEye GUID", "IP:Port", "Ping", "Comment"];
    public ObservableCollection<string> SortDirections { get; } = ["Ascending", "Descending"];

    public char LicenseKeyMaskChar => IsLicenseKeyRevealed ? '\0' : '•';
    public MaterialIconKind LicenseKeyIconKind => IsLicenseKeyRevealed ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public SettingsViewModel(DashboardViewModel? dashboard = null)
    {
        _dashboard = dashboard;
        LoadSettings();
        GeoIpService.DatabasesUpdated += RefreshGeoIpStatus;
        RefreshGeoIpStatus();
        _ = RefreshDatabaseStatsAsync();
    }

    public static void ApplyWindowGlassState(bool enableWindowGlass)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                int count = 0;
                foreach (var window in desktop.Windows)
                {
                    if (window is LuminaWindow luminaWin)
                    {
                        luminaWin.UseWindowGlass = enableWindowGlass;
                        count++;
                    }
                }
                AppLogger.Info($"[SettingsViewModel] Applied WindowGlass/Blur setting (Enabled: {enableWindowGlass}) across {count} active LuminaWindow instance(s).");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[SettingsViewModel] Error applying window glass setting to visual tree: {ex.Message}");
        }
    }

    private void LoadSettings()
    {
        ExecuteSafe(() =>
        {
            using var timing = AppLogger.Measure("SettingsViewModel.LoadSettings");
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                AppLogger.Info($"[SettingsViewModel] Loaded settings from '{SettingsFile}' ({json.Length} chars).");
            }
            else
            {
                var (acc, key) = GeoIpService.ResolveCredentials();
                Settings.MaxMindAccountId = acc;
                Settings.MaxMindLicenseKey = key;
                AppLogger.Info("[SettingsViewModel] Initialized default application settings.");
            }

            ApplyWindowGlassState(Settings.EnableWindowGlass);
        }, "Failed to load application settings from disk.");
    }

    public void OnSortSettingChanged()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[SettingsViewModel] Sort settings modified. Refreshing active tab filters...");
            _dashboard?.PlayersTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.BansTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard?.DatabaseTab.ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
        });
    }

    [RelayCommand]
    public Task<bool> RefreshDatabaseStatsAsync() => ExecuteSafeAsync(async () =>
    {
        using var timing = AppLogger.Measure("SettingsViewModel.RefreshDatabaseStatsAsync");
        AppLogger.Debug("[SettingsViewModel] Refreshing SQLite database telemetry statistics...");

        var stats = await PlayerDatabaseStorageService.GetDatabaseStatisticsAsync();
        DatabaseSizeText = $"{stats.DatabaseSizeBytes / (1024.0 * 1024.0):F2} MB (WAL: {stats.WalSizeBytes / 1024.0:F1} KB)";
        DatabaseRecordsText = $"{stats.TotalPlayers:N0} Players ({stats.TotalAliases:N0} Recorded Aliases)";

        AppLogger.Info($"[SettingsViewModel] Refreshed database stats: {DatabaseRecordsText}, Size: {DatabaseSizeText}");
    }, "Failed to query SQLite database telemetry stats.");

    [RelayCommand]
    public void ToggleLicenseKeyReveal()
    {
        ExecuteSafe(() =>
        {
            IsLicenseKeyRevealed = !IsLicenseKeyRevealed;
            OnPropertyChanged(nameof(LicenseKeyMaskChar));
            OnPropertyChanged(nameof(LicenseKeyIconKind));
            AppLogger.Trace($"[SettingsViewModel] Toggled MaxMind License Key mask: IsRevealed={IsLicenseKeyRevealed}");
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

            ApplyWindowGlassState(Settings.EnableWindowGlass);
            OnSortSettingChanged();

            AppLogger.Info($"[SettingsViewModel] Settings saved to '{SettingsFile}'.");
            ToastNotificationService.Instance.ShowToast("Settings Saved", "Preferences, appearance, and sorting updated.");
            RefreshGeoIpStatus();
        });
    }

    private void RefreshGeoIpStatus()
    {
        ExecuteSafe(() =>
        {
            var hasCreds = GeoIpService.HasCustomCredentials;

            if (GeoIpService.IsCityDbLoaded)
            {
                GeoIpCityStatusText = hasCreds
                    ? "Active (GeoLite2-City.mmdb)"
                    : "Active (Pre-bundled GeoLite2-City.mmdb)";
            }
            else
            {
                GeoIpCityStatusText = "Missing / Not Available";
            }

            if (GeoIpService.IsCountryDbLoaded)
            {
                GeoIpCountryStatusText = hasCreds
                    ? "Active (GeoLite2-Country.mmdb)"
                    : "Active (Pre-bundled GeoLite2-Country.mmdb)";
            }
            else
            {
                GeoIpCountryStatusText = "Missing / Not Available";
            }

            var lastMod = GeoIpService.CityDbLastModified ?? GeoIpService.CountryDbLastModified;
            GeoIpLastUpdatedText = lastMod.HasValue ? lastMod.Value.ToString("yyyy-MM-dd HH:mm UTC", CultureInfo.InvariantCulture) : "Bundled / Initial";
            IsGeoIpUpdating = GeoIpService.IsUpdating;

            AppLogger.Debug($"[SettingsViewModel] Refreshed GeoIP status UI: City='{GeoIpCityStatusText}', Country='{GeoIpCountryStatusText}', Date='{GeoIpLastUpdatedText}'");
        });
    }

    [RelayCommand]
    private Task<bool> UpdateGeoIpDatabasesAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(Settings.MaxMindAccountId) || string.IsNullOrWhiteSpace(Settings.MaxMindLicenseKey))
            {
                AppLogger.Info("[SettingsViewModel] Manual GeoIP update requested without credentials configured.");
                ToastNotificationService.Instance.ShowToast(
                    "Credentials Required for Updates",
                    "Currently using offline pre-bundled databases. To download newer updates, enter your MaxMind Account ID & License Key.",
                    "GEOIP_NOTICE"
                );
                return;
            }

            await SaveSettingsAsync();
            IsGeoIpUpdating = true;
            AppLogger.Info("[SettingsViewModel] Initiating forced GeoIP database download from MaxMind...");
            try
            {
                await GeoIpService.UpdateDatabasesAsync(force: true);
            }
            finally
            {
                IsGeoIpUpdating = false;
                RefreshGeoIpStatus();
            }
        });
    }

    [RelayCommand]
    private void ClearDatabase()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[SettingsViewModel] Prompting confirmation for SQLite database purge...");

            _dashboard?.ShowDialog(new ConfirmDialogViewModel(
                "Clear SQLite Database",
                "Are you sure you want to permanently clear all historical player records and alias tables from SQLite?",
                "Clear Database",
                true,
                async () =>
                {
                    AppLogger.Warn("[SettingsViewModel] Confirmed SQLite database purge. Purging data tables...");
                    await PlayerDatabaseStorageService.ClearAsync();
                    await RefreshDatabaseStatsAsync();
                    ToastNotificationService.Instance.ShowToast("Database Cleared", "SQLite historical database purged.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }
}