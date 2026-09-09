using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;
using ReforgerRcon.Services;

namespace ReforgerRcon.Models;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Reads generated partial properties")]
public partial class PlayerModel : ObservableObject
{
    [ObservableProperty] public partial int Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Uid { get; set; } = string.Empty;
    [ObservableProperty] public partial string Guid { get; set; } = string.Empty;
    [ObservableProperty] public partial string ReforgerUid { get; set; } = string.Empty;
    [ObservableProperty] public partial string BattlEyeGuid { get; set; } = string.Empty;
    [ObservableProperty] public partial string Ip { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int Port { get; set; } = 2304;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PingDisplay))]
    public partial int Ping { get; set; }

    [ObservableProperty] public partial string Comment { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchlistActionText))]
    public partial bool IsWatchlisted { get; set; }

    [ObservableProperty] public partial bool HasAliases { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial CountryInfo Country { get; set; } = new() { Code = "xx", Name = "Unknown Region" };
    [ObservableProperty] public partial List<string> Aliases { get; set; } = [];
    [ObservableProperty] public partial string LocationCity { get; set; } = string.Empty;
    [ObservableProperty] public partial string LocationState { get; set; } = string.Empty;
    [ObservableProperty] public partial string DisplayLocation { get; set; } = string.Empty;
    [ObservableProperty] public partial string TimeZone { get; set; } = string.Empty;

    public bool HasReforgerUid => !string.IsNullOrWhiteSpace(ReforgerUid) && ReforgerUid.Length == 36 && ReforgerUid.Contains('-');
    public bool HasBattlEyeGuid => !string.IsNullOrWhiteSpace(BattlEyeGuid) && BattlEyeGuid.Length == 32 && !BattlEyeGuid.Contains('-');

    public string FormattedEndpoint => $"{Ip}:{Port}";
    public string FormattedLocalTime => LocationFormatter.FormatLocalTime(TimeZone);
    public string WatchlistActionText => IsWatchlisted ? "Remove from Watchlist" : "Add to Watchlist";
    public string PingDisplay => Ping < 0 ? $"{Ping}" : $"{Ping} ms";
}