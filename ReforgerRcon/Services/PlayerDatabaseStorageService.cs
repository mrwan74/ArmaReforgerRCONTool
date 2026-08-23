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

            using var timing = AppLogger.Measure("PlayerDatabaseStorageService.InitializeAsync");
            using var op = Operation.Begin("Initialize SQLite Database Engine at {DatabaseFile}", DatabaseFile);
            AppLogger.Info($"[PlayerDatabase] Initializing SQLite database engine at '{DatabaseFile}'...");

            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
                AppLogger.Debug($"[PlayerDatabase] Created storage directory: {StorageDirectory}");
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            AppLogger.Debug($"[PlayerDatabase] Connection opened successfully. Server version: {connection.ServerVersion}");

            await ExecutePragmasAsync(connection);
            await CreateSchemaAsync(connection);

            _isInitialized = true;
            op.Complete();
            AppLogger.Info("[PlayerDatabase] SQLite engine initialization completed.");
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

        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.ExecutePragmas");
        await using var command = connection.CreateCommand();
        command.CommandText = pragmaSql;
        await command.ExecuteNonQueryAsync();
        AppLogger.Debug("[PlayerDatabase] Applied WAL mode and performance PRAGMAs.");
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection)
    {
        const string schemaSql = @"
            CREATE TABLE IF NOT EXISTS Players (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReforgerUid TEXT NOT NULL DEFAULT '',
                BattlEyeGuid TEXT NOT NULL DEFAULT '',
                Name TEXT NOT NULL,
                LastIp TEXT NOT NULL DEFAULT '127.0.0.1',
                LastPort INTEGER NOT NULL DEFAULT 2304,
                Ping INTEGER NOT NULL DEFAULT 0,
                IsOnline INTEGER NOT NULL DEFAULT 0,
                Comment TEXT NOT NULL DEFAULT '',
                IsWatchlisted INTEGER NOT NULL DEFAULT 0,
                HasAliases INTEGER NOT NULL DEFAULT 0,
                CountryCode TEXT NOT NULL DEFAULT 'xx',
                CountryName TEXT NOT NULL DEFAULT 'Unknown Region',
                Location TEXT NOT NULL DEFAULT '',
                TimeZone TEXT NOT NULL DEFAULT '',
                FirstSeenUtc TEXT NOT NULL,
                LastSeenUtc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PlayerAliases (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayerId INTEGER NOT NULL,
                AliasName TEXT NOT NULL,
                FirstSeenUtc TEXT NOT NULL,
                LastSeenUtc TEXT NOT NULL,
                FOREIGN KEY (PlayerId) REFERENCES Players(Id) ON DELETE CASCADE,
                UNIQUE(PlayerId, AliasName)
            );

            CREATE INDEX IF NOT EXISTS IX_Players_Name ON Players(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_Players_ReforgerUid ON Players(ReforgerUid);
            CREATE INDEX IF NOT EXISTS IX_Players_BattlEyeGuid ON Players(BattlEyeGuid);
            CREATE INDEX IF NOT EXISTS IX_Players_LastSeenUtc ON Players(LastSeenUtc);
            CREATE INDEX IF NOT EXISTS IX_Players_IsWatchlisted ON Players(IsWatchlisted);
            CREATE INDEX IF NOT EXISTS IX_Players_IsOnline ON Players(IsOnline);
            CREATE INDEX IF NOT EXISTS IX_PlayerAliases_PlayerId ON PlayerAliases(PlayerId);
            CREATE INDEX IF NOT EXISTS IX_PlayerAliases_AliasName ON PlayerAliases(AliasName COLLATE NOCASE);
        ";

        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.CreateSchema");
        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync();
        AppLogger.Info("[PlayerDatabase] Unified SQLite schema verified and indexed.");
    }

    public static async Task RecordSeenPlayersAsync(IEnumerable<PlayerModel> activePlayers)
    {
        await InitializeAsync();
        var playersList = activePlayers.ToList();
        if (playersList.Count == 0)
        {
            AppLogger.Trace("[PlayerDatabase] RecordSeenPlayersAsync called with 0 players. Skipped.");
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.RecordSeenPlayersAsync({playersList.Count} players)");
        await DbLock.WaitAsync();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            int insertedCount = 0;
            int updatedCount = 0;
            int aliasCount = 0;

            try
            {
                var nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                foreach (var player in playersList)
                {
                    string reforgerUid = player.ReforgerUid;
                    string beGuid = player.BattlEyeGuid;

                    if (string.IsNullOrWhiteSpace(reforgerUid) && !string.IsNullOrWhiteSpace(player.Uid) && player.Uid.Length == 36 && player.Uid.Contains('-'))
                    {
                        reforgerUid = player.Uid.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(beGuid) && !string.IsNullOrWhiteSpace(player.Guid) && player.Guid.Length == 32 && !player.Guid.Contains('-'))
                    {
                        beGuid = player.Guid.Trim();
                    }
                    else if (string.IsNullOrWhiteSpace(beGuid) && !string.IsNullOrWhiteSpace(player.Uid) && player.Uid.Length == 32 && !player.Uid.Contains('-'))
                    {
                        beGuid = player.Uid.Trim();
                    }

                    if (string.IsNullOrWhiteSpace(reforgerUid) && string.IsNullOrWhiteSpace(beGuid) && string.IsNullOrWhiteSpace(player.Name))
                    {
                        AppLogger.Warn($"[PlayerDatabase] Skipping unidentifiable player row (ID: #{player.Id}).");
                        continue;
                    }

                    int existingDbId = 0;
                    string existingName = string.Empty;
                    string existingComment = string.Empty;
                    bool existingWatchlisted = false;
                    string existingReforgerUid = string.Empty;
                    string existingBeGuid = string.Empty;

                    const string findExistingSql = @"
                        SELECT Id, Name, Comment, IsWatchlisted, ReforgerUid, BattlEyeGuid 
                        FROM Players 
                        WHERE (@ReforgerUid <> '' AND ReforgerUid = @ReforgerUid)
                           OR (@BattlEyeGuid <> '' AND BattlEyeGuid = @BattlEyeGuid)
                           OR (Name = @Name AND @Name <> '')
                        ORDER BY 
                           CASE WHEN (@ReforgerUid <> '' AND ReforgerUid = @ReforgerUid) THEN 1
                                WHEN (@BattlEyeGuid <> '' AND BattlEyeGuid = @BattlEyeGuid) THEN 2
                                ELSE 3 END
                        LIMIT 1;
                    ";

                    await using (var checkCmd = connection.CreateCommand())
                    {
                        checkCmd.Transaction = (SqliteTransaction)transaction;
                        checkCmd.CommandText = findExistingSql;
                        checkCmd.Parameters.AddWithValue("@ReforgerUid", reforgerUid);
                        checkCmd.Parameters.AddWithValue("@BattlEyeGuid", beGuid);
                        checkCmd.Parameters.AddWithValue("@Name", player.Name);

                        await using var reader = await checkCmd.ExecuteReaderAsync();
                        if (await reader.ReadAsync())
                        {
                            existingDbId = reader.GetInt32(0);
                            existingName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                            existingComment = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                            existingWatchlisted = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;
                            existingReforgerUid = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                            existingBeGuid = reader.IsDBNull(5) ? string.Empty : reader.GetString(5);
                        }
                    }

                    if (existingDbId > 0)
                    {
                        bool nameChanged = !string.IsNullOrWhiteSpace(player.Name) && !string.Equals(existingName, player.Name, StringComparison.Ordinal);

                        if (nameChanged && !string.IsNullOrWhiteSpace(existingName))
                        {
                            const string insertAliasSql = @"
                                INSERT INTO PlayerAliases (PlayerId, AliasName, FirstSeenUtc, LastSeenUtc)
                                VALUES (@PlayerId, @AliasName, @NowUtc, @NowUtc)
                                ON CONFLICT(PlayerId, AliasName) DO UPDATE SET LastSeenUtc = excluded.LastSeenUtc;
                            ";

                            await using var aliasCmd = connection.CreateCommand();
                            aliasCmd.Transaction = (SqliteTransaction)transaction;
                            aliasCmd.CommandText = insertAliasSql;
                            aliasCmd.Parameters.AddWithValue("@PlayerId", existingDbId);
                            aliasCmd.Parameters.AddWithValue("@AliasName", existingName);
                            aliasCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
                            await aliasCmd.ExecuteNonQueryAsync();

                            aliasCount++;
                            AppLogger.Info($"[PlayerDatabase] Name change detected for DB ID #{existingDbId}: '{existingName}' -> '{player.Name}'. Alias recorded.");
                        }

                        var mergedReforgerUid = !string.IsNullOrWhiteSpace(reforgerUid) ? reforgerUid : existingReforgerUid;
                        var mergedBeGuid = !string.IsNullOrWhiteSpace(beGuid) ? beGuid : existingBeGuid;

                        const string updateSql = @"
                            UPDATE Players SET
                                ReforgerUid = @ReforgerUid,
                                BattlEyeGuid = @BattlEyeGuid,
                                Name = @Name,
                                LastIp = CASE WHEN @LastIp <> '127.0.0.1' AND @LastIp <> 'N/A' THEN @LastIp ELSE LastIp END,
                                LastPort = CASE WHEN @LastPort > 0 THEN @LastPort ELSE LastPort END,
                                Ping = CASE WHEN @Ping > 0 THEN @Ping ELSE Ping END,
                                IsOnline = 1,
                                CountryCode = CASE WHEN @CountryCode <> 'xx' THEN @CountryCode ELSE CountryCode END,
                                CountryName = CASE WHEN @CountryName <> 'Unknown Region' THEN @CountryName ELSE CountryName END,
                                Location = CASE WHEN @Location <> '' AND @Location <> 'Unknown Region' THEN @Location ELSE Location END,
                                TimeZone = CASE WHEN @TimeZone <> '' THEN @TimeZone ELSE TimeZone END,
                                HasAliases = CASE WHEN @NameChanged = 1 THEN 1 ELSE HasAliases END,
                                LastSeenUtc = @LastSeenUtc
                            WHERE Id = @Id;
                        ";

                        await using (var updateCmd = connection.CreateCommand())
                        {
                            updateCmd.Transaction = (SqliteTransaction)transaction;
                            updateCmd.CommandText = updateSql;
                            updateCmd.Parameters.AddWithValue("@Id", existingDbId);
                            updateCmd.Parameters.AddWithValue("@ReforgerUid", mergedReforgerUid);
                            updateCmd.Parameters.AddWithValue("@BattlEyeGuid", mergedBeGuid);
                            updateCmd.Parameters.AddWithValue("@Name", player.Name);
                            updateCmd.Parameters.AddWithValue("@LastIp", player.Ip);
                            updateCmd.Parameters.AddWithValue("@LastPort", player.Port);
                            updateCmd.Parameters.AddWithValue("@Ping", player.Ping);
                            updateCmd.Parameters.AddWithValue("@CountryCode", player.Country.Code);
                            updateCmd.Parameters.AddWithValue("@CountryName", player.Country.Name);
                            updateCmd.Parameters.AddWithValue("@Location", player.DisplayLocation);
                            updateCmd.Parameters.AddWithValue("@TimeZone", player.TimeZone);
                            updateCmd.Parameters.AddWithValue("@NameChanged", nameChanged ? 1 : 0);
                            updateCmd.Parameters.AddWithValue("@LastSeenUtc", nowUtc);
                            await updateCmd.ExecuteNonQueryAsync();
                        }

                        player.ReforgerUid = mergedReforgerUid;
                        player.BattlEyeGuid = mergedBeGuid;
                        player.Comment = existingComment;
                        player.IsWatchlisted = existingWatchlisted;

                        var aliases = await GetAliasesForPlayerIdInternalAsync(connection, (SqliteTransaction)transaction, existingDbId);
                        player.Aliases = aliases;
                        player.HasAliases = aliases.Count > 0;
                        updatedCount++;
                    }
                    else
                    {
                        const string insertSql = @"
                            INSERT INTO Players (
                                ReforgerUid, BattlEyeGuid, Name, LastIp, LastPort, Ping, IsOnline, Comment,
                                IsWatchlisted, HasAliases, CountryCode, CountryName, Location, TimeZone,
                                FirstSeenUtc, LastSeenUtc
                            ) VALUES (
                                @ReforgerUid, @BattlEyeGuid, @Name, @LastIp, @LastPort, @Ping, 1, @Comment,
                                @IsWatchlisted, 0, @CountryCode, @CountryName, @Location, @TimeZone,
                                @NowUtc, @NowUtc
                            );
                        ";

                        await using var insertCmd = connection.CreateCommand();
                        insertCmd.Transaction = (SqliteTransaction)transaction;
                        insertCmd.CommandText = insertSql;
                        insertCmd.Parameters.AddWithValue("@ReforgerUid", reforgerUid);
                        insertCmd.Parameters.AddWithValue("@BattlEyeGuid", beGuid);
                        insertCmd.Parameters.AddWithValue("@Name", player.Name);
                        insertCmd.Parameters.AddWithValue("@LastIp", player.Ip);
                        insertCmd.Parameters.AddWithValue("@LastPort", player.Port);
                        insertCmd.Parameters.AddWithValue("@Ping", player.Ping);
                        insertCmd.Parameters.AddWithValue("@Comment", player.Comment ?? string.Empty);
                        insertCmd.Parameters.AddWithValue("@IsWatchlisted", player.IsWatchlisted ? 1 : 0);
                        insertCmd.Parameters.AddWithValue("@CountryCode", player.Country.Code);
                        insertCmd.Parameters.AddWithValue("@CountryName", player.Country.Name);
                        insertCmd.Parameters.AddWithValue("@Location", player.DisplayLocation);
                        insertCmd.Parameters.AddWithValue("@TimeZone", player.TimeZone);
                        insertCmd.Parameters.AddWithValue("@NowUtc", nowUtc);
                        await insertCmd.ExecuteNonQueryAsync();

                        insertedCount++;
                        AppLogger.Info($"[PlayerDatabase] Inserted new unified player record: '{player.Name}' (ReforgerUID: '{reforgerUid}', BE-GUID: '{beGuid}')");
                    }
                }

                await transaction.CommitAsync();
                AppLogger.Debug($"[PlayerDatabase] Batch recorded {playersList.Count} active players (Inserted: {insertedCount}, Updated: {updatedCount}, New Aliases: {aliasCount}).");
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
            AppLogger.Error($"[PlayerDatabase] SQLite error in RecordSeenPlayersAsync: {sqlEx.Message} (Code: {sqlEx.SqliteErrorCode})", sqlEx);
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

    private static async Task<List<string>> GetAliasesForPlayerIdInternalAsync(SqliteConnection connection, SqliteTransaction transaction, int playerId)
    {
        var aliases = new List<string>();
        const string querySql = "SELECT AliasName FROM PlayerAliases WHERE PlayerId = @PlayerId ORDER BY LastSeenUtc DESC;";
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = querySql;
        cmd.Parameters.AddWithValue("@PlayerId", playerId);
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
        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.SetAllOfflineAsync");
        await DbLock.WaitAsync();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string updateSql = "UPDATE Players SET IsOnline = 0 WHERE IsOnline = 1;";
            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            int affected = await command.ExecuteNonQueryAsync();
            AppLogger.Info($"[PlayerDatabase] Set {affected} player record(s) to offline status.");
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
        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.GetAllAsync");
        await DbLock.WaitAsync();

        var result = new List<DatabasePlayerModel>();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string queryAllAliasesSql = "SELECT PlayerId, AliasName FROM PlayerAliases ORDER BY LastSeenUtc DESC;";
            var aliasesMap = new Dictionary<int, List<string>>();

            await using (var aliasCmd = connection.CreateCommand())
            {
                aliasCmd.CommandText = queryAllAliasesSql;
                await using var aliasReader = await aliasCmd.ExecuteReaderAsync();
                while (await aliasReader.ReadAsync())
                {
                    var pId = aliasReader.GetInt32(0);
                    var alias = aliasReader.GetString(1);
                    if (!aliasesMap.TryGetValue(pId, out var list))
                    {
                        list = [];
                        aliasesMap[pId] = list;
                    }
                    list.Add(alias);
                }
            }

            const string queryPlayersSql = @"
                SELECT 
                    Id, ReforgerUid, BattlEyeGuid, Name, LastIp, LastPort, Ping, IsOnline, Comment,
                    IsWatchlisted, HasAliases, CountryCode, CountryName, Location, TimeZone, LastSeenUtc
                FROM Players
                ORDER BY LastSeenUtc DESC;
            ";

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = queryPlayersSql;
                await using var reader = await cmd.ExecuteReaderAsync();
                int rowNumber = 1;

                while (await reader.ReadAsync())
                {
                    var dbId = reader.GetInt32(0);
                    var reforgerUid = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    var beGuid = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var name = reader.GetString(3);
                    var ip = reader.IsDBNull(4) ? "127.0.0.1" : reader.GetString(4);
                    var port = reader.IsDBNull(5) ? 2304 : reader.GetInt32(5);
                    var ping = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                    var isOnline = !reader.IsDBNull(7) && reader.GetInt32(7) == 1;
                    var comment = reader.IsDBNull(8) ? string.Empty : reader.GetString(8);
                    var isWatchlisted = !reader.IsDBNull(9) && reader.GetInt32(9) == 1;
                    var hasAliases = !reader.IsDBNull(10) && reader.GetInt32(10) == 1;
                    var countryCode = reader.IsDBNull(11) ? "xx" : reader.GetString(11);
                    var countryName = reader.IsDBNull(12) ? "Unknown Region" : reader.GetString(12);
                    var location = reader.IsDBNull(13) ? string.Empty : reader.GetString(13);
                    var timeZone = reader.IsDBNull(14) ? string.Empty : reader.GetString(14);
                    var lastSeenStr = reader.GetString(15);

                    DateTime.TryParse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen);

                    var effectiveUid = !string.IsNullOrEmpty(reforgerUid) ? reforgerUid : beGuid;
                    var effectiveGuid = !string.IsNullOrEmpty(beGuid) ? beGuid : reforgerUid;

                    var playerModel = new DatabasePlayerModel
                    {
                        Id = rowNumber++,
                        Uid = effectiveUid,
                        Guid = effectiveGuid,
                        ReforgerUid = reforgerUid,
                        BattlEyeGuid = beGuid,
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
                        TimeZone = timeZone,
                        LastSeen = lastSeen
                    };

                    if (aliasesMap.TryGetValue(dbId, out var aliasesList))
                    {
                        playerModel.Aliases = aliasesList;
                        playerModel.HasAliases = aliasesList.Count > 0;
                    }

                    result.Add(playerModel);
                }
            }

            AppLogger.Debug($"[PlayerDatabase] Retrieved {result.Count} unified player records from SQLite with alias history attached.");
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
        if (string.IsNullOrWhiteSpace(identifier))
        {
            AppLogger.Warn("[PlayerDatabase] UpdateCommentAsync called with empty identifier.");
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.UpdateCommentAsync('{identifier}')");
        await DbLock.WaitAsync();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string updateSql = @"
                UPDATE Players 
                SET Comment = @Comment 
                WHERE ReforgerUid = @Id 
                   OR BattlEyeGuid = @Id 
                   OR Name = @Id;
            ";

            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            command.Parameters.AddWithValue("@Comment", comment ?? string.Empty);
            command.Parameters.AddWithValue("@Id", identifier.Trim());
            int affected = await command.ExecuteNonQueryAsync();

            AppLogger.Info($"[PlayerDatabase] Updated comment for player '{identifier}' (Rows affected: {affected}, Comment: '{comment}').");
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
        if (string.IsNullOrWhiteSpace(identifier))
        {
            AppLogger.Warn("[PlayerDatabase] SetWatchlistStatusAsync called with empty identifier.");
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.SetWatchlistStatusAsync('{identifier}', {isWatchlisted})");
        await DbLock.WaitAsync();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();

            const string updateSql = @"
                UPDATE Players 
                SET IsWatchlisted = @IsWatchlisted 
                WHERE ReforgerUid = @Id 
                   OR BattlEyeGuid = @Id 
                   OR Name = @Id;
            ";

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

    public static async Task ClearAsync()
    {
        await InitializeAsync();
        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.ClearAsync");
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
                    int aliasesDeleted = await delAliases.ExecuteNonQueryAsync();
                    AppLogger.Debug($"[PlayerDatabase] Deleted {aliasesDeleted} rows from PlayerAliases.");
                }

                await using (var delPlayers = connection.CreateCommand())
                {
                    delPlayers.Transaction = (SqliteTransaction)transaction;
                    delPlayers.CommandText = "DELETE FROM Players;";
                    int playersDeleted = await delPlayers.ExecuteNonQueryAsync();
                    AppLogger.Debug($"[PlayerDatabase] Deleted {playersDeleted} rows from Players.");
                }

                await transaction.CommitAsync();
            }
            catch (Exception txEx)
            {
                await transaction.RollbackAsync();
                AppLogger.Error("[PlayerDatabase] Rollback during database purge.", txEx);
                throw;
            }

            await using (var vacuumCmd = connection.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                await vacuumCmd.ExecuteNonQueryAsync();
                AppLogger.Debug("[PlayerDatabase] Executed VACUUM maintenance on SQLite database.");
            }

            AppLogger.Info("[PlayerDatabase] Purged entire player database.");
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
        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.GetDatabaseStatisticsAsync");
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

            AppLogger.Debug($"[PlayerDatabase] Queried database stats: Players={totalPlayers}, Aliases={totalAliases}, Size={dbSize / 1024.0:F1} KB.");
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