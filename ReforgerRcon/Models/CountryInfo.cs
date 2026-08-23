using Avalonia.Media.Imaging;
using ReforgerRcon.Services;
using System;

namespace ReforgerRcon.Models;

public class CountryInfo
{
    private string _code = "xx";

    public string Code
    {
        get => _code;
        set => _code = string.IsNullOrWhiteSpace(value) ? "xx" : value.Trim().ToLowerInvariant();
    }

    public string Name { get; set; } = "Unknown Region";

    public Bitmap? FlagImage => FlagAssetService.GetFlag(Code);

    public string FlagUrl => $"avares://ReforgerRcon/Assets/flags/{ResolveFlagFileName(Code, Name)}.svg";

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

        // Avoid confusing Unknown Region with the United Nations
        if (normalized == "un" && !string.Equals(name?.Trim(), "United Nations", StringComparison.OrdinalIgnoreCase))
        {
            return "xx";
        }

        return normalized;
    }
}