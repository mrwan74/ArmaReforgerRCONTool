using System;
using System.Collections.Generic;
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
        try
        {
            if (!File.Exists(FilePath))
            {
                AppLogger.Info("[ProfileStorageService] No profile cache file found. Generating and persisting default connection profiles.");
                var defaults = GetDefaultProfiles();
                await SaveProfilesAsync(defaults);
                return defaults;
            }

            var json = await File.ReadAllTextAsync(FilePath);
            var profiles = JsonSerializer.Deserialize<List<ServerProfile>>(json) ?? GetDefaultProfiles();
            AppLogger.Info($"[ProfileStorageService] Loaded {profiles.Count} server profile(s) from {FilePath}.");
            return profiles;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ProfileStorageService] Profile loading fallback triggered due to error reading {FilePath}", ex);
            return GetDefaultProfiles();
        }
    }

    public static async Task SaveProfilesAsync(List<ServerProfile> profiles)
    {
        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
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

            AppLogger.Debug($"[ProfileStorageService] Persisted {profiles.Count} server profile(s) to disk at {FilePath}.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ProfileStorageService] Failed saving server profiles to disk at {FilePath}", ex);
        }
    }

    [SuppressMessage("Security", "S1313:Hardcoded IP address", Justification = "Default localhost profile placeholders")]
    private static List<ServerProfile> GetDefaultProfiles() =>
    [
        new() { Name = "Reforger Dedicated (Local)", ServerIp = "127.0.0.1", Port = 19999, Password = string.Empty, Protocol = RconProtocol.ReforgerBuiltIn, AutoConnect = false, IsLastSelected = true },
        new() { Name = "BattlEye Server (Local)", ServerIp = "127.0.0.1", Port = 20007, Password = string.Empty, Protocol = RconProtocol.BattlEye, AutoConnect = false, IsLastSelected = false }
    ];
}