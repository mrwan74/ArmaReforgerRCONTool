using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ReforgerRcon.Models;
using Sentry;
using SerilogTimings;

namespace ReforgerRcon.Services;

public record DatabaseStatistics(
    int TotalPlayers,
    int TotalAliases,
    int OnlinePlayers,
    int WatchlistedPlayers,
    long DatabaseSizeBytes,
    long WalSizeBytes,
    string DatabasePath);

public static class PlayerDatabaseStorageService
{
    private const string DatabaseErrorTitle = "Database Error";

    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string DatabaseFile = Path.Combine(StorageDirectory, "player_database.db");
    private static readonly string LegacyJsonFile = Path.Combine(StorageDirectory, "player_database.json");
    private static readonly string ConnectionString = $"Data Source={DatabaseFile};Cache=Shared;Mode=ReadWriteCreate;";

    private static readonly SemaphoreSlim DbLock = new(1, 1);
    private static bool _isInitialized;

    public static async Task InitializeAsync()
    {
        if (_isInitialized) return;

        await DbLock.WaitAsync();
        try
        {
            if (_isInitialized) return;

            using var op = Operation.Begin("Initialize SQLite Database Schema at {DatabaseFile}", DatabaseFile);
            AppLogger.Info($"[PlayerDatabase] Initializing SQLite database engine at '{DatabaseFile}'...");

            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            await ExecutePragmasAsync(connection);
            await CreateSchemaAsync(connection);

            _isInitialized = true;
            op.Complete();

            await CheckAndMigrateLegacyJsonAsync(connection);
        }
        catch (SqliteException sqlEx)
        {
            AppLogger.Fatal($"[PlayerDatabase] SQLite fatal error initializing database at '{DatabaseFile}': {sqlEx.Message} (Error code: {sqlEx.SqliteErrorCode})", sqlEx);
            CrashReportService.HandleFatalException("PlayerDatabaseStorageService.InitializeAsync", sqlEx, isTerminating: false);
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Fatal($"[PlayerDatabase] Unexpected fatal failure initializing SQLite engine: {ex.Message}", ex);
            CrashReportService.HandleFatalException("PlayerDatabaseStorageService.InitializeAsync", ex, isTerminating: false);
            throw;
        }
        finally
        {
            DbLock.Release();
        }
    }

    private static async Task ExecutePragmasAsync(SqliteConnection connection)
    {
        const string pragmaSql = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = ON;
            PRAGMA temp_store = MEMORY;
        ";

        await using var command = connection.CreateCommand();
        command.CommandText = pragmaSql;
        await command.ExecuteNonQueryAsync();
        AppLogger.Debug("[PlayerDatabase] Applied WAL mode and performance PRAGMAs to SQLite connection.");
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection)
    {
        const string schemaSql = @"
            CREATE TABLE IF NOT EXISTS Players (
                Uid TEXT PRIMARY KEY NOT NULL,
                Guid TEXT NOT NULL DEFAULT '',
                Name TEXT NOT NULL,
                LastIp TEXT NOT NULL DEFAULT '127.0.0.1',
                LastPort INTEGER NOT NULL DEFAULT 2304,
                Ping INTEGER NOT NULL DEFAULT 0,
                IsOnline INTEGER NOT NULL DEFAULT 0,
                Comment TEXT NOT NULL DEFAULT '',
                IsWatchlisted INTEGER NOT NULL DEFAULT 0,
                HasAliases INTEGER NOT NULL DEFAULT 0,
                CountryCode TEXT NOT NULL DEFAULT 'un',
                CountryName TEXT NOT NULL DEFAULT 'Unknown Region',
                Location TEXT NOT NULL DEFAULT '',
                FirstSeenUtc TEXT NOT NULL,
                LastSeenUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PlayerAliases (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerUid TEXT NOT NULL,
                AliasName TEXT NOT NULL,
                FirstSeenUtc TEXT NOT NULL,
                LastSeenUtc TEXT NOT NULL,
                FOREIGN KEY (PlayerUid) REFERENCES Players(Uid) ON DELETE CASCADE,
                UNIQUE(PlayerUid, AliasName)
            );

            CREATE INDEX IF NOT EXISTS IX_Players_Name ON Players(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Players_Guid ON Players(Guid);
            CREATE INDEX IF NOT EXISTS IX_Players_LastSeenUtc ON Players(LastSeenUtc);
            CREATE INDEX IF NOT EXISTS IX_Players_IsWatchlisted ON Players(IsWatchlisted);
            CREATE INDEX IF NOT EXISTS IX_Players_IsOnline ON Players(IsOnline);
            CREATE INDEX IF NOT EXISTS IX_PlayerAliases_PlayerUid ON PlayerAliases(PlayerUid);
            CREATE INDEX IF NOT EXISTS IX_PlayerAliases_AliasName ON PlayerAliases(AliasName COLLATE NOCASE);
        ";

        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync();
        AppLogger.Info("[PlayerDatabase] SQLite tables, relational foreign keys, and indexes verified.");
    }

    private static async Task CheckAndMigrateLegacyJsonAsync(SqliteConnection connection)
    {
        if (!File.Exists(LegacyJsonFile)) return;

        var transaction = SentrySdk.StartTransaction("MigrateLegacyJsonToSqlite", "db.migration");
        using var op = Operation.Begin("Migrate legacy JSON player records to SQLite database");

        try
        {
            AppLogger.Info($"[PlayerDatabase] Legacy database file detected at '{LegacyJsonFile}'. Starting automated data migration...");
            var json = await File.ReadAllTextAsync(LegacyJsonFile);
            var legacyList = JsonSerializer.Deserialize<List<DatabasePlayerModel>>(json) ?? [];

            if (legacyList.Count > 0)
            {
                await using var dbTransaction = await connection.BeginTransactionAsync();
                try
                {
                    int importedCount = 0;
                    foreach (var player in legacyList)
                    {
                        var uid = !string.IsNullOrWhiteSpace(player.Uid)
                            ? player.Uid.Trim()
                            : (player.Guid?.Trim() ?? string.Empty);

                        if (string.IsNullOrWhiteSpace(uid)) continue;

                        const string upsertSql = @"
                            INSERT INTO Players (
                                Uid, Guid, Name, LastIp, LastPort, Ping, IsOnline, Comment,
                                IsWatchlisted, HasAliases, CountryCode, CountryName, Location,
                                FirstSeenUtc, LastSeenUtc
                            ) VALUES (
                                @Uid, @Guid, @Name, @LastIp, @LastPort, @Ping, @IsOnline, @Comment,
                                @IsWatchlisted, @HasAliases, @CountryCode, @CountryName, @Location,
                                @FirstSeenUtc, @LastSeenUtc
                            )
                            ON CONFLICT(Uid) DO UPDATE SET
                                Name = excluded.Name,
                                Comment = CASE WHEN excluded.Comment <> '' THEN excluded.Comment ELSE Players.Comment END,
                                IsWatchlisted = excluded.IsWatchlisted,
                                HasAliases = excluded.HasAliases,
                                CountryCode = excluded.CountryCode,
                                CountryName = excluded.CountryName,
                                Location = excluded.Location,
                                LastSeenUtc = excluded.LastSeenUtc;
                        ";

                        await using (var cmd = connection.CreateCommand())
                        {
                            cmd.Transaction = (SqliteTransaction)dbTransaction;
                            cmd.CommandText = upsertSql;
                            cmd.Parameters.AddWithValue("@Uid", uid);
                            cmd.Parameters.AddWithValue("@Guid", player.Guid ?? string.Empty);
                            cmd.Parameters.AddWithValue("@Name", player.Name ?? "Unknown Player");
                            cmd.Parameters.AddWithValue("@LastIp", player.LastIp ?? "127.0.0.1");
                            cmd.Parameters.AddWithValue("@LastPort", player.LastPort);
                            cmd.Parameters.AddWithValue("@Ping", player.Ping);
                            cmd.Parameters.AddWithValue("@IsOnline", player.IsOnline ? 1 : 0);
                            cmd.Parameters.AddWithValue("@Comment", player.Comment ?? string.Empty);
                            cmd.Parameters.AddWithValue("@IsWatchlisted", player.IsWatchlisted ? 1 : 0);
                            cmd.Parameters.AddWithValue("@HasAliases", player.Aliases is { Count: > 0 } ? 1 : 0);
                            cmd.Parameters.AddWithValue("@CountryCode", player.Country?.Code ?? "un");
                            cmd.Parameters.AddWithValue("@CountryName", player.Country?.Name ?? "Unknown Region");
                            cmd.Parameters.AddWithValue("@Location", player.Location ?? string.Empty);
                            cmd.Parameters.AddWithValue("@FirstSeenUtc", player.LastSeen.ToString("o", CultureInfo.InvariantCulture));
                            cmd.Parameters.AddWithValue("@LastSeenUtc", player.LastSeen.ToString("o", CultureInfo.InvariantCulture));
                            await cmd.ExecuteNonQueryAsync();
                        }

                        if (player.Aliases is { Count: > 0 })
                        {
                            foreach (var alias in player.Aliases.Where(a => !string.IsNullOrWhiteSpace(a)))
                            {
                                const string aliasSql = @"
                                    INSERT OR IGNORE INTO PlayerAliases (PlayerUid, AliasName, FirstSeenUtc, LastSeenUtc)
                                    VALUES (@PlayerUid, @AliasName, @FirstSeenUtc, @LastSeenUtc);
                                ";
                                await using var aliasCmd = connection.CreateCommand();
                                aliasCmd.Transaction = (SqliteTransaction)dbTransaction;
                                aliasCmd.CommandText = aliasSql;
                                aliasCmd.Parameters.AddWithValue("@PlayerUid", uid);
                                aliasCmd.Parameters.AddWithValue("@AliasName", alias.Trim());
                                aliasCmd.Parameters.AddWithValue("@FirstSeenUtc", player.LastSeen.ToString("o", CultureInfo.InvariantCulture));
                                aliasCmd.Parameters.AddWithValue("@LastSeenUtc", player.LastSeen.ToString("o", CultureInfo.InvariantCulture));
                                await aliasCmd.ExecuteNonQueryAsync();
                            }
                        }

                        importedCount++;
                    }

                    await dbTransaction.CommitAsync();
                    AppLogger.Info($"[PlayerDatabase] Successfully migrated {importedCount} historical records from JSON into SQLite.");
                }
                catch (Exception txEx)
                {
                    await dbTransaction.RollbackAsync();
                    AppLogger.Error("[PlayerDatabase] Rollback during legacy JSON migration due to transaction fault.", txEx);
                    throw;
                }
            }

            var backupFile = $"{LegacyJsonFile}.bak";
            if (File.Exists(backupFile))
            {
                File.Delete(backupFile);
            }
            File.Move(LegacyJsonFile, backupFile);
            AppLogger.Info($"[PlayerDatabase] Renamed legacy '{LegacyJsonFile}' to '{backupFile}'.");

            op.Complete("MigratedCount", legacyList.Count);
            transaction.Finish(SpanStatus.Ok);
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[PlayerDatabase] Failed migrating legacy JSON file '{LegacyJsonFile}' to SQLite database.", ex);
        }
    }

    public static async Task RecordSeenPlayersAsync(IEnumerable<PlayerModel> activePlayers)
    {
        await InitializeAsync();
        var playersList = activePlayers.ToList();
        if (playersList.Count == 0) return;

        var sw = Stopwatch.StartNew();
        await DbLock.WaitAsync();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                var nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                foreach (var player in playersList)
                {
                    var uid = !string.IsNullOrWhiteSpace(player.Uid)
                        ? player.Uid.Trim()
                        : (player.Guid?.Trim() ?? string.Empty);

                    if (string.IsNullOrWhiteSpace(uid)) continue;

                    string? existingName = null;
                    string? existingComment = null;
                    bool existingWatchlisted = false;

                    const string queryExistingSql = "SELECT Name, Comment, IsWatchlisted FROM Players WHERE Uid = @Uid LIMIT 1;";
                    await using (var checkCmd = connection.CreateCommand())
                    {
                        checkCmd.Transaction = (SqliteTransaction)transaction;
                        checkCmd.CommandText = queryExistingSql;
                        checkCmd.Parameters.AddWithValue("@Uid", uid);
                        await using var reader = await checkCmd.ExecuteReaderAsync();
                        if (await reader.ReadAsync())
                        {
                            existingName = reader.IsDBNull(0) ? null : reader.GetString(0);
                            existingComment = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            existingWatchlisted = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;
                        }
                    }

                    if (existingName != null)
                    {
                        bool nameChanged = !string.IsNullOrWhiteSpace(player.Name) && !string.Equals(existingName, player.Name, StringComparison.Ordinal);

                        if (nameChanged)
                        {
                            const string insertAliasSql = @"
                                INSERT INTO PlayerAliases (PlayerUid, AliasName, FirstSeenUtc, LastSeenUtc)
                                VALUES (@PlayerUid, @AliasName, @NowUtc, @NowUtc)
                                ON CONFLICT(PlayerUid, AliasName) DO UPDATE SET LastSeenUtc = excluded.LastSeenUtc;
                            ";

                            await using var aliasCmd = connection.CreateCommand();
                            aliasCmd.Transaction = (SqliteTransaction)transaction;
                            aliasCmd.CommandText = insertAliasSql;
                            aliasCmd.Parameters.AddWithValue("@PlayerUid", uid);
                            aliasCmd.Parameters.AddWithValue("@AliasName", existingName);
                            aliasCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
                            await aliasCmd.ExecuteNonQueryAsync();

                            AppLogger.Info($"[PlayerDatabase] Name change detected for player {uid}: '{existingName}' -> '{player.Name}'. Recorded in aliases.");
                        }

                        const string updateSql = @"
                            UPDATE Players SET
                                Guid = CASE WHEN @Guid <> '' THEN @Guid ELSE Guid END,
                                Name = @Name,
                                LastIp = @LastIp,
                                LastPort = @LastPort,
                                Ping = @Ping,
                                IsOnline = 1,
                                CountryCode = @CountryCode,
                                CountryName = @CountryName,
                                Location = @Location,
                                HasAliases = CASE WHEN @NameChanged = 1 THEN 1 ELSE HasAliases END,
                                LastSeenUtc = @LastSeenUtc
                            WHERE Uid = @Uid;
                        ";

                        await using (var updateCmd = connection.CreateCommand())
                        {
                            updateCmd.Transaction = (SqliteTransaction)transaction;
                            updateCmd.CommandText = updateSql;
                            updateCmd.Parameters.AddWithValue("@Uid", uid);
                            updateCmd.Parameters.AddWithValue("@Guid", player.Guid ?? string.Empty);
                            updateCmd.Parameters.AddWithValue("@Name", player.Name);
                            updateCmd.Parameters.AddWithValue("@LastIp", player.Ip);
                            updateCmd.Parameters.AddWithValue("@LastPort", player.Port);
                            updateCmd.Parameters.AddWithValue("@Ping", player.Ping);
                            updateCmd.Parameters.AddWithValue("@CountryCode", player.Country.Code);
                            updateCmd.Parameters.AddWithValue("@CountryName", player.Country.Name);
                            updateCmd.Parameters.AddWithValue("@Location", player.DisplayLocation);
                            updateCmd.Parameters.AddWithValue("@NameChanged", nameChanged ? 1 : 0);
                            updateCmd.Parameters.AddWithValue("@LastSeenUtc", nowUtc);
                            await updateCmd.ExecuteNonQueryAsync();
                        }

                        player.Comment = existingComment ?? string.Empty;
                        player.IsWatchlisted = existingWatchlisted;

                        var aliases = await GetAliasesForPlayerInternalAsync(connection, (SqliteTransaction)transaction, uid);
                        player.Aliases = aliases;
                        player.HasAliases = aliases.Count > 0;
                    }
                    else
                    {
                        const string insertSql = @"
                            INSERT INTO Players (
                                Uid, Guid, Name, LastIp, LastPort, Ping, IsOnline, Comment,
                                IsWatchlisted, HasAliases, CountryCode, CountryName, Location,
                                FirstSeenUtc, LastSeenUtc
                            ) VALUES (
                                @Uid, @Guid, @Name, @LastIp, @LastPort, @Ping, 1, @Comment,
                                @IsWatchlisted, 0, @CountryCode, @CountryName, @Location,
                                @NowUtc, @NowUtc
                            );
                        ";

                        await using var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = (SqliteTransaction)transaction;
                        insertCmd.CommandText = insertSql;
                        insertCmd.Parameters.AddWithValue("@Uid", uid);
                        insertCmd.Parameters.AddWithValue("@Guid", player.Guid ?? string.Empty);
                        insertCmd.Parameters.AddWithValue("@Name", player.Name);
                        insertCmd.Parameters.AddWithValue("@LastIp", player.Ip);
                        insertCmd.Parameters.AddWithValue("@LastPort", player.Port);
                        insertCmd.Parameters.AddWithValue("@Ping", player.Ping);
                        insertCmd.Parameters.AddWithValue("@Comment", player.Comment ?? string.Empty);
                        insertCmd.Parameters.AddWithValue("@IsWatchlisted", player.IsWatchlisted ? 1 : 0);
                        insertCmd.Parameters.AddWithValue("@CountryCode", player.Country.Code);
                        insertCmd.Parameters.AddWithValue("@CountryName", player.Country.Name);
                        insertCmd.Parameters.AddWithValue("@Location", player.DisplayLocation);
                        insertCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
                        await insertCmd.ExecuteNonQueryAsync();

                        AppLogger.Info($"[PlayerDatabase] Inserted new player record into SQLite: {player.Name} (UID: {uid})");
                    }
                }

                await transaction.CommitAsync();
                sw.Stop();
                AppLogger.Debug($"[PlayerDatabase] Batch recorded {playersList.Count} active players in {sw.ElapsedMilliseconds} ms.");
            }
            catch (Exception txEx)
            {
                await transaction.RollbackAsync();
                AppLogger.Error("[PlayerDatabase] Transaction rollback during RecordSeenPlayersAsync.", txEx);
                throw;
            }
        }
        catch (SqliteException sqlEx)
        {
            AppLogger.Error($"[PlayerDatabase] SQLite error in RecordSeenPlayersAsync: {sqlEx.Message} (Error code: {sqlEx.SqliteErrorCode})", sqlEx);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed updating SQLite player records.", "SQLITE_ERR");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PlayerDatabase] Unexpected error during RecordSeenPlayersAsync.", ex);
        }
        finally
        {
            DbLock.Release();
        }
    }

    private static async Task<List<string>> GetAliasesForPlayerInternalAsync(SqliteConnection connection, SqliteTransaction transaction, string uid)
    {
        var aliases = new List<string>();
        const string querySql = "SELECT AliasName FROM PlayerAliases WHERE PlayerUid = @PlayerUid ORDER BY LastSeenUtc DESC;";
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = querySql;
        cmd.Parameters.AddWithValue("@PlayerUid", uid);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0))
            {
                aliases.Add(reader.GetString(0));
            }
        }
        return aliases;
    }

    public static async Task SetAllOfflineAsync()
    {
        await InitializeAsync();
        await DbLock.WaitAsync();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string updateSql = "UPDATE Players SET IsOnline = 0 WHERE IsOnline = 1;";
            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            int affected = await command.ExecuteNonQueryAsync();
            AppLogger.Info($"[PlayerDatabase] Set {affected} player record(s) to offline status in SQLite database.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PlayerDatabase] Error setting all players offline in SQLite.", ex);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<List<DatabasePlayerModel>> GetAllAsync()
    {
        await InitializeAsync();
        await DbLock.WaitAsync();

        var result = new List<DatabasePlayerModel>();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string queryPlayersSql = @"
                SELECT 
                    Uid, Guid, Name, LastIp, LastPort, Ping, IsOnline, Comment,
                    IsWatchlisted, HasAliases, CountryCode, CountryName, Location, LastSeenUtc
                FROM Players
                ORDER BY LastSeenUtc DESC;
            ";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryPlayersSql;
                await using var reader = await cmd.ExecuteReaderAsync();
                int index = 1;

                while (await reader.ReadAsync())
                {
                    var uid = reader.GetString(0);
                    var guid = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var name = reader.GetString(2);
                    var ip = reader.IsDBNull(3) ? "127.0.0.1" : reader.GetString(3);
                    var port = reader.IsDBNull(4) ? 2304 : reader.GetInt32(4);
                    var ping = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                    var isOnline = !reader.IsDBNull(6) && reader.GetInt32(6) == 1;
                    var comment = reader.IsDBNull(7) ? string.Empty : reader.GetString(7);
                    var isWatchlisted = !reader.IsDBNull(8) && reader.GetInt32(8) == 1;
                    var hasAliases = !reader.IsDBNull(9) && reader.GetInt32(9) == 1;
                    var countryCode = reader.IsDBNull(10) ? "un" : reader.GetString(10);
                    var countryName = reader.IsDBNull(11) ? "Unknown Region" : reader.GetString(11);
                    var location = reader.IsDBNull(12) ? string.Empty : reader.GetString(12);
                    var lastSeenStr = reader.GetString(13);

                    DateTime.TryParse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen);

                    result.Add(new DatabasePlayerModel
                    {
                        Id = index++,
                        Uid = uid,
                        Guid = guid,
                        Name = name,
                        LastIp = ip,
                        LastPort = port,
                        Ping = ping,
                        IsOnline = isOnline,
                        Comment = comment,
                        IsWatchlisted = isWatchlisted,
                        HasAliases = hasAliases,
                        Country = new CountryInfo { Code = countryCode, Name = countryName },
                        Location = location,
                        LastSeen = lastSeen
                    });
                }
            }

            const string queryAllAliasesSql = "SELECT PlayerUid, AliasName FROM PlayerAliases ORDER BY LastSeenUtc DESC;";
            var aliasesMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            await using (var aliasCmd = connection.CreateCommand())
            {
                aliasCmd.CommandText = queryAllAliasesSql;
                await using var aliasReader = await aliasCmd.ExecuteReaderAsync();
                while (await aliasReader.ReadAsync())
                {
                    var pUid = aliasReader.GetString(0);
                    var alias = aliasReader.GetString(1);
                    if (!aliasesMap.TryGetValue(pUid, out var list))
                    {
                        list = [];
                        aliasesMap[pUid] = list;
                    }
                    list.Add(alias);
                }
            }

            foreach (var player in result)
            {
                if (aliasesMap.TryGetValue(player.Uid, out var aliases))
                {
                    player.Aliases = aliases;
                    player.HasAliases = aliases.Count > 0;
                }
            }

            AppLogger.Debug($"[PlayerDatabase] Retrieved {result.Count} player records from SQLite.");
            return result;
        }
        catch (SqliteException sqlEx)
        {
            AppLogger.Error($"[PlayerDatabase] SQLite query error in GetAllAsync: {sqlEx.Message}", sqlEx);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed querying players from database.", "SQLITE_QUERY_ERR");
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PlayerDatabase] Unexpected error querying all players from SQLite.", ex);
            return [];
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task UpdateCommentAsync(string identifier, string comment)
    {
        await InitializeAsync();
        if (string.IsNullOrWhiteSpace(identifier)) return;

        await DbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string updateSql = "UPDATE Players SET Comment = @Comment WHERE Uid = @Id OR Guid = @Id;";
            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            command.Parameters.AddWithValue("@Comment", comment ?? string.Empty);
            command.Parameters.AddWithValue("@Id", identifier.Trim());
            int affected = await command.ExecuteNonQueryAsync();

            AppLogger.Info($"[PlayerDatabase] Updated comment for player '{identifier}' (Rows affected: {affected}).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDatabase] Failed updating comment for player '{identifier}' in SQLite.", ex);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed saving comment to database.", "SQLITE_COMMENT_ERR");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task SetWatchlistStatusAsync(string identifier, bool isWatchlisted)
    {
        await InitializeAsync();
        if (string.IsNullOrWhiteSpace(identifier)) return;

        await DbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string updateSql = "UPDATE Players SET IsWatchlisted = @IsWatchlisted WHERE Uid = @Id OR Guid = @Id;";
            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            command.Parameters.AddWithValue("@IsWatchlisted", isWatchlisted ? 1 : 0);
            command.Parameters.AddWithValue("@Id", identifier.Trim());
            int affected = await command.ExecuteNonQueryAsync();

            AppLogger.Info($"[PlayerDatabase] Set watchlist status to {isWatchlisted} for player '{identifier}' (Rows affected: {affected}).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDatabase] Failed setting watchlist status for player '{identifier}'.", ex);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed updating watchlist status.", "SQLITE_WATCHLIST_ERR");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task ToggleWatchlistAsync(string identifier)
    {
        await InitializeAsync();
        if (string.IsNullOrWhiteSpace(identifier)) return;

        await DbLock.WaitAsync();
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string toggleSql = @"
                UPDATE Players 
                SET IsWatchlisted = CASE WHEN IsWatchlisted = 1 THEN 0 ELSE 1 END 
                WHERE Uid = @Id OR Guid = @Id;
            ";

            await using var command = connection.CreateCommand();
            command.CommandText = toggleSql;
            command.Parameters.AddWithValue("@Id", identifier.Trim());
            int affected = await command.ExecuteNonQueryAsync();

            AppLogger.Info($"[PlayerDatabase] Toggled watchlist status for player '{identifier}' (Rows affected: {affected}).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDatabase] Failed toggling watchlist status for player '{identifier}'.", ex);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed toggling watchlist status.", "SQLITE_WATCHLIST_ERR");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task ClearAsync()
    {
        await InitializeAsync();
        await DbLock.WaitAsync();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                await using (var delAliases = connection.CreateCommand())
                {
                    delAliases.Transaction = (SqliteTransaction)transaction;
                    delAliases.CommandText = "DELETE FROM PlayerAliases;";
                    await delAliases.ExecuteNonQueryAsync();
                }

                await using (var delPlayers = connection.CreateCommand())
                {
                    delPlayers.Transaction = (SqliteTransaction)transaction;
                    delPlayers.CommandText = "DELETE FROM Players;";
                    await delPlayers.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            await using (var vacuumCmd = connection.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                await vacuumCmd.ExecuteNonQueryAsync();
            }

            AppLogger.Info("[PlayerDatabase] Purged entire player database and executed VACUUM in SQLite.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PlayerDatabase] Error clearing player database in SQLite.", ex);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed clearing player database.", "SQLITE_PURGE_ERR");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<DatabaseStatistics> GetDatabaseStatisticsAsync()
    {
        await InitializeAsync();
        await DbLock.WaitAsync();

        int totalPlayers = 0;
        int totalAliases = 0;
        int onlinePlayers = 0;
        int watchlistedPlayers = 0;

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string statsSql = @"
                SELECT 
                    (SELECT COUNT(*) FROM Players),
                    (SELECT COUNT(*) FROM PlayerAliases),
                    (SELECT COUNT(*) FROM Players WHERE IsOnline = 1),
                    (SELECT COUNT(*) FROM Players WHERE IsWatchlisted = 1);
            ";

            await using var command = connection.CreateCommand();
            command.CommandText = statsSql;
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                totalPlayers = reader.GetInt32(0);
                totalAliases = reader.GetInt32(1);
                onlinePlayers = reader.GetInt32(2);
                watchlistedPlayers = reader.GetInt32(3);
            }

            long dbSize = File.Exists(DatabaseFile) ? new FileInfo(DatabaseFile).Length : 0;
            var walFile = $"{DatabaseFile}-wal";
            long walSize = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;

            return new DatabaseStatistics(totalPlayers, totalAliases, onlinePlayers, watchlistedPlayers, dbSize, walSize, DatabaseFile);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PlayerDatabase] Failed querying SQLite database statistics.", ex);
            return new DatabaseStatistics(0, 0, 0, 0, 0, 0, DatabaseFile);
        }
        finally
        {
            DbLock.Release();
        }
    }
}