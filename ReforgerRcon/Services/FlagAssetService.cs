using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Avalonia internal avares resource schema paths")]
public static class FlagAssetService
{
    private const string FlagUriPrefix = "avares://ReforgerRcon/Assets/flags/";
    private static readonly ConcurrentDictionary<string, WriteableBitmap?> FlagCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlightRasterizations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock RasterizeLock = new();

    private static readonly string[] StartupEssentialCodes =
    [
        "un", "us", "de", "gb", "fr", "ca", "au", "ru",
        "jp", "nl", "pl", "se", "no", "es", "it", "br",
        "cz", "fi", "at", "ch", "ua"
    ];

    public static void PrewarmCommonFlags()
    {
        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            int count = 0;
            try
            {
                foreach (var code in StartupEssentialCodes)
                {
                    if ((!FlagCache.TryGetValue(code, out var existing) || existing == null) &&
                        LoadSvgToBitmap(code) is { } bmp &&
                        FlagCache.TryAdd(code, bmp))
                    {
                        count++;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[FlagAssetService:Prewarm] Notice: {ex.Message}");
            }
            sw.Stop();
            AppLogger.Debug($"[FlagAssetService:Prewarm] Pre-warmed {count} vector flags and initialized Skia rendering pipeline in {sw.ElapsedMilliseconds}ms.");
        });
    }

    public static void PrewarmFlag(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return;
        var normalized = NormalizeCountryCode(countryCode);
        if (normalized is "xx" or "unknown" || FlagCache.ContainsKey(normalized)) return;

        if (InFlightRasterizations.TryAdd(normalized, 0))
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (LoadSvgToBitmap(normalized) is { } bmp)
                    {
                        FlagCache.TryAdd(normalized, bmp);
                    }
                }
                finally
                {
                    InFlightRasterizations.TryRemove(normalized, out _);
                }
            });
        }
    }

    public static Bitmap? GetFlag(string? countryCode)
    {
        var code = NormalizeCountryCode(countryCode);
        if (code is "xx" or "unknown" or "?" or "-")
        {
            return null;
        }

        if (FlagCache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            PrewarmFlag(code);
            return FlagCache.GetValueOrDefault("un");
        }

        lock (RasterizeLock)
        {
            if (FlagCache.TryGetValue(code, out cached))
            {
                return cached;
            }

            var bitmap = LoadSvgToBitmap(code);
            FlagCache.TryAdd(code, bitmap);
            return bitmap;
        }
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
                return RasterizeSvgUri(primaryUri, code);
            }

            var fallbackUnUri = GetFlagUri("un");
            if (AssetLoader.Exists(fallbackUnUri))
            {
                return RasterizeSvgUri(fallbackUnUri, "un");
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

    private static WriteableBitmap? RasterizeSvgUri(Uri uri, string code)
    {
        if (FlagCache.TryGetValue(code, out var existing) && existing != null)
        {
            return existing;
        }

        try
        {
            using var stream = AssetLoader.Open(uri);
            using var svg = new SKSvg();
            var picture = svg.Load(stream);

            if (picture == null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            {
                return null;
            }

            const int targetWidth = 48;
            const int targetHeight = 32;

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