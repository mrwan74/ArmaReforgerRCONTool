using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace ReforgerRcon.Services;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Avalonia internal avares resource schema paths")]
public static class FlagAssetService
{
    private const string FlagUriPrefix = "avares://ReforgerRcon/Assets/flags/";
    private static readonly ConcurrentDictionary<string, Bitmap?> FlagCache = new(StringComparer.OrdinalIgnoreCase);

    public static Bitmap? GetFlag(string? countryCode)
    {
        var code = NormalizeCountryCode(countryCode);

        if (FlagCache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var bitmap = LoadSvgToBitmap(code);
        FlagCache[code] = bitmap;
        return bitmap;
    }

    private static string NormalizeCountryCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "xx";
        var normalized = code.Trim().ToLowerInvariant();
        return normalized switch
        {
            "unknown" or "?" or "-" => "xx",
            _ => normalized
        };
    }

    private static Uri GetFlagUri(string code) => new($"{FlagUriPrefix}{code}.svg");

    private static Bitmap? LoadSvgToBitmap(string code)
    {
        var primaryUri = GetFlagUri(code);
        if (AssetLoader.Exists(primaryUri))
        {
            return RasterizeSvgUri(primaryUri, code);
        }

        var fallbackXxUri = GetFlagUri("xx");
        if (AssetLoader.Exists(fallbackXxUri))
        {
            AppLogger.Trace($"[FlagAssetService] Flag asset '{code}.svg' not found. Falling back to unknown flag 'xx.svg'.");
            return RasterizeSvgUri(fallbackXxUri, "xx");
        }

        var fallbackUnUri = GetFlagUri("un");
        if (AssetLoader.Exists(fallbackUnUri))
        {
            AppLogger.Trace($"[FlagAssetService] Flag asset '{code}.svg' and 'xx.svg' not found. Secondary fallback to 'un.svg'.");
            return RasterizeSvgUri(fallbackUnUri, "un");
        }

        AppLogger.Warn($"[FlagAssetService] Unable to locate flag asset for code '{code}' or default fallback.");
        return null;
    }

    private static Bitmap? RasterizeSvgUri(Uri uri, string code)
    {
        try
        {
            using var stream = AssetLoader.Open(uri);
            using var svg = new SKSvg();
            var picture = svg.Load(stream);

            if (picture == null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            {
                AppLogger.Warn($"[FlagAssetService] Loaded SVG picture is empty for '{code}'.");
                return null;
            }

            const int targetWidth = 128;
            const int targetHeight = 88;

            using var skBitmap = new SKBitmap(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(skBitmap))
            {
                canvas.Clear(SKColors.Transparent);
                var scaleX = (float)targetWidth / picture.CullRect.Width;
                var scaleY = (float)targetHeight / picture.CullRect.Height;
                canvas.Scale(scaleX, scaleY);
                canvas.DrawPicture(picture);
            }

            using var skImage = SKImage.FromBitmap(skBitmap);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            using var memStream = new MemoryStream(data.ToArray());

            var avaloniaBitmap = new Bitmap(memStream);
            AppLogger.Trace($"[FlagAssetService] Rasterized and cached SVG flag for '{code}' ({targetWidth}x{targetHeight} px).");
            return avaloniaBitmap;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[FlagAssetService] Error rasterizing SVG flag for '{code}' from '{uri}': {ex.Message}", ex);
        }

        return null;
    }
}