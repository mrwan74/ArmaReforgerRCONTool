using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public static class FlagAssetService
{
    public const string UnknownCountryCode = "xx";
    private const string FlagsFolderName = "flags";
    private const string AssetsFolderName = "Assets";
    private const string SvgExtension = ".svg";
    private const string PngExtension = ".png";

    private static readonly Assembly CurrentAssembly = typeof(FlagAssetService).Assembly;
    private static readonly string RawAssemblyName = CurrentAssembly.GetName().Name ?? "ARMA REFORGER RCON TOOL";
    private static readonly string EscapedAssemblyName = Uri.EscapeDataString(RawAssemblyName);
    private static readonly ConcurrentDictionary<string, Bitmap?> FlagCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock RasterizeLock = new();

    private static readonly string[] StartupEssentialCodes =
    [
        UnknownCountryCode, "us", "de", "gb", "fr", "ru", "pl", "ca", "au", "cz", "nl", "se", "no", "fi", "es", "it", "jp", "cn", "br", "il", "ua", "tr", "at", "ch", "be"
    ];

    static FlagAssetService()
    {
        try
        {
            AssetLoader.SetDefaultAssembly(CurrentAssembly);
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[FlagAssetService:Init] SetDefaultAssembly notice: {ex.Message}");
        }
    }

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
                    var bmp = FlagCache.GetOrAdd(code, static c => LoadFlagBitmap(c));
                    if (bmp != null)
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
            AppLogger.Debug($"[FlagAssetService:Prewarm] Primed {count} country flags in {sw.ElapsedMilliseconds}ms.");
        });
    }

    public static void PrewarmFlag(string? countryCode)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return;
        var code = NormalizeCountryCode(countryCode);

        _ = Task.Run(() =>
        {
            try
            {
                FlagCache.GetOrAdd(code, static c => LoadFlagBitmap(c));
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[FlagAssetService:PrewarmFlag] Notice: {ex.Message}");
            }
        });
    }

    public static Bitmap? GetFlag(string? countryCode)
    {
        var code = NormalizeCountryCode(countryCode);

        if (FlagCache.TryGetValue(code, out var cached))
        {
            return cached;
        }

        lock (RasterizeLock)
        {
            if (FlagCache.TryGetValue(code, out cached))
            {
                return cached;
            }

            var bitmap = LoadFlagBitmap(code);
            FlagCache.TryAdd(code, bitmap);
            return bitmap ?? FlagCache.GetValueOrDefault(UnknownCountryCode);
        }
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

    private static Bitmap? LoadFlagBitmap(string code)
    {
        try
        {
            using var stream = OpenFlagStream(code, out bool isSvg);
            if (stream != null)
            {
                return isSvg ? RasterizeSvg(stream) : new Bitmap(stream);
            }

            if (code != UnknownCountryCode)
            {
                using var fallbackStream = OpenFlagStream(UnknownCountryCode, out bool isFallbackSvg);
                if (fallbackStream != null)
                {
                    return isFallbackSvg ? RasterizeSvg(fallbackStream) : new Bitmap(fallbackStream);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[FlagAssetService:Load] Failed loading flag asset for '{code}': {ex.Message}");
        }

        return null;
    }

    private static Stream? OpenFlagStream(string code, out bool isSvg)
    {
        // 1. Check Avalonia embedded resources if Avalonia has initialized
        if (Application.Current != null)
        {
            string[] candidateUris = [
                $"avares:///{AssetsFolderName}/{FlagsFolderName}/{code}{SvgExtension}",
                $"avares:///{AssetsFolderName}/{FlagsFolderName}/{code}{PngExtension}",
                $"avares://{EscapedAssemblyName}/{AssetsFolderName}/{FlagsFolderName}/{code}{SvgExtension}",
                $"avares://{EscapedAssemblyName}/{AssetsFolderName}/{FlagsFolderName}/{code}{PngExtension}",
                $"avares://ReforgerRcon/{AssetsFolderName}/{FlagsFolderName}/{code}{SvgExtension}",
                $"avares://ReforgerRcon/{AssetsFolderName}/{FlagsFolderName}/{code}{PngExtension}"
            ];

            foreach (var uriStr in candidateUris)
            {
                if (Uri.TryCreate(uriStr, UriKind.Absolute, out var uri))
                {
                    try
                    {
                        if (AssetLoader.Exists(uri))
                        {
                            isSvg = uriStr.EndsWith(SvgExtension, StringComparison.OrdinalIgnoreCase);
                            return AssetLoader.Open(uri);
                        }
                    }
                    catch
                    {
                        // Safely try next candidate without throwing unhandled exceptions
                    }
                }
            }
        }

        // 2. Check file system disk paths fallback
        string[] candidateDiskPaths = [
            Path.Combine(AppContext.BaseDirectory, AssetsFolderName, FlagsFolderName, $"{code}{SvgExtension}"),
            Path.Combine(AppContext.BaseDirectory, "assets", FlagsFolderName, $"{code}{SvgExtension}"),
            Path.Combine(AppContext.BaseDirectory, FlagsFolderName, $"{code}{SvgExtension}"),
            Path.Combine(AppContext.BaseDirectory, AssetsFolderName, FlagsFolderName, $"{code}{PngExtension}"),
            Path.Combine(AppContext.BaseDirectory, "assets", FlagsFolderName, $"{code}{PngExtension}"),
            Path.Combine(AppContext.BaseDirectory, FlagsFolderName, $"{code}{PngExtension}"),
            Path.Combine(AppPaths.AppDataDirectory, FlagsFolderName, $"{code}{SvgExtension}"),
            Path.Combine(AppPaths.AppDataDirectory, FlagsFolderName, $"{code}{PngExtension}")
        ];

        var matchingPath = candidateDiskPaths.FirstOrDefault(File.Exists);
        if (matchingPath != null)
        {
            isSvg = matchingPath.EndsWith(SvgExtension, StringComparison.OrdinalIgnoreCase);
            return File.OpenRead(matchingPath);
        }

        isSvg = false;
        return null;
    }

    private static Bitmap? RasterizeSvg(Stream stream)
    {
        try
        {
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

            using var skImage = SKImage.FromBitmap(skBitmap);
            using var data = skImage.Encode(SKEncodedImageFormat.Png, 100);
            using var pngStream = data.AsStream();
            return new Bitmap(pngStream);
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[FlagAssetService:Rasterize] SVG rasterization notice: {ex.Message}");
            return null;
        }
    }
}