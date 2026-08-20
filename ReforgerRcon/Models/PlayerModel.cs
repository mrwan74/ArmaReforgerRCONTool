using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReforgerRcon.Models;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Reads generated partial properties")]
public partial class PlayerModel : ObservableObject
{
    [ObservableProperty] public partial int Id { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Uid { get; set; } = string.Empty;
    [ObservableProperty] public partial string Guid { get; set; } = string.Empty;
    [ObservableProperty] public partial string Ip { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int Port { get; set; } = 2304;
    [ObservableProperty] public partial int Ping { get; set; } = 25;
    [ObservableProperty] public partial string Comment { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsWatchlisted { get; set; }
    [ObservableProperty] public partial bool HasAliases { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial CountryInfo Country { get; set; } = new() { Code = "us", Name = "United States" };
    [ObservableProperty] public partial List<string> Aliases { get; set; } = [];
    [ObservableProperty] public partial string LocationCity { get; set; } = "Frankfurt";
    [ObservableProperty] public partial string LocationState { get; set; } = "Hesse";

    public string FormattedEndpoint => $"{Ip}:{Port}";
    public string DisplayLocation => $"{LocationCity}, {LocationState}, {Country.Name}";
}