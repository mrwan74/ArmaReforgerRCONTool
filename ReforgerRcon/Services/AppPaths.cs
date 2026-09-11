using System;
using System.Diagnostics;
using System.IO;
using Velopack;
using Velopack.Locators;

namespace ReforgerRcon.Services;

/// <summary>
/// Provides persistent storage paths that survive Velopack version updates.
/// </summary>
public static class AppPaths
{
    private static readonly Lazy<string> AppDataDirLazy = new(ResolveAppDataDirectoryInternal);

    public static string AppDataDirectory => AppDataDirLazy.Value;
    public static string LogsDirectory => Path.Combine(AppDataDirectory, "logs");
    public static string CrashReportsDirectory => Path.Combine(AppDataDirectory, "crash_reports");
    public static string DatabaseDirectory => AppDataDirectory;
    public static string GeoIpDirectory => Path.Combine(AppDataDirectory, "geoip");

    private static string ResolveAppDataDirectoryInternal()
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        try
        {
            // In Velopack 1.2.0, CurrentlyInstalledVersion != null indicates a packaged installation
            var locator = VelopackLocator.Current;
            if (locator.CurrentlyInstalledVersion != null)
            {
                var rootDir = locator.RootAppDir;
                if (!string.IsNullOrWhiteSpace(rootDir) && Directory.Exists(rootDir))
                {
                    var persistentPath = Path.Combine(rootDir, "appdata");
                    if (!Directory.Exists(persistentPath))
                    {
                        Directory.CreateDirectory(persistentPath);
                    }

                    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    System.Diagnostics.Trace.TraceInformation($"[AppPaths:Resolve] Persistent directory resolved in {elapsedMs:F2}ms: '{persistentPath}'");
                    return persistentPath;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[AppPaths:Resolve] Notice querying Velopack locator: {ex.Message}");
        }

        // Portable / Development fallback (relative to executable base)
        var fallbackPath = Path.Combine(AppContext.BaseDirectory, "appdata");
        try
        {
            if (!Directory.Exists(fallbackPath))
            {
                Directory.CreateDirectory(fallbackPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[AppPaths:Resolve] Failed creating local fallback AppData directory: {ex.Message}");
        }

        var totalElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        System.Diagnostics.Trace.TraceInformation($"[AppPaths:Resolve] Fallback resolution completed in {totalElapsedMs:F2}ms: '{fallbackPath}'");
        return fallbackPath;
    }
}