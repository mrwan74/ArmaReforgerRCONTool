namespace ReforgerRcon.Models;

public class AppSettings
{
    public bool AudioAlerts { get; set; } = false;
    public bool ToastNotifications { get; set; } = true;
    public bool PushNotifications { get; set; } = false;

    public bool AlertOnJoin { get; set; } = false;
    public bool AlertOnLeave { get; set; } = false;
    public bool AlertOnWatchlistJoin { get; set; } = false;
    public bool AlertOnWatchlistLeave { get; set; } = false;

    public int RefreshIntervalSeconds { get; set; } = 15;
    public bool AutoRefreshBans { get; set; } = true;
    public bool RunInBackground { get; set; } = true;

    public bool EnableWindowGlass { get; set; } = false;

    public string MaxMindAccountId { get; set; } = string.Empty;
    public string MaxMindLicenseKey { get; set; } = string.Empty;
    public bool AutoUpdateGeoIpOnStartup { get; set; } = true;

    // Independent Tab Sorting Configurations
    public string PlayersSortBy { get; set; } = "Default";
    public bool PlayersSortAscending { get; set; } = true;

    public string BansSortBy { get; set; } = "Default";
    public bool BansSortAscending { get; set; } = true;

    public string DatabaseSortBy { get; set; } = "Default";
    public bool DatabaseSortAscending { get; set; } = true;
}