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

    public string MaxMindAccountId { get; set; } = string.Empty;
    public string MaxMindLicenseKey { get; set; } = string.Empty;
    public bool AutoUpdateGeoIpOnStartup { get; set; } = true;
}