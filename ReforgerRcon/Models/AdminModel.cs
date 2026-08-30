using CommunityToolkit.Mvvm.ComponentModel;
using ReforgerRcon.Services;

namespace ReforgerRcon.Models;

public partial class AdminModel : ObservableObject
{
    [ObservableProperty] public partial int Id { get; set; }
    [ObservableProperty] public partial string Ip { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int Port { get; set; }
    [ObservableProperty] public partial CountryInfo Country { get; set; } = new() { Code = "un", Name = "Unknown Region" };
    [ObservableProperty] public partial string Location { get; set; } = "Direct Network";
    [ObservableProperty] public partial string TimeZone { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsCurrentSession { get; set; }

    public string FormattedEndpoint => $"{Ip}:{Port}";
    public string FormattedLocalTime => LocationFormatter.FormatLocalTime(TimeZone);

    public string GetFullDiagnosticInfo() =>
        $"RCON Admin #{Id}{(IsCurrentSession ? " (Current Session / You)" : "")} | Endpoint: {FormattedEndpoint} | Location: {Location} | Local Time: {FormattedLocalTime}";
}