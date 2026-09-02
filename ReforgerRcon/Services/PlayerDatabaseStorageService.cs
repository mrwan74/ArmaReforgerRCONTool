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
    int TotalReforgerPlayers,
    int TotalBattlEyePlayers,
    int OnlinePlayers,
    int WatchlistedPlayers,
    long DatabaseSizeBytes,
    long WalSizeBytes,
    string DatabasePath);

public static class PlayerDatabaseStorageService
{
    private const string DatabaseErrorTitle = "Database Error";

    private const string ParamName = "@Name";
    private const string ParamGuid = "@Guid";
    private const string ParamUid = "@Uid";
    private const string ParamComment = "@Comment";
    private const string ParamIsWatchlisted = "@IsWatchlisted";
    private const string ParamHasAliases = "@HasAliases";
    private const string ParamAliases = "@Aliases";
    private const string ParamLastSeenUtc = "@LastSeenUtc";
    private const string ParamNowUtc = "@NowUtc";
    private const string ParamCountryCode = "@CountryCode";
    private const string ParamCountryName = "@CountryName";
    private const string ParamLocation = "@Location";
    private const string ParamTimeZone = "@TimeZone";
    private const string ParamLastIpPort = "@LastIpPort";
    private const string ParamPing = "@Ping";
    private const string ParamId = "@Id";

    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string DatabaseFile = Path.Combine(StorageDirectory, "player_database.db");
    private static readonly string ConnectionString = $"Data Source={DatabaseFile};Mode=ReadWriteCreate;Pooling=True;";

    private static readonly SemaphoreSlim DbLock = new(1, 1);
    private static bool _isInitialized;

    public static async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var startTimestamp = Stopwatch.GetTimestamp();
        await DbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isInitialized) return;

            SQLitePCL.Batteries_V2.Init();

            using var timing = AppLogger.Measure("PlayerDatabaseStorageService.InitializeAsync", slowThresholdMs: 50.0);
            using var op = Operation.Begin("Initialize SQLite Database Engine at {DatabaseFile}", DatabaseFile);
            var transaction = SentrySdk.StartTransaction("InitSqliteDb", "db.sqlite.init");
            AppLogger.Info($"[PlayerDatabase:Init] Initializing SQLite database engine at '{DatabaseFile}'...");

            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
                AppLogger.Debug($"[PlayerDatabase:Init] Created storage directory: {StorageDirectory}");
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            var pragmaSpan = transaction.StartChild("db.sqlite.pragmas", "Configure SQLite Pragmas");
            await ExecutePragmasAsync(connection).ConfigureAwait(false);
            pragmaSpan.Finish(SpanStatus.Ok);

            var schemaSpan = transaction.StartChild("db.sqlite.schema", "Create Database Protocol Tables and Indexes");
            await CreateSchemaAsync(connection).ConfigureAwait(false);
            schemaSpan.Finish(SpanStatus.Ok);

            _isInitialized = true;
            op.Complete();
            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[PlayerDatabase:Init] SQLite dual-protocol database engine ready in {elapsedMs:F2}ms.");
        }
        catch (SqliteException sqlEx)
        {
            AppLogger.Fatal($"[PlayerDatabase:Init] SQLite error initializing database at '{DatabaseFile}': {sqlEx.Message} (Error code: {sqlEx.SqliteErrorCode})", sqlEx);
            CrashReportService.HandleFatalException("PlayerDatabaseStorageService.InitializeAsync", sqlEx, isTerminating: false);
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Fatal($"[PlayerDatabase:Init] Unexpected fatal error initializing SQLite engine: {ex.Message}", ex);
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
        var startTimestamp = Stopwatch.GetTimestamp();
        const string pragmaSql = @"
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA temp_store = MEMORY;
        ";

        await using var command = connection.CreateCommand();
        command.CommandText = pragmaSql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Debug($"[PlayerDatabase:Pragmas] WAL PRAGMAs configured in {elapsedMs:F2}ms.");
    }

    private static async Task CreateSchemaAsync(SqliteConnection connection)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        const string schemaSql = @"
            CREATE TABLE IF NOT EXISTS BattlEyePlayers (
                BattlEyeGuid TEXT PRIMARY KEY NOT NULL DEFAULT '',
                Name TEXT NOT NULL DEFAULT '',
                LastIpPort TEXT NOT NULL DEFAULT '',
                Ping INTEGER NOT NULL DEFAULT 0,
                IsOnline INTEGER NOT NULL DEFAULT 0,
                Comment TEXT NOT NULL DEFAULT '',
                IsWatchlisted INTEGER NOT NULL DEFAULT 0,
                HasAliases INTEGER NOT NULL DEFAULT 0,
                CountryCode TEXT NOT NULL DEFAULT 'xx',
                CountryName TEXT NOT NULL DEFAULT 'Unknown Region',
                Location TEXT NOT NULL DEFAULT '',
                TimeZone TEXT NOT NULL DEFAULT '',
                Aliases TEXT NOT NULL DEFAULT '[]',
                FirstSeenUtc TEXT NOT NULL DEFAULT '',
                LastSeenUtc TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS ReforgerPlayers (
                ReforgerUid TEXT PRIMARY KEY NOT NULL DEFAULT '',
                Name TEXT NOT NULL DEFAULT '',
                IsOnline INTEGER NOT NULL DEFAULT 0,
                Comment TEXT NOT NULL DEFAULT '',
                IsWatchlisted INTEGER NOT NULL DEFAULT 0,
                HasAliases INTEGER NOT NULL DEFAULT 0,
                Aliases TEXT NOT NULL DEFAULT '[]',
                FirstSeenUtc TEXT NOT NULL DEFAULT '',
                LastSeenUtc TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS IX_BattlEyePlayers_Name ON BattlEyePlayers(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_BattlEyePlayers_LastSeenUtc ON BattlEyePlayers(LastSeenUtc);
            CREATE INDEX IF NOT EXISTS IX_BattlEyePlayers_IsWatchlisted ON BattlEyePlayers(IsWatchlisted);
            CREATE INDEX IF NOT EXISTS IX_BattlEyePlayers_IsOnline ON BattlEyePlayers(IsOnline);

            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_Name ON ReforgerPlayers(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_LastSeenUtc ON ReforgerPlayers(LastSeenUtc);
            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_IsWatchlisted ON ReforgerPlayers(IsWatchlisted);
            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_IsOnline ON ReforgerPlayers(IsOnline);
        ";

        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[PlayerDatabase:Schema] Dual-protocol SQLite schemas verified in {elapsedMs:F2}ms.");
    }

    private static List<string> ParseAliases(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(rawJson) ?? [];
        }
        catch (JsonException ex)
        {
            AppLogger.Debug($"[PlayerDatabase:Aliases] Fallback parsing aliases '{rawJson}': {ex.Message}");
            return [.. rawJson.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDatabase:Aliases] Error parsing aliases '{rawJson}': {ex.Message}", ex);
            return [];
        }
    }

    private static string SerializeAliases(IEnumerable<string> aliases)
    {
        try
        {
            return JsonSerializer.Serialize(aliases.Where(a => !string.IsNullOrWhiteSpace(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDatabase:Aliases] Error serializing aliases: {ex.Message}", ex);
            return "[]";
        }
    }

    public static async Task RecordSeenPlayersAsync(IEnumerable<PlayerModel> activePlayers, RconProtocol protocol)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await InitializeAsync().ConfigureAwait(false);
        var playersList = activePlayers.ToList();
        if (playersList.Count == 0)
        {
            AppLogger.Trace($"[PlayerDatabase:Upsert] RecordSeenPlayersAsync ({protocol}) skipped: 0 players.");
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.RecordSeenPlayersAsync({playersList.Count} players, {protocol})", slowThresholdMs: 40.0);
        var transaction = SentrySdk.StartTransaction("RecordSeenPlayers", "db.sqlite.batch_upsert");
        await DbLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var dbTransaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

            int insertedCount = 0;
            int updatedCount = 0;
            var nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            try
            {
                if (protocol == RconProtocol.BattlEye)
                {
                    foreach (var player in playersList)
                    {
                        var beGuid = player.BattlEyeGuid;
                        if (string.IsNullOrWhiteSpace(beGuid) && !string.IsNullOrWhiteSpace(player.Guid) && !player.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase))
                        {
                            beGuid = player.Guid.Trim();
                        }
                        else if (string.IsNullOrWhiteSpace(beGuid) && !string.IsNullOrWhiteSpace(player.Uid) && player.Uid.Length == 32 && !player.Uid.Contains('-'))
                        {
                            beGuid = player.Uid.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(beGuid) || beGuid.StartsWith("init", StringComparison.OrdinalIgnoreCase))
                        {
                            AppLogger.Warn($"[PlayerDatabase:Upsert] Skipping unidentifiable BattlEye player (ID: #{player.Id}, Name: '{player.Name}').");
                            continue;
                        }

                        string lastIpPort = string.Empty;
                        if (!string.IsNullOrWhiteSpace(player.Ip) && !player.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                        {
                            lastIpPort = player.Port > 0 ? $"{player.Ip}:{player.Port}" : player.Ip;
                        }

                        string existingName = string.Empty;
                        string existingComment = string.Empty;
                        bool existingWatchlisted = false;
                        string existingAliasesJson = "[]";
                        bool recordExists = false;

                        const string checkBeSql = @"
                            SELECT Name, Comment, IsWatchlisted, Aliases 
                            FROM BattlEyePlayers 
                            WHERE BattlEyeGuid = @Guid 
                            LIMIT 1;
                        ";

                        await using (var checkCmd = connection.CreateCommand())
                        {
                            checkCmd.Transaction = (SqliteTransaction)dbTransaction;
                            checkCmd.CommandText = checkBeSql;
                            checkCmd.Parameters.AddWithValue(ParamGuid, beGuid);
                            await using var reader = await checkCmd.ExecuteReaderAsync().ConfigureAwait(false);
                            if (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                recordExists = true;
                                existingName = await reader.IsDBNullAsync(0).ConfigureAwait(false) ? string.Empty : reader.GetString(0);
                                existingComment = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                                existingWatchlisted = !await reader.IsDBNullAsync(2).ConfigureAwait(false) && reader.GetInt32(2) == 1;
                                existingAliasesJson = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? "[]" : reader.GetString(3);
                            }
                        }

                        var aliasList = ParseAliases(existingAliasesJson);

                        if (recordExists)
                        {
                            bool nameChanged = !string.IsNullOrWhiteSpace(player.Name) &&
                                               !string.IsNullOrWhiteSpace(existingName) &&
                                               !string.Equals(existingName, player.Name, StringComparison.Ordinal);

                            if (nameChanged)
                            {
                                if (!aliasList.Contains(existingName, StringComparer.OrdinalIgnoreCase))
                                {
                                    aliasList.Add(existingName);
                                }
                                AppLogger.Info($"[PlayerDatabase:Upsert] Name change recorded for GUID '{beGuid}': '{existingName}' -> '{player.Name}'. Alias archived (Total: {aliasList.Count}).");
                            }

                            bool hasAliases = aliasList.Count > 0;
                            var serializedAliases = SerializeAliases(aliasList);

                            const string updateBeSql = @"
                                UPDATE BattlEyePlayers SET
                                    Name = @Name,
                                    LastIpPort = CASE WHEN @LastIpPort <> '' AND @LastIpPort <> 'N/A' THEN @LastIpPort ELSE LastIpPort END,
                                    Ping = CASE WHEN @Ping > 0 THEN @Ping ELSE Ping END,
                                    IsOnline = 1,
                                    HasAliases = @HasAliases,
                                    CountryCode = CASE WHEN @CountryCode <> 'xx' THEN @CountryCode ELSE CountryCode END,
                                    CountryName = CASE WHEN @CountryName <> 'Unknown Region' THEN @CountryName ELSE CountryName END,
                                    Location = CASE WHEN @Location <> '' AND @Location <> 'Unknown Region' THEN @Location ELSE Location END,
                                    TimeZone = CASE WHEN @TimeZone <> '' THEN @TimeZone ELSE TimeZone END,
                                    Aliases = @Aliases,
                                    LastSeenUtc = @LastSeenUtc
                                WHERE BattlEyeGuid = @Guid;
                            ";

                            await using (var updateCmd = connection.CreateCommand())
                            {
                                updateCmd.Transaction = (SqliteTransaction)dbTransaction;
                                updateCmd.CommandText = updateBeSql;
                                updateCmd.Parameters.AddWithValue(ParamGuid, beGuid);
                                updateCmd.Parameters.AddWithValue(ParamName, player.Name);
                                updateCmd.Parameters.AddWithValue(ParamLastIpPort, lastIpPort);
                                updateCmd.Parameters.AddWithValue(ParamPing, player.Ping);
                                updateCmd.Parameters.AddWithValue(ParamHasAliases, hasAliases ? 1 : 0);
                                updateCmd.Parameters.AddWithValue(ParamCountryCode, player.Country.Code);
                                updateCmd.Parameters.AddWithValue(ParamCountryName, player.Country.Name);
                                updateCmd.Parameters.AddWithValue(ParamLocation, player.DisplayLocation);
                                updateCmd.Parameters.AddWithValue(ParamTimeZone, player.TimeZone);
                                updateCmd.Parameters.AddWithValue(ParamAliases, serializedAliases);
                                updateCmd.Parameters.AddWithValue(ParamLastSeenUtc, nowUtc);
                                await updateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }

                            player.Comment = existingComment;
                            player.IsWatchlisted = existingWatchlisted;
                            player.Aliases = aliasList;
                            player.HasAliases = hasAliases;
                            updatedCount++;
                        }
                        else
                        {
                            const string insertBeSql = @"
                                INSERT INTO BattlEyePlayers (
                                    BattlEyeGuid, Name, LastIpPort, Ping, IsOnline, Comment,
                                    IsWatchlisted, HasAliases, CountryCode, CountryName, Location, TimeZone,
                                    Aliases, FirstSeenUtc, LastSeenUtc
                                ) VALUES (
                                    @Guid, @Name, @LastIpPort, @Ping, 1, @Comment,
                                    @IsWatchlisted, 0, @CountryCode, @CountryName, @Location, @TimeZone,
                                    '[]', @NowUtc, @NowUtc
                                );
                            ";

                            await using (var insertCmd = connection.CreateCommand())
                            {
                                insertCmd.Transaction = (SqliteTransaction)dbTransaction;
                                insertCmd.CommandText = insertBeSql;
                                insertCmd.Parameters.AddWithValue(ParamGuid, beGuid);
                                insertCmd.Parameters.AddWithValue(ParamName, player.Name);
                                insertCmd.Parameters.AddWithValue(ParamLastIpPort, lastIpPort);
                                insertCmd.Parameters.AddWithValue(ParamPing, player.Ping);
                                insertCmd.Parameters.AddWithValue(ParamComment, player.Comment ?? string.Empty);
                                insertCmd.Parameters.AddWithValue(ParamIsWatchlisted, player.IsWatchlisted ? 1 : 0);
                                insertCmd.Parameters.AddWithValue(ParamCountryCode, player.Country.Code);
                                insertCmd.Parameters.AddWithValue(ParamCountryName, player.Country.Name);
                                insertCmd.Parameters.AddWithValue(ParamLocation, player.DisplayLocation);
                                insertCmd.Parameters.AddWithValue(ParamTimeZone, player.TimeZone);
                                insertCmd.Parameters.AddWithValue(ParamNowUtc, nowUtc);
                                await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }

                            insertedCount++;
                            AppLogger.Info($"[PlayerDatabase:Upsert] Inserted new BattlEye player record: '{player.Name}' (BE-GUID: '{beGuid}')");
                        }
                    }
                }
                else
                {
                    foreach (var player in playersList)
                    {
                        var reforgerUid = player.ReforgerUid;
                        if (string.IsNullOrWhiteSpace(reforgerUid) && !string.IsNullOrWhiteSpace(player.Uid))
                        {
                            reforgerUid = player.Uid.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(reforgerUid))
                        {
                            AppLogger.Warn($"[PlayerDatabase:Upsert] Skipping unidentifiable Reforger player (ID: #{player.Id}, Name: '{player.Name}').");
                            continue;
                        }

                        string existingName = string.Empty;
                        string existingComment = string.Empty;
                        bool existingWatchlisted = false;
                        string existingAliasesJson = "[]";
                        bool recordExists = false;

                        const string checkReforgerSql = @"
                            SELECT Name, Comment, IsWatchlisted, Aliases 
                            FROM ReforgerPlayers 
                            WHERE ReforgerUid = @Uid 
                            LIMIT 1;
                        ";

                        await using (var checkCmd = connection.CreateCommand())
                        {
                            checkCmd.Transaction = (SqliteTransaction)dbTransaction;
                            checkCmd.CommandText = checkReforgerSql;
                            checkCmd.Parameters.AddWithValue(ParamUid, reforgerUid);
                            await using var reader = await checkCmd.ExecuteReaderAsync().ConfigureAwait(false);
                            if (await reader.ReadAsync().ConfigureAwait(false))
                            {
                                recordExists = true;
                                existingName = await reader.IsDBNullAsync(0).ConfigureAwait(false) ? string.Empty : reader.GetString(0);
                                existingComment = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                                existingWatchlisted = !await reader.IsDBNullAsync(2).ConfigureAwait(false) && reader.GetInt32(2) == 1;
                                existingAliasesJson = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? "[]" : reader.GetString(3);
                            }
                        }

                        var aliasList = ParseAliases(existingAliasesJson);

                        if (recordExists)
                        {
                            bool nameChanged = !string.IsNullOrWhiteSpace(player.Name) &&
                                               !string.IsNullOrWhiteSpace(existingName) &&
                                               !string.Equals(existingName, player.Name, StringComparison.Ordinal);

                            if (nameChanged)
                            {
                                if (!aliasList.Contains(existingName, StringComparer.OrdinalIgnoreCase))
                                {
                                    aliasList.Add(existingName);
                                }
                                AppLogger.Info($"[PlayerDatabase:Upsert] Name change recorded for UID '{reforgerUid}': '{existingName}' -> '{player.Name}'. Alias archived (Total: {aliasList.Count}).");
                            }

                            bool hasAliases = aliasList.Count > 0;
                            var serializedAliases = SerializeAliases(aliasList);

                            const string updateReforgerSql = @"
                                UPDATE ReforgerPlayers SET
                                    Name = @Name,
                                    IsOnline = 1,
                                    HasAliases = @HasAliases,
                                    Aliases = @Aliases,
                                    LastSeenUtc = @LastSeenUtc
                                WHERE ReforgerUid = @Uid;
                            ";

                            await using (var updateCmd = connection.CreateCommand())
                            {
                                updateCmd.Transaction = (SqliteTransaction)dbTransaction;
                                updateCmd.CommandText = updateReforgerSql;
                                updateCmd.Parameters.AddWithValue(ParamUid, reforgerUid);
                                updateCmd.Parameters.AddWithValue(ParamName, player.Name);
                                updateCmd.Parameters.AddWithValue(ParamHasAliases, hasAliases ? 1 : 0);
                                updateCmd.Parameters.AddWithValue(ParamAliases, serializedAliases);
                                updateCmd.Parameters.AddWithValue(ParamLastSeenUtc, nowUtc);
                                await updateCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }

                            player.Comment = existingComment;
                            player.IsWatchlisted = existingWatchlisted;
                            player.Aliases = aliasList;
                            player.HasAliases = hasAliases;
                            updatedCount++;
                        }
                        else
                        {
                            const string insertReforgerSql = @"
                                INSERT INTO ReforgerPlayers (
                                    ReforgerUid, Name, IsOnline, Comment, IsWatchlisted, HasAliases,
                                    Aliases, FirstSeenUtc, LastSeenUtc
                                ) VALUES (
                                    @Uid, @Name, 1, @Comment, @IsWatchlisted, 0,
                                    '[]', @NowUtc, @NowUtc
                                );
                            ";

                            await using (var insertCmd = connection.CreateCommand())
                            {
                                insertCmd.Transaction = (SqliteTransaction)dbTransaction;
                                insertCmd.CommandText = insertReforgerSql;
                                insertCmd.Parameters.AddWithValue(ParamUid, reforgerUid);
                                insertCmd.Parameters.AddWithValue(ParamName, player.Name);
                                insertCmd.Parameters.AddWithValue(ParamComment, player.Comment ?? string.Empty);
                                insertCmd.Parameters.AddWithValue(ParamIsWatchlisted, player.IsWatchlisted ? 1 : 0);
                                insertCmd.Parameters.AddWithValue(ParamNowUtc, nowUtc);
                                await insertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                            }

                            insertedCount++;
                            AppLogger.Info($"[PlayerDatabase:Upsert] Inserted new Reforger player record: '{player.Name}' (Reforger-UID: '{reforgerUid}')");
                        }
                    }
                }

                await dbTransaction.CommitAsync().ConfigureAwait(false);
                transaction.Finish(SpanStatus.Ok);
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                AppLogger.Debug($"[PlayerDatabase:Upsert] Batch committed for {playersList.Count} {protocol} active players in {elapsedMs:F2}ms (Inserted: {insertedCount}, Updated: {updatedCount}).");
            }
            catch (Exception txEx)
            {
                await dbTransaction.RollbackAsync().ConfigureAwait(false);
                transaction.Finish(SpanStatus.InternalError);
                AppLogger.Error($"[PlayerDatabase:Upsert] Rollback during RecordSeenPlayersAsync ({protocol}): {txEx.Message}", txEx);
                throw;
            }
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[PlayerDatabase:Upsert] SQLite error in RecordSeenPlayersAsync: {sqlEx.Message} (Code: {sqlEx.SqliteErrorCode})", sqlEx);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed updating SQLite player records.", "SQLITE_ERR");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            AppLogger.Error($"[PlayerDatabase:Upsert] Unexpected error during RecordSeenPlayersAsync ({protocol}): {ex.Message}", ex);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task SetAllOfflineAsync(RconProtocol? protocol = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.SetAllOfflineAsync({protocol?.ToString() ?? "All"})", slowThresholdMs: 30.0);
        var transaction = SentrySdk.StartTransaction("SetAllOffline", "db.sqlite.set_offline");
        await DbLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            int affected = 0;
            if (protocol == null || protocol == RconProtocol.BattlEye)
            {
                await using var cmdBe = connection.CreateCommand();
                cmdBe.CommandText = "UPDATE BattlEyePlayers SET IsOnline = 0 WHERE IsOnline = 1;";
                var beRows = await cmdBe.ExecuteNonQueryAsync().ConfigureAwait(false);
                affected += beRows;
                AppLogger.Trace($"[PlayerDatabase:Offline] Reset {beRows} BattlEye player records to offline.");
            }

            if (protocol == null || protocol == RconProtocol.ReforgerBuiltIn)
            {
                await using var cmdRef = connection.CreateCommand();
                cmdRef.CommandText = "UPDATE ReforgerPlayers SET IsOnline = 0 WHERE IsOnline = 1;";
                var refRows = await cmdRef.ExecuteNonQueryAsync().ConfigureAwait(false);
                affected += refRows;
                AppLogger.Trace($"[PlayerDatabase:Offline] Reset {refRows} Reforger player records to offline.");
            }

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[PlayerDatabase:Offline] Total {affected} player record(s) set to offline in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[PlayerDatabase:Offline] Error setting players offline in SQLite: {ex.Message}", ex);
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<List<DatabasePlayerModel>> GetAllAsync(RconProtocol protocol)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.GetAllAsync({protocol})", slowThresholdMs: 35.0);
        var transaction = SentrySdk.StartTransaction($"GetAll_{protocol}", "db.sqlite.query_all");
        await DbLock.WaitAsync().ConfigureAwait(false);

        var result = new List<DatabasePlayerModel>();

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            if (protocol == RconProtocol.BattlEye)
            {
                const string queryBeSql = @"
                    SELECT 
                        BattlEyeGuid, Name, LastIpPort, Ping, IsOnline, Comment,
                        IsWatchlisted, HasAliases, CountryCode, CountryName, Location, TimeZone,
                        Aliases, FirstSeenUtc, LastSeenUtc
                    FROM BattlEyePlayers
                    ORDER BY LastSeenUtc DESC;
                ";

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = queryBeSql;
                await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                int rowNumber = 1;

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var guid = reader.GetString(0);
                    var name = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                    var lastIpPort = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? string.Empty : reader.GetString(2);
                    var ping = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? 0 : reader.GetInt32(3);
                    var isOnline = !await reader.IsDBNullAsync(4).ConfigureAwait(false) && reader.GetInt32(4) == 1;
                    var comment = await reader.IsDBNullAsync(5).ConfigureAwait(false) ? string.Empty : reader.GetString(5);
                    var isWatchlisted = !await reader.IsDBNullAsync(6).ConfigureAwait(false) && reader.GetInt32(6) == 1;
                    var hasAliases = !await reader.IsDBNullAsync(7).ConfigureAwait(false) && reader.GetInt32(7) == 1;
                    var countryCode = await reader.IsDBNullAsync(8).ConfigureAwait(false) ? "xx" : reader.GetString(8);
                    var countryName = await reader.IsDBNullAsync(9).ConfigureAwait(false) ? "Unknown Region" : reader.GetString(9);
                    var location = await reader.IsDBNullAsync(10).ConfigureAwait(false) ? string.Empty : reader.GetString(10);
                    var timeZone = await reader.IsDBNullAsync(11).ConfigureAwait(false) ? string.Empty : reader.GetString(11);
                    var aliasesJson = await reader.IsDBNullAsync(12).ConfigureAwait(false) ? "[]" : reader.GetString(12);
                    var lastSeenStr = await reader.IsDBNullAsync(14).ConfigureAwait(false) ? string.Empty : reader.GetString(14);

                    DateTime.TryParse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen);

                    var playerModel = new DatabasePlayerModel
                    {
                        Id = rowNumber++,
                        Uid = guid,
                        Guid = guid,
                        BattlEyeGuid = guid,
                        ReforgerUid = string.Empty,
                        Name = name,
                        LastIpPort = lastIpPort,
                        Ping = ping,
                        IsOnline = isOnline,
                        Comment = comment,
                        IsWatchlisted = isWatchlisted,
                        HasAliases = hasAliases,
                        Country = new CountryInfo { Code = countryCode, Name = countryName },
                        Location = location,
                        TimeZone = timeZone,
                        Aliases = ParseAliases(aliasesJson),
                        LastSeen = lastSeen
                    };

                    result.Add(playerModel);
                }
            }
            else
            {
                const string queryReforgerSql = @"
                    SELECT 
                        ReforgerUid, Name, IsOnline, Comment, IsWatchlisted, HasAliases,
                        Aliases, FirstSeenUtc, LastSeenUtc
                    FROM ReforgerPlayers
                    ORDER BY LastSeenUtc DESC;
                ";

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = queryReforgerSql;
                await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
                int rowNumber = 1;

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    var uid = reader.GetString(0);
                    var name = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                    var isOnline = !await reader.IsDBNullAsync(2).ConfigureAwait(false) && reader.GetInt32(2) == 1;
                    var comment = await reader.IsDBNullAsync(3).ConfigureAwait(false) ? string.Empty : reader.GetString(3);
                    var isWatchlisted = !await reader.IsDBNullAsync(4).ConfigureAwait(false) && reader.GetInt32(4) == 1;
                    var hasAliases = !await reader.IsDBNullAsync(5).ConfigureAwait(false) && reader.GetInt32(5) == 1;
                    var aliasesJson = await reader.IsDBNullAsync(6).ConfigureAwait(false) ? "[]" : reader.GetString(6);
                    var lastSeenStr = await reader.IsDBNullAsync(8).ConfigureAwait(false) ? string.Empty : reader.GetString(8);

                    DateTime.TryParse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen);

                    var playerModel = new DatabasePlayerModel
                    {
                        Id = rowNumber++,
                        Uid = uid,
                        Guid = string.Empty,
                        ReforgerUid = uid,
                        BattlEyeGuid = string.Empty,
                        Name = name,
                        LastIpPort = "N/A",
                        Ping = 0,
                        IsOnline = isOnline,
                        Comment = comment,
                        IsWatchlisted = isWatchlisted,
                        HasAliases = hasAliases,
                        Country = new CountryInfo { Code = "xx", Name = "Unknown Region" },
                        Location = string.Empty,
                        TimeZone = string.Empty,
                        Aliases = ParseAliases(aliasesJson),
                        LastSeen = lastSeen
                    };

                    result.Add(playerModel);
                }
            }

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[PlayerDatabase:Query] Retrieved {result.Count} {protocol} records from SQLite in {elapsedMs:F2}ms.");
            return result;
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[PlayerDatabase:Query] SQLite error in GetAllAsync ({protocol}): {sqlEx.Message}", sqlEx);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed querying players from database.", "SQLITE_QUERY_ERR");
            return [];
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            AppLogger.Error($"[PlayerDatabase:Query] Error querying players ({protocol}) from SQLite: {ex.Message}", ex);
            return [];
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task UpdateCommentAsync(string identifier, string comment, RconProtocol protocol)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await InitializeAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            AppLogger.Warn("[PlayerDatabase:Comment] UpdateCommentAsync called with empty identifier.");
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.UpdateCommentAsync('{identifier}', {protocol})", slowThresholdMs: 25.0);
        var transaction = SentrySdk.StartTransaction("UpdateComment", "db.sqlite.update_comment");
        await DbLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            string updateSql = protocol == RconProtocol.BattlEye
                ? "UPDATE BattlEyePlayers SET Comment = @Comment WHERE BattlEyeGuid = @Id OR Name = @Id;"
                : "UPDATE ReforgerPlayers SET Comment = @Comment WHERE ReforgerUid = @Id OR Name = @Id;";

            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            command.Parameters.AddWithValue(ParamComment, comment ?? string.Empty);
            command.Parameters.AddWithValue(ParamId, identifier.Trim());
            int affected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[PlayerDatabase:Comment] Updated comment for {protocol} player '{identifier}' in {elapsedMs:F2}ms (Rows={affected}, Comment='{comment}').");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[PlayerDatabase:Comment] Failed updating comment for '{identifier}' in SQLite ({protocol}): {ex.Message}", ex);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed saving comment to database.", "SQLITE_COMMENT_ERR");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task SetWatchlistStatusAsync(string identifier, bool isWatchlisted, RconProtocol protocol)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await InitializeAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            AppLogger.Warn("[PlayerDatabase:Watchlist] SetWatchlistStatusAsync called with empty identifier.");
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.SetWatchlistStatusAsync('{identifier}', {isWatchlisted}, {protocol})", slowThresholdMs: 25.0);
        var transaction = SentrySdk.StartTransaction("SetWatchlistStatus", "db.sqlite.update_watchlist");
        await DbLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            string updateSql = protocol == RconProtocol.BattlEye
                ? "UPDATE BattlEyePlayers SET IsWatchlisted = @IsWatchlisted WHERE BattlEyeGuid = @Id OR Name = @Id;"
                : "UPDATE ReforgerPlayers SET IsWatchlisted = @IsWatchlisted WHERE ReforgerUid = @Id OR Name = @Id;";

            await using var command = connection.CreateCommand();
            command.CommandText = updateSql;
            command.Parameters.AddWithValue(ParamIsWatchlisted, isWatchlisted ? 1 : 0);
            command.Parameters.AddWithValue(ParamId, identifier.Trim());
            int affected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[PlayerDatabase:Watchlist] Set watchlist={isWatchlisted} for {protocol} player '{identifier}' in {elapsedMs:F2}ms (Rows={affected}).");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[PlayerDatabase:Watchlist] Failed updating watchlist status for '{identifier}' ({protocol}): {ex.Message}", ex);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed updating watchlist status.", "SQLITE_WATCHLIST_ERR");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task ClearDatabaseAsync(RconProtocol? protocol = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.ClearDatabaseAsync({protocol?.ToString() ?? "All"})", slowThresholdMs: 100.0);
        var transaction = SentrySdk.StartTransaction("ClearDatabase", "db.sqlite.purge");
        await DbLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var dbTransaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

            try
            {
                if (protocol == null || protocol == RconProtocol.BattlEye)
                {
                    await using var delBe = connection.CreateCommand();
                    delBe.Transaction = (SqliteTransaction)dbTransaction;
                    delBe.CommandText = "DELETE FROM BattlEyePlayers;";
                    int beDeleted = await delBe.ExecuteNonQueryAsync().ConfigureAwait(false);
                    AppLogger.Debug($"[PlayerDatabase:Purge] Deleted {beDeleted} rows from BattlEyePlayers.");
                }

                if (protocol == null || protocol == RconProtocol.ReforgerBuiltIn)
                {
                    await using var delRef = connection.CreateCommand();
                    delRef.Transaction = (SqliteTransaction)dbTransaction;
                    delRef.CommandText = "DELETE FROM ReforgerPlayers;";
                    int refDeleted = await delRef.ExecuteNonQueryAsync().ConfigureAwait(false);
                    AppLogger.Debug($"[PlayerDatabase:Purge] Deleted {refDeleted} rows from ReforgerPlayers.");
                }

                await dbTransaction.CommitAsync().ConfigureAwait(false);
            }
            catch (Exception txEx)
            {
                await dbTransaction.RollbackAsync().ConfigureAwait(false);
                AppLogger.Error($"[PlayerDatabase:Purge] Rollback during database purge: {txEx.Message}", txEx);
                throw;
            }

            await using (var vacuumCmd = connection.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                await vacuumCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                AppLogger.Debug("[PlayerDatabase:Purge] Executed VACUUM maintenance on SQLite database.");
            }

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[PlayerDatabase:Purge] Purged historical player database ({protocol?.ToString() ?? "All"}) in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[PlayerDatabase:Purge] Error clearing player database in SQLite: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowToast(DatabaseErrorTitle, "Failed clearing player database.", "SQLITE_PURGE_ERR");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<DatabaseStatistics> GetDatabaseStatisticsAsync()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.GetDatabaseStatisticsAsync", slowThresholdMs: 30.0);
        await DbLock.WaitAsync().ConfigureAwait(false);

        int totalReforger = 0;
        int totalBattlEye = 0;
        int onlinePlayers = 0;
        int watchlistedPlayers = 0;

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            const string statsSql = @"
                SELECT 
                    (SELECT COUNT(*) FROM ReforgerPlayers),
                    (SELECT COUNT(*) FROM BattlEyePlayers),
                    (SELECT COUNT(*) FROM ReforgerPlayers WHERE IsOnline = 1) + (SELECT COUNT(*) FROM BattlEyePlayers WHERE IsOnline = 1),
                    (SELECT COUNT(*) FROM ReforgerPlayers WHERE IsWatchlisted = 1) + (SELECT COUNT(*) FROM BattlEyePlayers WHERE IsWatchlisted = 1);
            ";

            await using var command = connection.CreateCommand();
            command.CommandText = statsSql;
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            if (await reader.ReadAsync().ConfigureAwait(false))
            {
                totalReforger = reader.GetInt32(0);
                totalBattlEye = reader.GetInt32(1);
                onlinePlayers = reader.GetInt32(2);
                watchlistedPlayers = reader.GetInt32(3);
            }

            long dbSize = File.Exists(DatabaseFile) ? new FileInfo(DatabaseFile).Length : 0;
            var walFile = $"{DatabaseFile}-wal";
            long walSize = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[PlayerDatabase:Stats] Stats query complete in {elapsedMs:F2}ms (Reforger={totalReforger}, BattlEye={totalBattlEye}, Online={onlinePlayers}, Watchlisted={watchlistedPlayers}, Size={dbSize / 1024.0:F1} KB, WAL={walSize / 1024.0:F1} KB).");
            return new DatabaseStatistics(totalReforger, totalBattlEye, onlinePlayers, watchlistedPlayers, dbSize, walSize, DatabaseFile);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDatabase:Stats] Failed querying SQLite database statistics: {ex.Message}", ex);
            return new DatabaseStatistics(0, 0, 0, 0, 0, 0, DatabaseFile);
        }
        finally
        {
            DbLock.Release();
        }
    }
}