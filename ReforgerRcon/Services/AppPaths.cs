using System;
using System.Diagnostics;
using System.IO;
using Velopack;
using Velopack.Locators;

namespace ReforgerRcon.Services;

/// <summary>
/// Provides persistent storage paths that survive updates and support portable execution (Windows and Linux AppImage).
/// </summary>
public static class AppPaths
{
    private const string AppDataFolderName = "appdata";

    private static readonly Lazy<string> AppDataDirLazy = new(ResolveAppDataDirectoryInternal);

    public static string AppDataDirectory => AppDataDirLazy.Value;
    public static string LogsDirectory => Path.Combine(AppDataDirectory, "logs");
    public static string CrashReportsDirectory => Path.Combine(AppDataDirectory, "crash_reports");
    public static string DatabaseDirectory => AppDataDirectory;
    public static string GeoIpDirectory => Path.Combine(AppDataDirectory, "geoip");

    private static string ResolveAppDataDirectoryInternal()
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        // 1. Check Velopack packaged installation locator (Windows installed mode)
        try
        {
            var locator = VelopackLocator.Current;
            if (locator.CurrentlyInstalledVersion != null)
            {
                var rootDir = locator.RootAppDir;
                if (!string.IsNullOrWhiteSpace(rootDir) && Directory.Exists(rootDir))
                {
                    var persistentPath = Path.Combine(rootDir, AppDataFolderName);
                    if (!Directory.Exists(persistentPath))
                    {
                        Directory.CreateDirectory(persistentPath);
                    }

                    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    System.Diagnostics.Trace.TraceInformation($"[AppPaths:Resolve] Velopack root directory resolved in {elapsedMs:F2}ms: '{persistentPath}'");
                    return persistentPath;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[AppPaths:Resolve] Notice querying Velopack locator: {ex.Message}");
        }

        // 2. Linux AppImage Support: The mounted directory is read-only.
        // The $APPIMAGE environment variable points to the real .AppImage file on disk (e.g. /root/Desktop/ReforgerRcon.AppImage).
        var appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrWhiteSpace(appImagePath))
        {
            try
            {
                var appImageDir = Path.GetDirectoryName(appImagePath);
                if (!string.IsNullOrWhiteSpace(appImageDir) && Directory.Exists(appImageDir))
                {
                    var persistentPath = Path.Combine(appImageDir, AppDataFolderName);
                    if (!Directory.Exists(persistentPath))
                    {
                        Directory.CreateDirectory(persistentPath);
                    }

                    // Test write access
                    var testFile = Path.Combine(persistentPath, ".write_test");
                    File.WriteAllText(testFile, "test");
                    File.Delete(testFile);

                    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    System.Diagnostics.Trace.TraceInformation($"[AppPaths:Resolve] Linux AppImage persistent directory resolved in {elapsedMs:F2}ms: '{persistentPath}'");
                    return persistentPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceWarning($"[AppPaths:Resolve] Notice creating AppImage local appdata directory: {ex.Message}");
            }
        }

        // 3. Portable fallback relative to executable base (Windows Portable or unbundled Linux)
        var fallbackPath = Path.Combine(AppContext.BaseDirectory, AppDataFolderName);
        try
        {
            if (!Directory.Exists(fallbackPath))
            {
                Directory.CreateDirectory(fallbackPath);
            }

            // Test write access to ensure it's not a read-only mount
            var testFile = Path.Combine(fallbackPath, ".write_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            System.Diagnostics.Trace.TraceInformation($"[AppPaths:Resolve] Portable fallback resolved in {elapsedMs:F2}ms: '{fallbackPath}'");
            return fallbackPath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"[AppPaths:Resolve] Base directory '{fallbackPath}' is read-only or inaccessible: {ex.Message}");
        }

        // 4. User home directory fallback for Linux / macOS when base directory is read-only
        var userHomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userHomeDir))
        {
            userHomeDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var userProfilePath = Path.Combine(userHomeDir, ".reforger_rcon", AppDataFolderName);
        try
        {
            if (!Directory.Exists(userProfilePath))
            {
                Directory.CreateDirectory(userProfilePath);
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            System.Diagnostics.Trace.TraceInformation($"[AppPaths:Resolve] User profile fallback resolved in {elapsedMs:F2}ms: '{userProfilePath}'");
            return userProfilePath;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[AppPaths:Resolve] Failed creating user profile fallback directory: {ex.Message}");
        }

        return fallbackPath;
    }
}