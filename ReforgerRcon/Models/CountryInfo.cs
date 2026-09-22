using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ReforgerRcon.Services;
using System;

namespace ReforgerRcon.Models;

public partial class CountryInfo : ObservableObject
{
    private static readonly string RawAssemblyName = typeof(CountryInfo).Assembly.GetName().Name ?? "ARMA REFORGER RCON TOOL";
    private static readonly string EscapedAssemblyName = Uri.EscapeDataString(RawAssemblyName);
    private string _code = FlagAssetService.UnknownCountryCode;

    public string Code
    {
        get => _code;
        set
        {
            var normalized = FlagAssetService.NormalizeCountryCode(value);

            if (normalized == "un" && !string.Equals(Name?.Trim(), "United Nations", StringComparison.OrdinalIgnoreCase))
            {
                normalized = FlagAssetService.UnknownCountryCode;
            }

            if (SetProperty(ref _code, normalized))
            {
                OnPropertyChanged(nameof(FlagImage));
                OnPropertyChanged(nameof(FlagUrl));
                OnPropertyChanged(nameof(UpperCode));
                OnPropertyChanged(nameof(IsKnownCountry));
            }
        }
    }

    [ObservableProperty]
    public partial string Name { get; set; } = LocationFormatter.UnknownRegion;

    public string UpperCode => _code.ToUpperInvariant();

    public bool IsKnownCountry => _code != FlagAssetService.UnknownCountryCode;

    public Bitmap? FlagImage => FlagAssetService.GetFlag(_code);

    public string FlagUrl => $"avares://{EscapedAssemblyName}/Assets/flags/{_code}.svg";

    public CountryInfo()
    {
        // Zero-leak model: Bitmaps are resolved and cached on-demand in FlagAssetService
    }
}