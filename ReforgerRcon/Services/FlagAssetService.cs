using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
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
    private static readonly ConcurrentDictionary<string, WriteableBitmap?> FlagCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock RasterizeLock = new();

    public static Bitmap? GetFlag(string? countryCode)
    {
        var code = NormalizeCountryCode(countryCode);

        if (FlagCache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        var bitmap = LoadSvgToBitmap(code);
        FlagCache[code] = bitmap;
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Trace($"[FlagAssetService:Get] Loaded flag for '{code}' in {elapsedMs:F2}ms.");
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

    private static WriteableBitmap? LoadSvgToBitmap(string code)
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
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[FlagAssetService:Load] Failed loading SVG for '{code}': {ex.Message}");
        }

        return null;
    }

    private static WriteableBitmap? RasterizeSvgUri(Uri uri)
    {
        var start = Stopwatch.GetTimestamp();
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

                var writeableBitmap = new WriteableBitmap(
                    new Avalonia.PixelSize(targetWidth, targetHeight),
                    new Avalonia.Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    Avalonia.Platform.AlphaFormat.Premul);

                var pixelBytes = skBitmap.Bytes;
                using (var frameBuffer = writeableBitmap.Lock())
                {
                    Marshal.Copy(pixelBytes, 0, frameBuffer.Address, pixelBytes.Length);
                }

                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Trace($"[FlagAssetService:Rasterize] Fast rasterized '{uri}' in {elapsedMs:F2}ms.");
                return writeableBitmap;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[FlagAssetService:Rasterize] Notice for '{uri}': {ex.Message}");
                return null;
            }
        }
    }
}