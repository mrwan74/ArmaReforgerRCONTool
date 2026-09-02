using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ReforgerRcon.Services;

internal static class TelemetrySecrets
{
    private const string SentryDsnFileName = "sentry_dsn.txt";
    private const string AptabaseAppKeyFileName = "aptabase_app_key.txt";
    private const string FallbackDsn = "https://cba1d67d907f70dea0258035dedd0f29@o4511942107725824.ingest.de.sentry.io/4511942114345040";
    private const string FallbackAppKey = "A-EU-7965738583";

    private static readonly byte[] XorKey = [0x41, 0x52, 0x52, 0x54, 0x5F, 0x53, 0x45, 0x4E, 0x54, 0x52, 0x59, 0x30, 0x38]; // "ARRT_SENTRY08"

    private static readonly byte[] ObfuscatedDsn = EncodeString(FallbackDsn);
    private static readonly byte[] ObfuscatedAppKey = EncodeString(FallbackAppKey);

    private static string? _cachedDsn;
    private static string? _cachedAppKey;

    private static byte[] EncodeString(string source)
    {
        var raw = Encoding.UTF8.GetBytes(source);
        var result = new byte[raw.Length];
        for (int i = 0; i < raw.Length; i++)
        {
            result[i] = (byte)(raw[i] ^ XorKey[i % XorKey.Length]);
        }
        return result;
    }

    public static string GetEmbeddedDsn()
    {
        if (_cachedDsn != null)
        {
            return _cachedDsn;
        }

        try
        {
            var candidateFiles = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", SentryDsnFileName),
                Path.Combine(AppContext.BaseDirectory, "assets", SentryDsnFileName),
                Path.Combine(AppContext.BaseDirectory, "appdata", SentryDsnFileName),
                Path.Combine(AppContext.BaseDirectory, SentryDsnFileName)
            };

            foreach (var path in candidateFiles.Where(File.Exists))
            {
                var text = File.ReadAllText(path).Trim();
                if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    _cachedDsn = text;
                    return _cachedDsn;
                }
            }

            if (ObfuscatedDsn.Length > 0)
            {
                var decoded = new byte[ObfuscatedDsn.Length];
                for (int i = 0; i < ObfuscatedDsn.Length; i++)
                {
                    decoded[i] = (byte)(ObfuscatedDsn[i] ^ XorKey[i % XorKey.Length]);
                }
                var dsn = Encoding.UTF8.GetString(decoded).Trim();
                if (Uri.TryCreate(dsn, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                {
                    _cachedDsn = dsn;
                    return _cachedDsn;
                }
            }
        }
        catch
        {
            // Fallback gracefully
        }

        _cachedDsn = FallbackDsn;
        return _cachedDsn;
    }

    public static string GetEmbeddedAppKey()
    {
        if (_cachedAppKey != null)
        {
            return _cachedAppKey;
        }

        try
        {
            var candidateFiles = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", AptabaseAppKeyFileName),
                Path.Combine(AppContext.BaseDirectory, "assets", AptabaseAppKeyFileName),
                Path.Combine(AppContext.BaseDirectory, "appdata", AptabaseAppKeyFileName),
                Path.Combine(AppContext.BaseDirectory, AptabaseAppKeyFileName)
            };

            foreach (var path in candidateFiles.Where(File.Exists))
            {
                var text = File.ReadAllText(path).Trim();
                if (text.StartsWith("A-", StringComparison.OrdinalIgnoreCase) && text.Split('-').Length >= 3)
                {
                    _cachedAppKey = text;
                    return _cachedAppKey;
                }
            }

            if (ObfuscatedAppKey.Length > 0)
            {
                var decoded = new byte[ObfuscatedAppKey.Length];
                for (int i = 0; i < ObfuscatedAppKey.Length; i++)
                {
                    decoded[i] = (byte)(ObfuscatedAppKey[i] ^ XorKey[i % XorKey.Length]);
                }
                var key = Encoding.UTF8.GetString(decoded).Trim();
                if (key.StartsWith("A-", StringComparison.OrdinalIgnoreCase) && key.Split('-').Length >= 3)
                {
                    _cachedAppKey = key;
                    return _cachedAppKey;
                }
            }
        }
        catch
        {
            // Fallback gracefully
        }

        _cachedAppKey = FallbackAppKey;
        return _cachedAppKey;
    }
}