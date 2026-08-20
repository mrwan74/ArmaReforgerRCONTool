using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ReforgerRcon.Models;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Reads generated partial properties")]
public partial class BanModel : ObservableObject
{
    [ObservableProperty] public partial int BanNumber { get; set; }
    [ObservableProperty] public partial string IdentityId { get; set; } = string.Empty;
    [ObservableProperty] public partial string BannedName { get; set; } = string.Empty;
    [ObservableProperty] public partial string Reason { get; set; } = string.Empty;
    [ObservableProperty] public partial DateTime BannedAt { get; set; } = DateTime.UtcNow;
    [ObservableProperty] public partial long DurationSeconds { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    public string ExpirationText => DurationSeconds <= 0
        ? "Permanent"
        : BannedAt.AddSeconds(DurationSeconds).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public string MinutesLeftText => DurationSeconds <= 0
        ? "Permanent"
        : string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, (int)(DurationSeconds / 60))} min");
}