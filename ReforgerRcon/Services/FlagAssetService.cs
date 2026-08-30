using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace ReforgerRcon.Services;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Avalonia internal avares resource schema paths")]
public static class FlagAssetService
{
    private const string FlagUriPrefix = "avares://ReforgerRcon/Assets/flags/";
    private static readonly string[] CommonCountryCodes = ["xx", "un", "us", "de", "gb", "fr", "ca", "au", "ru", "il", "pl", "cz", "nl", "se", "no", "fi", "es", "it", "br", "jp", "kr", "cn"];
    private static readonly ConcurrentDictionary<string, Bitmap?> FlagCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock RasterizeLock = new();
    private static bool _isPrewarmed;

    public static void PrewarmCache()
    {
        if (_isPrewarmed) return;
        _isPrewarmed = true;

        _ = Task.Run(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                foreach (var code in CommonCountryCodes)
                {
                    GetFlag(code);
                }
                sw.Stop();
                AppLogger.Debug($"[FlagAssetService] Background SVG engine and flag pre-warming completed in {sw.ElapsedMilliseconds} ms ({FlagCache.Count} flags cached).");
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.Debug($"[FlagAssetService] Asset loader not ready during prewarm: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"[FlagAssetService] Background warmup notice: {ex.Message}");
            }
        }, CancellationToken.None);
    }

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
        try
        {
            var primaryUri = GetFlagUri(code);
            if (AssetLoader.Exists(primaryUri))
            {
                return RasterizeSvgUri(primaryUri);
            }

            var fallbackXxUri = GetFlagUri("xx");
            if (AssetLoader.Exists(fallbackXxUri))
            {
                return RasterizeSvgUri(fallbackXxUri);
            }

            var fallbackUnUri = GetFlagUri("un");
            if (AssetLoader.Exists(fallbackUnUri))
            {
                return RasterizeSvgUri(fallbackUnUri);
            }
        }
        catch (InvalidOperationException)
        {
            // AssetLoader is not yet registered by Avalonia framework
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[FlagAssetService] Failed loading SVG for code '{code}': {ex.Message}");
        }

        return null;
    }

    private static Bitmap? RasterizeSvgUri(Uri uri)
    {
        lock (RasterizeLock)
        {
            try
            {
                using var stream = AssetLoader.Open(uri);
                using var svg = new SKSvg();
                var picture = svg.Load(stream);

                if (picture == null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
                {
                    return null;
                }

                const int targetWidth = 64;
                const int targetHeight = 44;

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
                using var data = skImage.Encode(SKEncodedImageFormat.Png, 90);
                using var memStream = new MemoryStream(data.ToArray());

                return new Bitmap(memStream);
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[FlagAssetService] Rasterize notice for '{uri}': {ex.Message}");
                return null;
            }
        }
    }
}