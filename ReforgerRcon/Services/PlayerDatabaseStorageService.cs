using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public static class PlayerDatabaseStorageService
{
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string StorageFile = Path.Combine(StorageDirectory, "player_database.json");
    private static readonly string TempStorageFile = Path.Combine(StorageDirectory, "player_database.json.tmp");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly ConcurrentDictionary<string, DatabasePlayerModel> Db = new(StringComparer.OrdinalIgnoreCase);
    private static bool _isLoaded;
    private static readonly SemaphoreSlim SaveSemaphore = new(1, 1);

    public static async Task EnsureLoadedAsync()
    {
        if (_isLoaded) return;
        _isLoaded = true;

        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
            }

            if (File.Exists(StorageFile))
            {
                var json = await File.ReadAllTextAsync(StorageFile);
                var list = JsonSerializer.Deserialize<List<DatabasePlayerModel>>(json) ?? [];
                foreach (var item in list)
                {
                    var key = !string.IsNullOrEmpty(item.Uid) ? item.Uid : item.Guid;
                    if (!string.IsNullOrEmpty(key))
                    {
                        Db[key] = item;
                    }
                }
                AppLogger.Info($"Loaded {Db.Count} historical player records from {StorageFile}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed loading player database records from disk.", ex);
        }
    }

    public static async Task RecordSeenPlayersAsync(IEnumerable<PlayerModel> activePlayers, bool isReforger = false)
    {
        try
        {
            await EnsureLoadedAsync();
            bool changed = false;

            foreach (var player in activePlayers)
            {
                var key = !string.IsNullOrWhiteSpace(player.Uid) ? player.Uid : player.Guid;
                if (string.IsNullOrWhiteSpace(key)) continue;

                if (Db.TryGetValue(key, out var existing))
                {
                    if (UpdateExistingRecord(existing, player, isReforger))
                    {
                        changed = true;
                    }
                }
                else
                {
                    CreateNewRecord(key, player, isReforger);
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error in RecordSeenPlayersAsync.", ex);
        }
    }

    private static bool UpdateExistingRecord(DatabasePlayerModel existing, PlayerModel player, bool isReforger)
    {
        bool changed = false;
        existing.IsOnline = true;
        existing.LastSeen = DateTime.UtcNow;
        existing.Ping = player.Ping;
        existing.LastIp = player.Ip;
        existing.LastPort = player.Port;

        if (isReforger)
        {
            existing.Id = 0; // Dynamic session ID is not stored for Reforger
        }

        if (!string.IsNullOrWhiteSpace(player.Name) && !existing.Name.Equals(player.Name, StringComparison.Ordinal))
        {
            if (!existing.Aliases.Contains(existing.Name))
            {
                existing.Aliases.Add(existing.Name);
            }
            existing.Name = player.Name;
            existing.HasAliases = true;
            player.HasAliases = true;
            player.Aliases = [.. existing.Aliases];
            changed = true;
        }

        player.Comment = existing.Comment;
        player.IsWatchlisted = existing.IsWatchlisted;
        return changed;
    }

    private static void CreateNewRecord(string key, PlayerModel player, bool isReforger)
    {
        var newEntry = new DatabasePlayerModel
        {
            Id = isReforger ? 0 : player.Id, // Do not store dynamic session player# in Reforger
            Name = player.Name,
            Uid = player.Uid,
            Guid = player.Guid,
            LastIp = player.Ip,
            LastPort = player.Port,
            Ping = player.Ping,
            IsOnline = true,
            Comment = player.Comment,
            IsWatchlisted = player.IsWatchlisted,
            HasAliases = false,
            LastSeen = DateTime.UtcNow,
            Country = player.Country,
            Location = player.DisplayLocation
        };

        Db[key] = newEntry;
    }

    public static async Task SetAllOfflineAsync()
    {
        try
        {
            await EnsureLoadedAsync();
            foreach (var entry in Db.Values)
            {
                entry.IsOnline = false;
            }
            await SaveAsync();
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error in SetAllOfflineAsync.", ex);
        }
    }

    public static async Task<List<DatabasePlayerModel>> GetAllAsync()
    {
        try
        {
            await EnsureLoadedAsync();
            return [.. Db.Values.OrderByDescending(p => p.LastSeen)];
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error retrieving all database player entries.", ex);
            return [];
        }
    }

    public static async Task UpdateCommentAsync(string identifier, string comment)
    {
        try
        {
            await EnsureLoadedAsync();
            if (Db.TryGetValue(identifier, out var record))
            {
                record.Comment = comment;
                await SaveAsync();
                AppLogger.Info($"Updated admin comment for {identifier}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed updating comment for {identifier}", ex);
        }
    }

    public static async Task ToggleWatchlistAsync(string identifier)
    {
        try
        {
            await EnsureLoadedAsync();
            if (Db.TryGetValue(identifier, out var record))
            {
                record.IsWatchlisted = !record.IsWatchlisted;
                await SaveAsync();
                AppLogger.Info($"Toggled watchlist for {identifier} -> {record.IsWatchlisted}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed toggling watchlist for {identifier}", ex);
        }
    }

    public static async Task ClearAsync()
    {
        try
        {
            Db.Clear();
            await SaveAsync();
            AppLogger.Info("Purged entire player database.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed clearing player database.", ex);
        }
    }

    private static async Task SaveAsync()
    {
        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
            }

            var list = Db.Values.ToList();
            var json = JsonSerializer.Serialize(list, JsonOptions);

            await SaveSemaphore.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(TempStorageFile, json);
                if (File.Exists(StorageFile))
                {
                    File.Delete(StorageFile);
                }
                File.Move(TempStorageFile, StorageFile, overwrite: true);
            }
            finally
            {
                SaveSemaphore.Release();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed persisting player database records to disk.", ex);
        }
    }
}