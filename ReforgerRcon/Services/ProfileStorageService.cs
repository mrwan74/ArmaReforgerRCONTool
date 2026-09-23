using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public static class ProfileStorageService
{
    private const string ContextProfilesCount = "profiles_count";
    private const string ContextStorageDir = "storage_dir";
    private const string ContextFilePath = "file_path";
    private const string ContextThreadId = "thread_id";
    private const string ContextElapsedMs = "elapsed_ms";
    private const string ContextError = "error";

    private static readonly JsonSerializerOptions CachedSerializerOptions = new() { WriteIndented = true };
    private static readonly string StorageDirectory = AppPaths.AppDataDirectory;
    private static readonly string FilePath = Path.Combine(StorageDirectory, "profiles.json");
    private static readonly string TempFilePath = Path.Combine(StorageDirectory, "profiles.json.tmp");
    private static readonly Lock SyncLock = new();
    private static List<ServerProfile>? _cachedProfiles;

    public static List<ServerProfile> LoadProfilesFast()
    {
        var start = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            [ContextStorageDir] = StorageDirectory,
            [ContextFilePath] = FilePath,
            [ContextThreadId] = threadId
        };

        lock (SyncLock)
        {
            if (_cachedProfiles != null)
            {
                AppLogger.Trace($"[ProfileStorageService:Load] Returning {_cachedProfiles.Count} cached profile(s) from memory.", context);
                return CloneProfileList(_cachedProfiles);
            }

            MigrateLegacyProfilesIfPresent();

            if (!File.Exists(FilePath))
            {
                AppLogger.Info($"[ProfileStorageService:Load] Configuration file '{FilePath}' does not exist. Initializing defaults...", context);
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                _ = Task.Run(() => SaveProfilesFast(defaults), CancellationToken.None);
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Info($"[ProfileStorageService:Load] Default server profiles initialized in {elapsedMs:F2}ms.", context);
                return CloneProfileList(defaults);
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                context["file_size_bytes"] = json.Length;

                var profiles = JsonSerializer.Deserialize<List<ServerProfile>>(json, CachedSerializerOptions) ?? GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(profiles);
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextProfilesCount] = profiles.Count;
                context[ContextElapsedMs] = elapsedMs;

                AppLogger.Info($"[ProfileStorageService:Load] Loaded {profiles.Count} profiles in {elapsedMs:F2}ms from '{FilePath}'.", context);
                return CloneProfileList(profiles);
            }
            catch (JsonException jsonEx)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = jsonEx.Message;

                AppLogger.Error($"[ProfileStorageService:Load] JSON corruption in '{FilePath}' after {elapsedMs:F2}ms: {jsonEx.Message}. Restoring defaults.", jsonEx, context);
                ToastNotificationService.Instance.ShowWarning("Profiles Corrupted", "Server profiles configuration was invalid. Restored default profiles.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                return CloneProfileList(defaults);
            }
            catch (IOException ioEx)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = ioEx.Message;
                context["hresult"] = string.Create(CultureInfo.InvariantCulture, $"0x{ioEx.HResult:X8}");

                AppLogger.Error($"[ProfileStorageService:Load] Disk I/O failure reading '{FilePath}' after {elapsedMs:F2}ms: {ioEx.Message}", ioEx, context);
                ToastNotificationService.Instance.ShowError("Profile Load Error", "Unable to read server profile configuration from storage.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                return CloneProfileList(defaults);
            }
            catch (UnauthorizedAccessException authEx)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = authEx.Message;

                AppLogger.Error($"[ProfileStorageService:Load] Permission denied reading '{FilePath}' after {elapsedMs:F2}ms: {authEx.Message}", authEx, context);
                ToastNotificationService.Instance.ShowError("Access Denied", "Operating system denied read permissions to profile file.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                return CloneProfileList(defaults);
            }
            catch (Exception ex)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = ex.Message;

                AppLogger.Fatal($"[ProfileStorageService:Load] Critical unhandled error reading '{FilePath}' after {elapsedMs:F2}ms: {ex.Message}", ex, context);
                CrashReportService.HandleFatalException("ProfileStorageService.LoadProfilesFast", ex, isTerminating: false);
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                return CloneProfileList(defaults);
            }
        }
    }

    public static Task<List<ServerProfile>> LoadProfilesAsync() => Task.FromResult(LoadProfilesFast());

    public static void SaveProfilesFast(List<ServerProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var start = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            [ContextProfilesCount] = profiles.Count,
            [ContextStorageDir] = StorageDirectory,
            [ContextFilePath] = FilePath,
            [ContextThreadId] = threadId
        };

        lock (SyncLock)
        {
            _cachedProfiles = CloneProfileList(profiles);

            try
            {
                if (!Directory.Exists(StorageDirectory))
                {
                    AppLogger.Trace($"[ProfileStorageService:Save] Creating storage directory: '{StorageDirectory}'", context);
                    Directory.CreateDirectory(StorageDirectory);
                }

                var json = JsonSerializer.Serialize(profiles, CachedSerializerOptions);
                context["json_length"] = json.Length;

                AppLogger.Trace($"[ProfileStorageService:Save] Writing {json.Length} bytes to temporary file '{TempFilePath}'...", context);
                File.WriteAllText(TempFilePath, json);

                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }

                File.Move(TempFilePath, FilePath, overwrite: true);

                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                AppLogger.Debug($"[ProfileStorageService:Save] Persisted {profiles.Count} profile(s) in {elapsedMs:F2}ms.", context);
            }
            catch (JsonException jsonEx)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = jsonEx.Message;

                AppLogger.Error($"[ProfileStorageService:Save] Serialization error saving profiles after {elapsedMs:F2}ms: {jsonEx.Message}", jsonEx, context);
                ToastNotificationService.Instance.ShowError("Profile Save Error", "Failed to serialize server profiles: " + jsonEx.Message);
            }
            catch (IOException ioEx)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = ioEx.Message;
                context["hresult"] = string.Create(CultureInfo.InvariantCulture, $"0x{ioEx.HResult:X8}");

                AppLogger.Error($"[ProfileStorageService:Save] Disk I/O error writing profiles to '{FilePath}' after {elapsedMs:F2}ms: {ioEx.Message} (HResult: 0x{ioEx.HResult:X8})", ioEx, context);

                // Fallback direct write if atomic move fails
                try
                {
                    AppLogger.Warn($"[ProfileStorageService:Save] Attempting fallback direct write to '{FilePath}'...", null, context);
                    var fallbackJson = JsonSerializer.Serialize(profiles, CachedSerializerOptions);
                    File.WriteAllText(FilePath, fallbackJson);
                    AppLogger.Info($"[ProfileStorageService:Save] Fallback direct write to '{FilePath}' succeeded.", context);
                    return;
                }
                catch (Exception fallbackEx)
                {
                    AppLogger.Error($"[ProfileStorageService:Save] Fallback direct write also failed: {fallbackEx.Message}", fallbackEx, context);
                }

                ToastNotificationService.Instance.ShowError("Profile Save Error", $"Disk I/O failure while saving server profiles: {ioEx.Message}");
            }
            catch (UnauthorizedAccessException authEx)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = authEx.Message;

                AppLogger.Error($"[ProfileStorageService:Save] Access denied writing profiles to '{FilePath}' after {elapsedMs:F2}ms: {authEx.Message}", authEx, context);
                ToastNotificationService.Instance.ShowError("Access Denied", $"Operating system denied write permissions to save profiles: {authEx.Message}");
            }
            catch (Exception ex)
            {
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                context[ContextElapsedMs] = elapsedMs;
                context[ContextError] = ex.Message;

                AppLogger.Fatal($"[ProfileStorageService:Save] Unexpected critical failure saving profiles after {elapsedMs:F2}ms: {ex.Message}", ex, context);
                CrashReportService.HandleFatalException("ProfileStorageService.SaveProfilesFast", ex, isTerminating: false);
                ToastNotificationService.Instance.ShowError("Profile Storage Fault", "Unexpected error saving server profiles: " + ex.Message);
            }
        }
    }

    public static Task SaveProfilesAsync(List<ServerProfile> profiles)
    {
        SaveProfilesFast(profiles);
        return Task.CompletedTask;
    }

    private static void MigrateLegacyProfilesIfPresent()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var legacyDir = Path.Combine(AppContext.BaseDirectory, "appdata");
                var legacyFile = Path.Combine(legacyDir, "profiles.json");
                if (File.Exists(legacyFile))
                {
                    if (!Directory.Exists(StorageDirectory))
                    {
                        Directory.CreateDirectory(StorageDirectory);
                    }
                    File.Copy(legacyFile, FilePath, overwrite: false);
                    AppLogger.Info($"[ProfileStorageService:Migrate] Migrated legacy profiles from '{legacyFile}' to '{FilePath}'.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[ProfileStorageService:Migrate] Notice during legacy profile migration check: {ex.Message}");
        }
    }

    private static List<ServerProfile> CloneProfileList(List<ServerProfile> source)
    {
        var result = new List<ServerProfile>(source.Count);
        foreach (var p in source)
        {
            result.Add(new ServerProfile
            {
                Id = p.Id,
                Name = p.Name,
                ServerIp = p.ServerIp,
                Port = p.Port,
                Password = p.Password,
                Protocol = p.Protocol,
                AutoConnect = p.AutoConnect,
                IsLastSelected = p.IsLastSelected
            });
        }
        return result;
    }

    [SuppressMessage("Security", "S1313:Hardcoded IP address", Justification = "Default localhost profile placeholders")]
    private static List<ServerProfile> GetDefaultProfiles() =>
    [
        new() { Name = "Reforger Dedicated (Local)", ServerIp = "127.0.0.1", Port = 19999, Password = string.Empty, Protocol = RconProtocol.ReforgerBuiltIn, AutoConnect = false, IsLastSelected = true },
        new() { Name = "BattlEye Server (Local)", ServerIp = "127.0.0.1", Port = 20007, Password = string.Empty, Protocol = RconProtocol.BattlEye, AutoConnect = false, IsLastSelected = false }
    ];
}