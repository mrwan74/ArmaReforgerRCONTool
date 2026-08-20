using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using System;
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
    [ObservableProperty] public partial bool IsGeoIpUpdating { get; set; }
    [ObservableProperty] public partial string GeoIpCityStatusText { get; set; } = "Not Loaded";
    [ObservableProperty] public partial string GeoIpCountryStatusText { get; set; } = "Not Loaded";
    [ObservableProperty] public partial string GeoIpLastUpdatedText { get; set; } = "Never";
    [ObservableProperty] public partial bool IsLicenseKeyRevealed { get; set; }

    public char LicenseKeyMaskChar => IsLicenseKeyRevealed ? '\0' : '•';
    public MaterialIconKind LicenseKeyIconKind => IsLicenseKeyRevealed ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public SettingsViewModel(DashboardViewModel? dashboard = null)
    {
        _dashboard = dashboard;
        LoadSettings();
        GeoIpService.DatabasesUpdated += RefreshGeoIpStatus;
        RefreshGeoIpStatus();
    }

    private void LoadSettings()
    {
        ExecuteSafe(() =>
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                var (acc, key) = GeoIpService.ResolveCredentials();
                Settings.MaxMindAccountId = acc;
                Settings.MaxMindLicenseKey = key;
            }
        }, "Failed to load application settings from disk.");
    }

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

            AppLogger.Info("[SettingsViewModel] Application settings saved to disk.");
            ToastNotificationService.Instance.ShowToast("Settings Saved", "Preferences and MaxMind credentials updated.");
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
        });
    }

    [RelayCommand]
    private Task<bool> UpdateGeoIpDatabasesAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            if (string.IsNullOrWhiteSpace(Settings.MaxMindAccountId) || string.IsNullOrWhiteSpace(Settings.MaxMindLicenseKey))
            {
                ToastNotificationService.Instance.ShowToast(
                    "Credentials Required for Updates",
                    "Currently using offline pre-bundled databases. To download newer updates, enter your MaxMind Account ID & License Key.",
                    "GEOIP_NOTICE"
                );
                return;
            }

            await SaveSettingsAsync();
            IsGeoIpUpdating = true;
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
            _dashboard?.ShowDialog(new ConfirmDialogViewModel(
                "Clear Player Database",
                "Are you sure you want to permanently clear all historical player entries?",
                "Clear Database",
                true,
                async () =>
                {
                    await PlayerDatabaseStorageService.ClearAsync();
                    ToastNotificationService.Instance.ShowToast("Database Cleared", "Historical database purged.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }
}