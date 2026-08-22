using System;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReforgerRcon.Models;

public partial class ServerProfile : ObservableObject
{
    [ObservableProperty] public partial Guid Id { get; set; } = Guid.NewGuid();
    [ObservableProperty] public partial string Name { get; set; } = "Official Server";
    [ObservableProperty] public partial string ServerIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int Port { get; set; } = 19999;
    [ObservableProperty] public partial string Password { get; set; } = string.Empty;
    [ObservableProperty] public partial RconProtocol Protocol { get; set; } = RconProtocol.ReforgerBuiltIn;
    [ObservableProperty] public partial bool AutoConnect { get; set; }
    [ObservableProperty] public partial bool IsLastSelected { get; set; }

    [JsonIgnore]
    [ObservableProperty] public partial bool IsEditing { get; set; }

    [JsonIgnore]
    [ObservableProperty] public partial string EditNameBuffer { get; set; } = string.Empty;

    public string ProtocolShortName => Protocol == RconProtocol.ReforgerBuiltIn ? "Reforger" : "BattlEye";
    public string FormattedEndpoint => $"{ServerIp}:{Port}";
}