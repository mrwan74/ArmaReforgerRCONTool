using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public static class ProfileStorageService
{
    private static readonly JsonSerializerOptions CachedSerializerOptions = new() { WriteIndented = true };
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string FilePath = Path.Combine(StorageDirectory, "profiles.json");
    private static readonly string TempFilePath = Path.Combine(StorageDirectory, "profiles.json.tmp");
    private static readonly SemaphoreSlim SaveSemaphore = new(1, 1);

    public static async Task<List<ServerProfile>> LoadProfilesAsync()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!File.Exists(FilePath))
            {
                AppLogger.Info("[ProfileStorageService] No profile configuration file found. Generating default connection profiles.");
                var defaults = GetDefaultProfiles();
                await SaveProfilesAsync(defaults);
                sw.Stop();
                return defaults;
            }

            var json = await File.ReadAllTextAsync(FilePath);
            var profiles = JsonSerializer.Deserialize<List<ServerProfile>>(json) ?? GetDefaultProfiles();
            sw.Stop();
            AppLogger.Info($"[ProfileStorageService] Loaded {profiles.Count} server profile(s) from '{FilePath}' in {sw.ElapsedMilliseconds} ms.");
            return profiles;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Error($"[ProfileStorageService] Profile loading fallback triggered due to error reading '{FilePath}': {ex.Message}", ex);
            ToastNotificationService.Instance.ShowWarning("Profile Warning", "Could not load saved server profiles from disk. Using defaults.");
            return GetDefaultProfiles();
        }
    }

    public static async Task SaveProfilesAsync(List<ServerProfile> profiles)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
                AppLogger.Debug($"[ProfileStorageService] Created storage directory: {StorageDirectory}");
            }
            var json = JsonSerializer.Serialize(profiles, CachedSerializerOptions);

            await SaveSemaphore.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(TempFilePath, json);
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
                File.Move(TempFilePath, FilePath, overwrite: true);
            }
            finally
            {
                SaveSemaphore.Release();
            }

            sw.Stop();
            AppLogger.Debug($"[ProfileStorageService] Persisted {profiles.Count} server profile(s) to '{FilePath}' in {sw.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Error($"[ProfileStorageService] Failed saving server profiles to disk at '{FilePath}': {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Profile Save Failed", "Could not persist connection profiles to disk: " + ex.Message);
        }
    }

    [SuppressMessage("Security", "S1313:Hardcoded IP address", Justification = "Default localhost profile placeholders")]
    private static List<ServerProfile> GetDefaultProfiles() =>
    [
        new() { Name = "Reforger Dedicated (Local)", ServerIp = "127.0.0.1", Port = 19999, Password = string.Empty, Protocol = RconProtocol.ReforgerBuiltIn, AutoConnect = false, IsLastSelected = true },
        new() { Name = "BattlEye Server (Local)", ServerIp = "127.0.0.1", Port = 20007, Password = string.Empty, Protocol = RconProtocol.BattlEye, AutoConnect = false, IsLastSelected = false }
    ];
}