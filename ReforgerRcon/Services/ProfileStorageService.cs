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
    private static readonly Lock SyncLock = new();
    private static List<ServerProfile>? _cachedProfiles;

    public static List<ServerProfile> LoadProfilesFast()
    {
        var start = Stopwatch.GetTimestamp();
        lock (SyncLock)
        {
            if (_cachedProfiles != null)
            {
                return CloneProfileList(_cachedProfiles);
            }

            if (!File.Exists(FilePath))
            {
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                _ = Task.Run(() => SaveProfilesFast(defaults), CancellationToken.None);
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Info($"[ProfileStorageService:Load] Default profiles loaded in {elapsedMs:F2}ms.");
                return CloneProfileList(defaults);
            }

            try
            {
                var json = File.ReadAllText(FilePath);
                var profiles = JsonSerializer.Deserialize<List<ServerProfile>>(json, CachedSerializerOptions) ?? GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(profiles);
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Info($"[ProfileStorageService:Load] Loaded {profiles.Count} profiles in {elapsedMs:F2}ms.");
                return CloneProfileList(profiles);
            }
            catch (JsonException jsonEx)
            {
                AppLogger.Error($"[ProfileStorageService:Load] JSON corruption in '{FilePath}': {jsonEx.Message}. Restoring defaults.", jsonEx);
                ToastNotificationService.Instance.ShowWarning("Profiles Corrupted", "Server profiles configuration was invalid. Restored default profiles.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                return CloneProfileList(defaults);
            }
            catch (IOException ioEx)
            {
                AppLogger.Error($"[ProfileStorageService:Load] Disk I/O failure reading '{FilePath}': {ioEx.Message}", ioEx);
                ToastNotificationService.Instance.ShowError("Profile Load Error", "Unable to read profile configuration from disk.");
                var defaults = GetDefaultProfiles();
                _cachedProfiles = CloneProfileList(defaults);
                return CloneProfileList(defaults);
            }
            catch (UnauthorizedAccessException authEx)
            {
                AppLogger.Error($"[ProfileStorageService:Load] Access denied reading '{FilePath}': {authEx.Message}", authEx);
                ToastNotificationService.Instance.ShowError("Access Denied", "Operating system denied read permissions to profile file.");
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
        lock (SyncLock)
        {
            _cachedProfiles = CloneProfileList(profiles);
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
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Debug($"[ProfileStorageService:Save] Persisted {profiles.Count} profile(s) in {elapsedMs:F2}ms.");
            }
            catch (JsonException jsonEx)
            {
                AppLogger.Error($"[ProfileStorageService:Save] Serialization error saving profiles: {jsonEx.Message}", jsonEx);
                ToastNotificationService.Instance.ShowError("Profile Save Error", "Failed to serialize server profiles.");
            }
            catch (IOException ioEx)
            {
                AppLogger.Error($"[ProfileStorageService:Save] Disk I/O error writing profiles to '{FilePath}': {ioEx.Message}", ioEx);
                ToastNotificationService.Instance.ShowError("Profile Save Error", "Disk I/O failure while saving server profiles.");
            }
            catch (UnauthorizedAccessException authEx)
            {
                AppLogger.Error($"[ProfileStorageService:Save] Access denied writing profiles to '{FilePath}': {authEx.Message}", authEx);
                ToastNotificationService.Instance.ShowError("Access Denied", "Operating system denied write permissions to save profiles.");
            }
        }
    }

    public static Task SaveProfilesAsync(List<ServerProfile> profiles)
    {
        SaveProfilesFast(profiles);
        return Task.CompletedTask;
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