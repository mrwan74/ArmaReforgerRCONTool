using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ReforgerRcon.Services;
using System;

namespace ReforgerRcon.Models;

public partial class CountryInfo : ObservableObject
{
    private string _code = "xx";

    public string Code
    {
        get => _code;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "xx" : value.Trim().ToLowerInvariant();
            if (SetProperty(ref _code, normalized))
            {
                OnPropertyChanged(nameof(FlagImage));
                OnPropertyChanged(nameof(FlagUrl));
            }
        }
    }

    [ObservableProperty]
    public partial string Name { get; set; } = "Unknown Region";

    public Bitmap? FlagImage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_code) || _code is "xx" or "unknown" or "?" or "-")
            {
                return null;
            }

            return FlagAssetService.GetFlag(_code);
        }
    }

    public string FlagUrl => $"avares://ReforgerRcon/Assets/flags/{ResolveFlagFileName(Code, Name)}.svg";

    public CountryInfo()
    {
        FlagAssetService.FlagLoaded += OnFlagLoaded;
    }

    private void OnFlagLoaded(string loadedCode)
    {
        if (string.Equals(_code, loadedCode, StringComparison.OrdinalIgnoreCase))
        {
            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(FlagImage)));
        }
    }

    private static string ResolveFlagFileName(string? code, string? name)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "xx";
        }

        var normalized = code.Trim().ToLowerInvariant();

        if (normalized is "unknown" or "?" or "-" or "xx")
        {
            return "xx";
        }

        if (normalized == "un" && !string.Equals(name?.Trim(), "United Nations", StringComparison.OrdinalIgnoreCase))
        {
            return "xx";
        }

        return normalized;
    }
}