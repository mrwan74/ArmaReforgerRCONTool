using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public static class FlagAssetService
{
    public const string UnknownCountryCode = "xx";
    private const string FlagsDirectoryName = "flags";
    private const int TargetWidth = 48;
    private const int TargetHeight = 32;

    private static readonly Assembly CurrentAssembly = typeof(FlagAssetService).Assembly;
    private static readonly string RawAssemblyName = CurrentAssembly.GetName().Name ?? "ARMA REFORGER RCON TOOL";
    private static readonly string EscapedAssemblyName = Uri.EscapeDataString(RawAssemblyName);
    private static readonly ConcurrentDictionary<string, WriteableBitmap?> FlagCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> InFlightRasterizations = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] StartupEssentialCodes =
    [
        UnknownCountryCode, "us", "de", "gb", "fr", "ru", "pl", "ca", "au", "cz", "nl", "se", "no", "fi", "es", "it", "jp", "cn", "br", "ua", "tr", "at", "ch"
    ];

    public static event Action<string>? FlagRasterized;

    public static void PrewarmCommonFlags()
    {
        _ = Task.Run(() =>
        {
            foreach (var code in StartupEssentialCodes)
            {
                FlagCache.GetOrAdd(code, static c => LoadFlagBitmap(c));
            }

            try
            {
                var flagsDir = Path.Combine(AppContext.BaseDirectory, "Assets", FlagsDirectoryName);
                if (!Directory.Exists(flagsDir))
                {
                    flagsDir = Path.Combine(AppContext.BaseDirectory, FlagsDirectoryName);
                }

                if (Directory.Exists(flagsDir))
                {
                    foreach (var file in Directory.EnumerateFiles(flagsDir, "*.svg"))
                    {
                        var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                        FlagCache.GetOrAdd(code, static c => LoadFlagBitmap(c));
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[FlagAssetService:Prewarm] Background directory scan notice: {ex.Message}");
            }
        });
    }

    public static void PrewarmFlag(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return;
        var code = NormalizeCountryCode(countryCode);
        if (FlagCache.ContainsKey(code)) return;

        QueueBackgroundRasterize(code);
    }

    public static WriteableBitmap? GetFlag(string? countryCode)
    {
        var code = NormalizeCountryCode(countryCode);

        if (FlagCache.TryGetValue(code, out var cached) && cached != null)
        {
            return cached;
        }

        QueueBackgroundRasterize(code);
        return FlagCache.GetValueOrDefault(UnknownCountryCode);
    }

    private static void QueueBackgroundRasterize(string code)
    {
        if (!InFlightRasterizations.TryAdd(code, 1)) return;

        _ = Task.Run(() =>
        {
            try
            {
                var bitmap = LoadFlagBitmap(code);
                FlagCache[code] = bitmap;
                FlagRasterized?.Invoke(code);
            }
            finally
            {
                InFlightRasterizations.TryRemove(code, out _);
            }
        });
    }

    public static string NormalizeCountryCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return UnknownCountryCode;
        var normalized = code.Trim().ToLowerInvariant();
        return normalized switch
        {
            "unknown" or "?" or "-" or "xx" => UnknownCountryCode,
            _ => normalized
        };
    }

    private static WriteableBitmap? LoadFlagBitmap(string code)
    {
        try
        {
            using var stream = OpenFlagStream(code);
            if (stream != null)
            {
                return RenderSvgToAvaloniaBitmap(stream);
            }

            if (code != UnknownCountryCode && FlagCache.TryGetValue(UnknownCountryCode, out var fallback))
            {
                return fallback;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[FlagAssetService:Load] Failed loading flag for '{code}': {ex.Message}");
        }

        return null;
    }

    private static Stream? OpenFlagStream(string code)
    {
        var primaryUri = new Uri($"avares://{EscapedAssemblyName}/Assets/{FlagsDirectoryName}/{code}.svg");
        if (AssetLoader.Exists(primaryUri))
        {
            return AssetLoader.Open(primaryUri);
        }

        var diskPath = Path.Combine(AppContext.BaseDirectory, "Assets", FlagsDirectoryName, $"{code}.svg");
        if (File.Exists(diskPath))
        {
            return File.OpenRead(diskPath);
        }

        var diskPathAlt = Path.Combine(AppContext.BaseDirectory, FlagsDirectoryName, $"{code}.svg");
        if (File.Exists(diskPathAlt))
        {
            return File.OpenRead(diskPathAlt);
        }

        return null;
    }

    private static WriteableBitmap? RenderSvgToAvaloniaBitmap(Stream stream)
    {
        try
        {
            using var svg = new SKSvg();
            var picture = svg.Load(stream);

            if (picture == null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            {
                return null;
            }

            var writeableBmp = new WriteableBitmap(
                new PixelSize(TargetWidth, TargetHeight),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Premul);

            using (var fb = writeableBmp.Lock())
            {
                var imageInfo = new SKImageInfo(TargetWidth, TargetHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(imageInfo, fb.Address, fb.RowBytes);
                var canvas = surface.Canvas;

                canvas.Clear(SKColors.Transparent);
                var scaleX = (float)TargetWidth / picture.CullRect.Width;
                var scaleY = (float)TargetHeight / picture.CullRect.Height;
                canvas.Scale(scaleX, scaleY);
                canvas.DrawPicture(picture);
                canvas.Flush();
            }

            return writeableBmp;
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[FlagAssetService:Render] Direct SVG rasterization notice: {ex.Message}");
            return null;
        }
    }
}