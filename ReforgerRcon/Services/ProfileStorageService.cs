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
    private static readonly Lock SyncLock = new();
    private static List<ServerProfile>? _cachedProfiles;

    public static List<ServerProfile> LoadProfilesFast()
    {
        lock (SyncLock)
        {
            if (_cachedProfiles != null)
            {
                return _cachedProfiles;
            }

            if (!File.Exists(FilePath))
            {
                var defaults = GetDefaultProfiles();
                _cachedProfiles = defaults;
                SaveProfilesFast(defaults);
                return defaults;
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var profiles = JsonSerializer.Deserialize<List<ServerProfile>>(json, CachedSerializerOptions) ?? GetDefaultProfiles();
                _cachedProfiles = profiles;
                return profiles;
            }
            catch (JsonException jsonEx)
            {
                AppLogger.Error($"[ProfileStorageService] JSON corruption in profiles file '{FilePath}': {jsonEx.Message}. Restoring default profiles.", jsonEx);
                ToastNotificationService.Instance.ShowWarning("Profiles Corrupted", "Server profiles configuration was invalid. Restored default profiles.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = defaults;
                SaveProfilesFast(defaults);
                return defaults;
            }
            catch (IOException ioEx)
            {
                AppLogger.Error($"[ProfileStorageService] Disk I/O failure reading profiles from '{FilePath}': {ioEx.Message}", ioEx);
                ToastNotificationService.Instance.ShowError("Profile Load Error", "Unable to read profile configuration from disk.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = defaults;
                return defaults;
            }
            catch (UnauthorizedAccessException authEx)
            {
                AppLogger.Error($"[ProfileStorageService] Access denied reading profiles at '{FilePath}': {authEx.Message}", authEx);
                ToastNotificationService.Instance.ShowError("Access Denied", "Operating system denied read permissions to profile file.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = defaults;
                return defaults;
            }
        }
    }

    public static Task<List<ServerProfile>> LoadProfilesAsync() => Task.FromResult(LoadProfilesFast());

    public static void SaveProfilesFast(List<ServerProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        lock (SyncLock)
        {
            _cachedProfiles = profiles;
            try
            {
                if (!Directory.Exists(StorageDirectory))
                {
                    Directory.CreateDirectory(StorageDirectory);
                }

                var json = JsonSerializer.Serialize(profiles, CachedSerializerOptions);
                File.WriteAllText(TempFilePath, json);
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
                File.Move(TempFilePath, FilePath, overwrite: true);
                AppLogger.Debug($"[ProfileStorageService] Persisted {profiles.Count} server profile(s) atomically.");
            }
            catch (JsonException jsonEx)
            {
                AppLogger.Error($"[ProfileStorageService] Serialization error saving profiles: {jsonEx.Message}", jsonEx);
                ToastNotificationService.Instance.ShowError("Profile Save Error", "Failed to serialize server profiles.");
            }
            catch (IOException ioEx)
            {
                AppLogger.Error($"[ProfileStorageService] Disk I/O error writing profiles to '{FilePath}': {ioEx.Message}", ioEx);
                ToastNotificationService.Instance.ShowError("Profile Save Error", "Disk I/O failure while saving server profiles.");
            }
            catch (UnauthorizedAccessException authEx)
            {
                AppLogger.Error($"[ProfileStorageService] Access denied writing profiles to '{FilePath}': {authEx.Message}", authEx);
                ToastNotificationService.Instance.ShowError("Access Denied", "Operating system denied write permissions to save profiles.");
            }
        }
    }

    public static Task SaveProfilesAsync(List<ServerProfile> profiles)
    {
        SaveProfilesFast(profiles);
        return Task.CompletedTask;
    }

    [SuppressMessage("Security", "S1313:Hardcoded IP address", Justification = "Default localhost profile placeholders")]
    private static List<ServerProfile> GetDefaultProfiles() =>
    [
        new() { Name = "Reforger Dedicated (Local)", ServerIp = "127.0.0.1", Port = 19999, Password = string.Empty, Protocol = RconProtocol.ReforgerBuiltIn, AutoConnect = false, IsLastSelected = true },
        new() { Name = "BattlEye Server (Local)", ServerIp = "127.0.0.1", Port = 20007, Password = string.Empty, Protocol = RconProtocol.BattlEye, AutoConnect = false, IsLastSelected = false }
    ];
}