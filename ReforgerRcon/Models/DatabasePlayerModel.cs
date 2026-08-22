using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReforgerRcon.Models;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Reads generated partial properties")]
public partial class DatabasePlayerModel : ObservableObject
{
    [ObservableProperty] public partial int Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Uid { get; set; } = string.Empty;
    [ObservableProperty] public partial string Guid { get; set; } = string.Empty;
    [ObservableProperty] public partial string LastIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int LastPort { get; set; } = 2304;
    [ObservableProperty] public partial int Ping { get; set; } = 25;
    [ObservableProperty] public partial bool IsOnline { get; set; }
    [ObservableProperty] public partial string Comment { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WatchlistActionText))]
    public partial bool IsWatchlisted { get; set; }

    [ObservableProperty] public partial bool HasAliases { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial DateTime LastSeen { get; set; } = DateTime.UtcNow;
    [ObservableProperty] public partial List<string> Aliases { get; set; } = [];
    [ObservableProperty] public partial CountryInfo Country { get; set; } = new() { Code = "us", Name = "United States" };
    [ObservableProperty] public partial string Location { get; set; } = "New York, USA";

    public string FormattedEndpoint => $"{LastIp}:{LastPort}";
    public string PingDisplay => IsOnline ? $"{Ping} ms" : "Offline";
    public string WatchlistActionText => IsWatchlisted ? "Remove from Watchlist" : "Add to Watchlist";
}