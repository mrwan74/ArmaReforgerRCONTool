using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ReforgerRcon.Services;

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

    partial void OnDurationSecondsChanged(long value)
    {
        AppLogger.Trace($"[BanModel:State] Ban #{BanNumber} ({IdentityId}) DurationSeconds mutated to {value}s (Expiry: {ExpirationText}, Remaining: {MinutesLeftText}).");
    }

    partial void OnIsSelectedChanged(bool value)
    {
        AppLogger.Trace($"[BanModel:Selection] Ban #{BanNumber} ({IdentityId}, Name='{BannedName}') IsSelected changed to {value}.");
    }

    public string ExpirationText
    {
        get
        {
            try
            {
                if (DurationSeconds == 0)
                {
                    return "Permanent";
                }

                if (DurationSeconds < 0)
                {
                    return "Expired";
                }

                var expiryUtc = BannedAt.AddSeconds(DurationSeconds);
                return expiryUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            }
            catch (ArgumentOutOfRangeException outOfRangeEx)
            {
                AppLogger.Warn($"[BanModel:ExpirationText] Calculation overflow for ban #{BanNumber} ({IdentityId}, Duration={DurationSeconds}s, BannedAt={BannedAt:O}): {outOfRangeEx.Message}");
                return "Out of Range";
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[BanModel:ExpirationText] Unexpected error formatting expiration date for ban #{BanNumber} ({IdentityId}): {ex.Message}", ex);
                return "Calculation Error";
            }
        }
    }

    public string MinutesLeftText
    {
        get
        {
            try
            {
                if (DurationSeconds == 0)
                {
                    return "Permanent";
                }

                if (DurationSeconds < 0)
                {
                    return "Expired";
                }

                if (DurationSeconds < 60)
                {
                    return "< 1 min";
                }

                return string.Create(CultureInfo.InvariantCulture, $"{(int)(DurationSeconds / 60)} min");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[BanModel:MinutesLeftText] Unexpected error formatting minutes left for ban #{BanNumber} ({IdentityId}): {ex.Message}", ex);
                return "Unknown";
            }
        }
    }
}