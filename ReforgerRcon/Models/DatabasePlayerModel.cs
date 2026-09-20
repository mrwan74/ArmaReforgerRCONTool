using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using ReforgerRcon.Services;

namespace ReforgerRcon.Models;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Reads generated partial properties")]
public partial class DatabasePlayerModel : ObservableObject
{
    [ObservableProperty] public partial int Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Uid { get; set; } = string.Empty;
    [ObservableProperty] public partial string Guid { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReforgerUid { get; set; } = string.Empty;
    [ObservableProperty] public partial string BattlEyeGuid { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastIpPort { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    public partial int Ping { get; set; }

    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial string Comment { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchlistActionText))]
    public partial bool IsWatchlisted { get; set; }

    [ObservableProperty] public partial bool HasAliases { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial DateTime LastSeen { get; set; } = DateTime.UtcNow;
    [ObservableProperty] public partial List<string> Aliases { get; set; } = [];
    [ObservableProperty] public partial CountryInfo Country { get; set; } = new() { Code = "xx", Name = "Unknown Region" };
    [ObservableProperty] public partial string Location { get; set; } = string.Empty;
    [ObservableProperty] public partial string TimeZone { get; set; } = string.Empty;

    public bool HasReforgerUid => !string.IsNullOrWhiteSpace(ReforgerUid);
    public bool HasBattlEyeGuid => !string.IsNullOrWhiteSpace(BattlEyeGuid);

    public string DisplayReforgerUid => ResolveDisplayReforgerUid();
    public string DisplayBattlEyeGuid => ResolveDisplayBattlEyeGuid();

    private string ResolveDisplayReforgerUid()
    {
        if (!string.IsNullOrWhiteSpace(ReforgerUid))
        {
            return ReforgerUid;
        }

        if (!string.IsNullOrWhiteSpace(Uid))
        {
            return Uid;
        }

        return "N/A";
    }

    private string ResolveDisplayBattlEyeGuid()
    {
        if (!string.IsNullOrWhiteSpace(BattlEyeGuid))
        {
            return BattlEyeGuid;
        }

        if (!string.IsNullOrWhiteSpace(Guid))
        {
            return Guid;
        }

        if (!string.IsNullOrWhiteSpace(Uid) && !Uid.Contains('-'))
        {
            return Uid;
        }

        return "N/A";
    }

    public string FormattedEndpoint => !string.IsNullOrWhiteSpace(LastIpPort) && !LastIpPort.Equals("N/A", StringComparison.OrdinalIgnoreCase) ? LastIpPort : "N/A";
    public string FormattedLocalTime => LocationFormatter.FormatLocalTime(TimeZone);
    public string PingDisplay => Ping < 0 ? $"{Ping}" : $"{Ping} ms";
    public string WatchlistActionText => IsWatchlisted ? "Remove from Watchlist" : "Add to Watchlist";
}