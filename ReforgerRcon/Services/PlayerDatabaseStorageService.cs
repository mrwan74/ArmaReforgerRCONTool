using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

public record DatabaseQueryParameters(
    RconProtocol Protocol,
    int PageIndex = 1,
    int PageSize = 50,
    string? SearchQuery = null,
    string? SearchType = null,
    string? SortBy = null,
    bool SortAscending = true);

public record PagedResult<T>(
    List<T> Items,
    int TotalCount,
    int PageIndex,
    int PageSize);

public static class PlayerDatabaseStorageService
{
    private const string DatabaseErrorTitle = "Database Storage Error";

    private const string ContextThreadId = "thread_id";
    private const string ContextSqliteErrorCode = "sqlite_error_code";
    private const string ContextErrorMessage = "error_message";
    private const string ContextRowsAffected = "rows_affected";
    private const string ContextDurationMs = "duration_ms";

    private const string ParamName = "@Name";
    private const string ParamGuid = "@Guid";
    private const string ParamUid = "@Uid";
    private const string ParamComment = "@Comment";
    private const string ParamIsWatchlisted = "@IsWatchlisted";
    private const string ParamHasAliases = "@HasAliases";
    private const string ParamAliases = "@Aliases";
    private const string ParamNowUtc = "@NowUtc";
    private const string ParamCountryCode = "@CountryCode";
    private const string ParamCountryName = "@CountryName";
    private const string ParamLocation = "@Location";
    private const string ParamTimeZone = "@TimeZone";
    private const string ParamLastIpPort = "@LastIpPort";
    private const string ParamPing = "@Ping";
    private const string ParamId = "@Id";
    private const string ParamActiveGuidsJson = "@ActiveGuidsJson";
    private const string ParamActiveUidsJson = "@ActiveUidsJson";
    private const string ParamSearchQuery = "@SearchQuery";
    private const string ParamLimit = "@Limit";
    private const string ParamOffset = "@Offset";

    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string DatabaseFile = Path.Combine(StorageDirectory, "player_database.db");
    private static readonly string ConnectionString = $"Data Source={DatabaseFile};Mode=ReadWriteCreate;Cache=Shared;Pooling=True;";

    private static readonly SemaphoreSlim DbLock = new(1, 1);
    private static volatile bool _isInitialized;

    public static async Task InitializeAsync()
    {
        if (_isInitialized) return;

        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            [ContextThreadId] = threadId,
            ["db_file"] = DatabaseFile
        };

        AppLogger.Debug("[PlayerDatabase:Init] Acquiring SQLite initialization lock...", context);
        await DbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isInitialized) return;

            AppLogger.Debug("[PlayerDatabase:Init] Initializing SQLitePCL provider batteries...", context);
            SQLitePCL.Batteries_V2.Init();

            using var timing = AppLogger.Measure("PlayerDatabaseStorageService.InitializeAsync");
            using var op = Operation.Begin("Initialize SQLite Database Engine at {DatabaseFile}", DatabaseFile);
            var transaction = SentrySdk.StartTransaction("InitSqliteDb", "db.sqlite.init");
            AppLogger.Info($"[PlayerDatabase:Init] Configuring SQLite relational database engine at '{DatabaseFile}'...", context);

            if (!Directory.Exists(StorageDirectory))
            {
                var createdDir = Directory.CreateDirectory(StorageDirectory);
                AppLogger.Info($"[PlayerDatabase:Init] Created SQLite storage directory: '{createdDir.FullName}'.", context);
            }

            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            AppLogger.Debug($"[PlayerDatabase:Init] SQLite physical connection opened (EngineVersion='{connection.ServerVersion}').", context);

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
            context[ContextDurationMs] = elapsedMs;
            AppLogger.Info($"[PlayerDatabase:Init] SQLite dual-protocol database schema and WAL engine ready in {elapsedMs:F2}ms.", context);
        }
        catch (SqliteException sqlEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextSqliteErrorCode] = sqlEx.SqliteErrorCode;
            context["sqlite_extended_code"] = sqlEx.SqliteExtendedErrorCode;
            AppLogger.Fatal($"[PlayerDatabase:Init] SQLite error initializing database at '{DatabaseFile}' after {elapsedMs:F2}ms: {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"SQLite engine initialization failed (Code: {sqlEx.SqliteErrorCode}): {sqlEx.Message}");
            CrashReportService.HandleFatalException("PlayerDatabaseStorageService.InitializeAsync", sqlEx, isTerminating: false);
            throw;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Fatal($"[PlayerDatabase:Init] Unexpected error initializing SQLite engine after {elapsedMs:F2}ms: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Database storage access failure: {ex.Message}");
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
        AppLogger.Debug($"[PlayerDatabase:Pragmas] WAL PRAGMAs executed in {elapsedMs:F2}ms (journal_mode=WAL, synchronous=NORMAL, busy_timeout=5000ms).");
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
            CREATE INDEX IF NOT EXISTS IX_BattlEyePlayers_CountryName ON BattlEyePlayers(CountryName COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_BattlEyePlayers_LastIpPort ON BattlEyePlayers(LastIpPort);

            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_Name ON ReforgerPlayers(Name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_LastSeenUtc ON ReforgerPlayers(LastSeenUtc);
            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_IsWatchlisted ON ReforgerPlayers(IsWatchlisted);
            CREATE INDEX IF NOT EXISTS IX_ReforgerPlayers_IsOnline ON ReforgerPlayers(IsOnline);
        ";

        await using var command = connection.CreateCommand();
        command.CommandText = schemaSql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Debug($"[PlayerDatabase:Schema] Relational database schemas and indices verified in {elapsedMs:F2}ms.");
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
            AppLogger.Warn($"[PlayerDatabase:Aliases] Failed parsing JSON aliases '{rawJson}': {ex.Message}. Falling back to delimiter parsing.");
            return [.. rawJson.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDatabase:Aliases] Unexpected error parsing aliases: {ex.Message}", ex);
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
            AppLogger.Error($"[PlayerDatabase:Aliases] Failed serializing aliases to JSON: {ex.Message}", ex);
            return "[]";
        }
    }

    public static async Task SetPlayerOfflineAsync(string identifier, RconProtocol protocol)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            AppLogger.Warn("[PlayerDatabase:Offline] SetPlayerOfflineAsync rejected: Target identifier is null or empty string.");
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            ["identifier"] = identifier,
            ["protocol"] = protocol.ToString(),
            [ContextThreadId] = threadId
        };

        await InitializeAsync().ConfigureAwait(false);
        await DbLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            string sql = protocol == RconProtocol.BattlEye
                ? "UPDATE BattlEyePlayers SET IsOnline = 0 WHERE BattlEyeGuid = @Id OR Name = @Id;"
                : "UPDATE ReforgerPlayers SET IsOnline = 0 WHERE ReforgerUid = @Id OR Name = @Id;";

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue(ParamId, identifier.Trim());
            var affected = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextRowsAffected] = affected;
            context[ContextDurationMs] = elapsedMs;

            AppLogger.Trace($"[PlayerDatabase:Offline] Marked player '{identifier}' offline in {elapsedMs:F2}ms (RowsAffected={affected}).", context);
        }
        catch (SqliteException sqlEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextSqliteErrorCode] = sqlEx.SqliteErrorCode;
            AppLogger.Error($"[PlayerDatabase:Offline] SQLite error setting '{identifier}' offline after {elapsedMs:F2}ms: {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed updating player offline status in SQLite: {sqlEx.Message}");
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[PlayerDatabase:Offline] Error setting player '{identifier}' offline after {elapsedMs:F2}ms: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Unexpected error updating player state: {ex.Message}");
        }
        finally
        {
            DbLock.Release();
        }
    }

    [SuppressMessage("Security", "S2077:Use a parameterized query instead of string formatting", Justification = "Static parameterized SQL statements utilize SQLite json_each for secure parameterization")]
    public static async Task RecordSeenPlayersAsync(IEnumerable<PlayerModel> activePlayers, RconProtocol protocol)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        await InitializeAsync().ConfigureAwait(false);
        var playersList = activePlayers.ToList();
        if (playersList.Count == 0) return;

        var context = new Dictionary<string, object?>
        {
            ["player_count"] = playersList.Count,
            ["protocol"] = protocol.ToString(),
            [ContextThreadId] = threadId
        };
        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.RecordSeenPlayersAsync({playersList.Count} players, {protocol})");
        var transaction = SentrySdk.StartTransaction("RecordSeenPlayers", "db.sqlite.batch_upsert");
        await DbLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var dbTransaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

            int updatedCount = 0;
            var nowUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            try
            {
                if (protocol == RconProtocol.BattlEye)
                {
                    var activeGuids = playersList
                        .Select(p => !string.IsNullOrWhiteSpace(p.BattlEyeGuid) ? p.BattlEyeGuid : p.Guid)
                        .Where(g => !string.IsNullOrWhiteSpace(g) && !g.StartsWith("init", StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    context["distinct_guids_count"] = activeGuids.Count;
                    AppLogger.Debug($"[PlayerDatabase:Upsert] Recording {playersList.Count} active BattlEye players ({activeGuids.Count} valid GUIDs)...", context);

                    const string setOfflineBeSql = @"
                        UPDATE BattlEyePlayers 
                        SET IsOnline = 0 
                        WHERE IsOnline = 1 
                          AND BattlEyeGuid NOT IN (SELECT value FROM json_each(@ActiveGuidsJson));
                    ";

                    await using var setOfflineCmd = connection.CreateCommand();
                    setOfflineCmd.Transaction = (SqliteTransaction)dbTransaction;
                    setOfflineCmd.CommandText = setOfflineBeSql;
                    setOfflineCmd.Parameters.AddWithValue(ParamActiveGuidsJson, JsonSerializer.Serialize(activeGuids));
                    var setOfflineRows = await setOfflineCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    AppLogger.Trace($"[PlayerDatabase:Upsert] Marked {setOfflineRows} non-active BattlEye players offline.");

                    var existingBeMap = new Dictionary<string, (string Name, List<string> Aliases)>(StringComparer.OrdinalIgnoreCase);
                    const string fetchBeSql = @"
                        SELECT BattlEyeGuid, Name, Aliases 
                        FROM BattlEyePlayers 
                        WHERE BattlEyeGuid IN (SELECT value FROM json_each(@ActiveGuidsJson));
                    ";

                    var fetchStart = Stopwatch.GetTimestamp();
                    await using var fetchBeCmd = connection.CreateCommand();
                    fetchBeCmd.Transaction = (SqliteTransaction)dbTransaction;
                    fetchBeCmd.CommandText = fetchBeSql;
                    fetchBeCmd.Parameters.AddWithValue(ParamActiveGuidsJson, JsonSerializer.Serialize(activeGuids));
                    await using (var reader = await fetchBeCmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var guid = reader.GetString(0);
                            var name = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                            var aliasesJson = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? "[]" : reader.GetString(2);
                            existingBeMap[guid] = (name, ParseAliases(aliasesJson));
                        }
                    }
                    AppLogger.Trace($"[PlayerDatabase:Upsert] Pre-fetched {existingBeMap.Count} existing BattlEye records in {Stopwatch.GetElapsedTime(fetchStart).TotalMilliseconds:F2}ms.");

                    const string upsertBeSql = @"
                        INSERT INTO BattlEyePlayers (
                            BattlEyeGuid, Name, LastIpPort, Ping, IsOnline, Comment,
                            IsWatchlisted, HasAliases, CountryCode, CountryName, Location, TimeZone,
                            Aliases, FirstSeenUtc, LastSeenUtc
                        ) VALUES (
                            @Guid, @Name, @LastIpPort, @Ping, 1, @Comment,
                            @IsWatchlisted, @HasAliases, @CountryCode, @CountryName, @Location, @TimeZone,
                            @Aliases, @NowUtc, @NowUtc
                        )
                        ON CONFLICT(BattlEyeGuid) DO UPDATE SET
                            Name = excluded.Name,
                            Aliases = excluded.Aliases,
                            HasAliases = excluded.HasAliases,
                            LastIpPort = CASE WHEN excluded.LastIpPort <> '' AND excluded.LastIpPort <> 'N/A' THEN excluded.LastIpPort ELSE BattlEyePlayers.LastIpPort END,
                            Ping = CASE WHEN excluded.Ping <> 0 THEN excluded.Ping ELSE BattlEyePlayers.Ping END,
                            IsOnline = 1,
                            CountryCode = CASE WHEN excluded.CountryCode <> 'xx' THEN excluded.CountryCode ELSE BattlEyePlayers.CountryCode END,
                            CountryName = CASE WHEN excluded.CountryName <> 'Unknown Region' THEN excluded.CountryName ELSE BattlEyePlayers.CountryName END,
                            Location = CASE WHEN excluded.Location <> '' AND excluded.Location <> 'Unknown Region' THEN excluded.Location ELSE BattlEyePlayers.Location END,
                            TimeZone = CASE WHEN excluded.TimeZone <> '' THEN excluded.TimeZone ELSE BattlEyePlayers.TimeZone END,
                            LastSeenUtc = excluded.LastSeenUtc;
                    ";

                    await using var upsertCmd = connection.CreateCommand();
                    upsertCmd.Transaction = (SqliteTransaction)dbTransaction;
                    upsertCmd.CommandText = upsertBeSql;

                    var pGuid = upsertCmd.Parameters.Add(ParamGuid, SqliteType.Text);
                    var pName = upsertCmd.Parameters.Add(ParamName, SqliteType.Text);
                    var pLastIp = upsertCmd.Parameters.Add(ParamLastIpPort, SqliteType.Text);
                    var pPing = upsertCmd.Parameters.Add(ParamPing, SqliteType.Integer);
                    var pComment = upsertCmd.Parameters.Add(ParamComment, SqliteType.Text);
                    var pWatch = upsertCmd.Parameters.Add(ParamIsWatchlisted, SqliteType.Integer);
                    var pAliasesFlag = upsertCmd.Parameters.Add(ParamHasAliases, SqliteType.Integer);
                    var pCc = upsertCmd.Parameters.Add(ParamCountryCode, SqliteType.Text);
                    var pCn = upsertCmd.Parameters.Add(ParamCountryName, SqliteType.Text);
                    var pLoc = upsertCmd.Parameters.Add(ParamLocation, SqliteType.Text);
                    var pTz = upsertCmd.Parameters.Add(ParamTimeZone, SqliteType.Text);
                    var pAliases = upsertCmd.Parameters.Add(ParamAliases, SqliteType.Text);
                    var pNow = upsertCmd.Parameters.Add(ParamNowUtc, SqliteType.Text);

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
                            continue;
                        }

                        var distinctAliases = new HashSet<string>(player.Aliases ?? [], StringComparer.OrdinalIgnoreCase);
                        if (existingBeMap.TryGetValue(beGuid, out var existingRecord))
                        {
                            foreach (var oldAlias in existingRecord.Aliases)
                            {
                                distinctAliases.Add(oldAlias);
                            }

                            if (!string.IsNullOrWhiteSpace(existingRecord.Name) &&
                                !string.Equals(existingRecord.Name, player.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                distinctAliases.Add(existingRecord.Name);
                                AppLogger.Warn($"[PlayerDatabase:AliasDetected] Player alias transition detected for GUID '{beGuid}': '{existingRecord.Name}' -> '{player.Name}'. Current aliases count: {distinctAliases.Count}");
                            }
                        }

                        distinctAliases.Remove(player.Name);
                        player.Aliases = [.. distinctAliases];
                        player.HasAliases = distinctAliases.Count > 0;

                        string lastIpPort = string.Empty;
                        if (!string.IsNullOrWhiteSpace(player.Ip) && !player.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                        {
                            lastIpPort = player.Port > 0 ? $"{player.Ip}:{player.Port}" : player.Ip;
                        }

                        pGuid.Value = beGuid;
                        pName.Value = player.Name;
                        pLastIp.Value = lastIpPort;
                        pPing.Value = player.Ping;
                        pComment.Value = player.Comment ?? string.Empty;
                        pWatch.Value = player.IsWatchlisted ? 1 : 0;
                        pAliasesFlag.Value = player.HasAliases ? 1 : 0;
                        pCc.Value = player.Country.Code;
                        pCn.Value = player.Country.Name;
                        pLoc.Value = player.DisplayLocation;
                        pTz.Value = player.TimeZone;
                        pAliases.Value = SerializeAliases(player.Aliases);
                        pNow.Value = nowUtc;

                        await upsertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        updatedCount++;
                    }
                }
                else
                {
                    var activeUids = playersList
                        .Select(p => !string.IsNullOrWhiteSpace(p.ReforgerUid) ? p.ReforgerUid : p.Uid)
                        .Where(u => !string.IsNullOrWhiteSpace(u))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    context["distinct_uids_count"] = activeUids.Count;
                    AppLogger.Debug($"[PlayerDatabase:Upsert] Recording {playersList.Count} active Reforger players ({activeUids.Count} valid UIDs)...", context);

                    const string setOfflineRefSql = @"
                        UPDATE ReforgerPlayers 
                        SET IsOnline = 0 
                        WHERE IsOnline = 1 
                          AND ReforgerUid NOT IN (SELECT value FROM json_each(@ActiveUidsJson));
                    ";

                    await using var setOfflineCmd = connection.CreateCommand();
                    setOfflineCmd.Transaction = (SqliteTransaction)dbTransaction;
                    setOfflineCmd.CommandText = setOfflineRefSql;
                    setOfflineCmd.Parameters.AddWithValue(ParamActiveUidsJson, JsonSerializer.Serialize(activeUids));
                    var setOfflineRows = await setOfflineCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                    AppLogger.Trace($"[PlayerDatabase:Upsert] Marked {setOfflineRows} non-active Reforger players offline.");

                    var existingRefMap = new Dictionary<string, (string Name, List<string> Aliases)>(StringComparer.OrdinalIgnoreCase);
                    const string fetchRefSql = @"
                        SELECT ReforgerUid, Name, Aliases 
                        FROM ReforgerPlayers 
                        WHERE ReforgerUid IN (SELECT value FROM json_each(@ActiveUidsJson));
                    ";

                    var fetchStart = Stopwatch.GetTimestamp();
                    await using var fetchRefCmd = connection.CreateCommand();
                    fetchRefCmd.Transaction = (SqliteTransaction)dbTransaction;
                    fetchRefCmd.CommandText = fetchRefSql;
                    fetchRefCmd.Parameters.AddWithValue(ParamActiveUidsJson, JsonSerializer.Serialize(activeUids));
                    await using (var reader = await fetchRefCmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            var uid = reader.GetString(0);
                            var name = await reader.IsDBNullAsync(1).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                            var aliasesJson = await reader.IsDBNullAsync(2).ConfigureAwait(false) ? "[]" : reader.GetString(2);
                            existingRefMap[uid] = (name, ParseAliases(aliasesJson));
                        }
                    }
                    AppLogger.Trace($"[PlayerDatabase:Upsert] Pre-fetched {existingRefMap.Count} existing Reforger records in {Stopwatch.GetElapsedTime(fetchStart).TotalMilliseconds:F2}ms.");

                    const string upsertReforgerSql = @"
                        INSERT INTO ReforgerPlayers (
                            ReforgerUid, Name, IsOnline, Comment, IsWatchlisted, HasAliases,
                            Aliases, FirstSeenUtc, LastSeenUtc
                        ) VALUES (
                            @Uid, @Name, 1, @Comment, @IsWatchlisted, @HasAliases,
                            @Aliases, @NowUtc, @NowUtc
                        )
                        ON CONFLICT(ReforgerUid) DO UPDATE SET
                            Name = excluded.Name,
                            Aliases = excluded.Aliases,
                            HasAliases = excluded.HasAliases,
                            IsOnline = 1,
                            LastSeenUtc = excluded.LastSeenUtc;
                    ";

                    await using var upsertCmd = connection.CreateCommand();
                    upsertCmd.Transaction = (SqliteTransaction)dbTransaction;
                    upsertCmd.CommandText = upsertReforgerSql;

                    var pUid = upsertCmd.Parameters.Add(ParamUid, SqliteType.Text);
                    var pName = upsertCmd.Parameters.Add(ParamName, SqliteType.Text);
                    var pComment = upsertCmd.Parameters.Add(ParamComment, SqliteType.Text);
                    var pWatch = upsertCmd.Parameters.Add(ParamIsWatchlisted, SqliteType.Integer);
                    var pAliasesFlag = upsertCmd.Parameters.Add(ParamHasAliases, SqliteType.Integer);
                    var pAliases = upsertCmd.Parameters.Add(ParamAliases, SqliteType.Text);
                    var pNow = upsertCmd.Parameters.Add(ParamNowUtc, SqliteType.Text);

                    foreach (var player in playersList)
                    {
                        var reforgerUid = player.ReforgerUid;
                        if (string.IsNullOrWhiteSpace(reforgerUid) && !string.IsNullOrWhiteSpace(player.Uid))
                        {
                            reforgerUid = player.Uid.Trim();
                        }

                        if (string.IsNullOrWhiteSpace(reforgerUid))
                        {
                            continue;
                        }

                        var distinctAliases = new HashSet<string>(player.Aliases ?? [], StringComparer.OrdinalIgnoreCase);
                        if (existingRefMap.TryGetValue(reforgerUid, out var existingRecord))
                        {
                            foreach (var oldAlias in existingRecord.Aliases)
                            {
                                distinctAliases.Add(oldAlias);
                            }

                            if (!string.IsNullOrWhiteSpace(existingRecord.Name) &&
                                !string.Equals(existingRecord.Name, player.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                distinctAliases.Add(existingRecord.Name);
                                AppLogger.Warn($"[PlayerDatabase:AliasDetected] Player alias transition detected for UID '{reforgerUid}': '{existingRecord.Name}' -> '{player.Name}'. Current aliases count: {distinctAliases.Count}");
                            }
                        }

                        distinctAliases.Remove(player.Name);
                        player.Aliases = [.. distinctAliases];
                        player.HasAliases = distinctAliases.Count > 0;

                        pUid.Value = reforgerUid;
                        pName.Value = player.Name;
                        pComment.Value = player.Comment ?? string.Empty;
                        pWatch.Value = player.IsWatchlisted ? 1 : 0;
                        pAliasesFlag.Value = player.HasAliases ? 1 : 0;
                        pAliases.Value = SerializeAliases(player.Aliases);
                        pNow.Value = nowUtc;

                        await upsertCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        updatedCount++;
                    }
                }

                await dbTransaction.CommitAsync().ConfigureAwait(false);
                transaction.Finish(SpanStatus.Ok);
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                context["updated_count"] = updatedCount;
                context[ContextDurationMs] = elapsedMs;
                AppLogger.Info($"[PlayerDatabase:Upsert] Successfully committed batch upsert of {updatedCount}/{playersList.Count} {protocol} active player records in {elapsedMs:F2}ms.", context);
            }
            catch (Exception txEx)
            {
                await dbTransaction.RollbackAsync().ConfigureAwait(false);
                transaction.Finish(SpanStatus.InternalError);
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                context[ContextErrorMessage] = txEx.Message;
                context[ContextDurationMs] = elapsedMs;
                AppLogger.Error($"[PlayerDatabase:Upsert] Transaction rolled back during RecordSeenPlayersAsync ({protocol}) after {elapsedMs:F2}ms: {txEx.Message}", txEx, context);
                ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Transaction failure during player batch update: {txEx.Message}");
                throw;
            }
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            context["sqlite_error"] = sqlEx.Message;
            AppLogger.Error($"[PlayerDatabase:Upsert] SQLite error in RecordSeenPlayersAsync: {sqlEx.Message} (Code: {sqlEx.SqliteErrorCode})", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"SQLite error updating player records: {sqlEx.Message}");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[PlayerDatabase:Upsert] Unexpected error in RecordSeenPlayersAsync: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed updating active players in SQLite: {ex.Message}");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task SetAllOfflineAsync(RconProtocol? protocol = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            ["protocol"] = protocol?.ToString() ?? "All",
            [ContextThreadId] = threadId
        };

        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.SetAllOfflineAsync({protocol?.ToString() ?? "All"})");
        var transaction = SentrySdk.StartTransaction("SetAllOffline", "db.sqlite.set_offline");
        await DbLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);

            int affected = 0;
            if (protocol == null || protocol == RconProtocol.BattlEye)
            {
                const string cmdBeSql = "UPDATE BattlEyePlayers SET IsOnline = 0 WHERE IsOnline = 1;";
                await using var cmdBe = connection.CreateCommand();
                cmdBe.CommandText = cmdBeSql;
                var beRows = await cmdBe.ExecuteNonQueryAsync().ConfigureAwait(false);
                affected += beRows;
                AppLogger.Trace($"[PlayerDatabase:Offline] Marked {beRows} BattlEye players offline.");
            }

            if (protocol == null || protocol == RconProtocol.ReforgerBuiltIn)
            {
                const string cmdRefSql = "UPDATE ReforgerPlayers SET IsOnline = 0 WHERE IsOnline = 1;";
                await using var cmdRef = connection.CreateCommand();
                cmdRef.CommandText = cmdRefSql;
                var refRows = await cmdRef.ExecuteNonQueryAsync().ConfigureAwait(false);
                affected += refRows;
                AppLogger.Trace($"[PlayerDatabase:Offline] Marked {refRows} Reforger players offline.");
            }

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextRowsAffected] = affected;
            context[ContextDurationMs] = elapsedMs;
            AppLogger.Info($"[PlayerDatabase:Offline] Total {affected} player record(s) set to offline in {elapsedMs:F2}ms ({protocol?.ToString() ?? "All"}).", context);
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            context["sqlite_error"] = sqlEx.Message;
            AppLogger.Error($"[PlayerDatabase:Offline] SQLite error setting players offline: {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed updating offline states in SQLite: {sqlEx.Message}");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[PlayerDatabase:Offline] Error setting players offline in SQLite: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Unexpected error marking players offline: {ex.Message}");
        }
        finally
        {
            DbLock.Release();
        }
    }

    [SuppressMessage("Security", "S2077:Use a parameterized query instead of string formatting", Justification = "Dynamic WHERE and ORDER BY clauses use strictly sanitized and validated internal enum mappings with parameterized query parameters")]
    public static async Task<PagedResult<DatabasePlayerModel>> GetPagedAsync(DatabaseQueryParameters parameters, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.GetPagedAsync(Page={parameters.PageIndex}, Size={parameters.PageSize}, Protocol={parameters.Protocol})");
        var transaction = SentrySdk.StartTransaction($"GetPaged_{parameters.Protocol}", "db.sqlite.query_paged");

        var cleanQuery = parameters.SearchQuery?.Trim();
        var context = new Dictionary<string, object?>
        {
            ["protocol"] = parameters.Protocol.ToString(),
            ["page_index"] = parameters.PageIndex,
            ["page_size"] = parameters.PageSize,
            ["search_query"] = AppLogger.SanitizeSensitiveData(cleanQuery),
            ["search_type"] = parameters.SearchType ?? "All",
            ["sort_by"] = parameters.SortBy ?? "Default",
            ["sort_ascending"] = parameters.SortAscending,
            [ContextThreadId] = threadId
        };

        AppLogger.Debug($"[PlayerDatabase:QueryPaged] Executing paginated query on {parameters.Protocol} (Page={parameters.PageIndex}, Size={parameters.PageSize})...", context);

        await DbLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        var items = new List<DatabasePlayerModel>();
        int totalCount = 0;

        int safePageSize = Math.Clamp(parameters.PageSize, 1, 500);
        int safePageIndex = Math.Max(1, parameters.PageIndex);
        int offset = (safePageIndex - 1) * safePageSize;

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            bool isBe = parameters.Protocol == RconProtocol.BattlEye;
            string tableName = isBe ? "BattlEyePlayers" : "ReforgerPlayers";

            var whereClauses = new List<string>();

            if (!string.IsNullOrWhiteSpace(cleanQuery))
            {
                switch (parameters.SearchType)
                {
                    case "Name":
                        whereClauses.Add("(Name LIKE @SearchQuery OR Aliases LIKE @SearchQuery)");
                        break;
                    case "UID":
                        if (isBe)
                            whereClauses.Add("(BattlEyeGuid LIKE @SearchQuery)");
                        else
                            whereClauses.Add("(ReforgerUid LIKE @SearchQuery)");
                        break;
                    case "Comment":
                        whereClauses.Add("(Comment LIKE @SearchQuery)");
                        break;
                    default:
                        if (isBe)
                            whereClauses.Add("(Name LIKE @SearchQuery OR Aliases LIKE @SearchQuery OR BattlEyeGuid LIKE @SearchQuery OR Comment LIKE @SearchQuery OR LastIpPort LIKE @SearchQuery OR Location LIKE @SearchQuery)");
                        else
                            whereClauses.Add("(Name LIKE @SearchQuery OR Aliases LIKE @SearchQuery OR ReforgerUid LIKE @SearchQuery OR Comment LIKE @SearchQuery)");
                        break;
                }
            }

            string whereSql = whereClauses.Count > 0 ? " WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

            string countSql = $"SELECT COUNT(*) FROM {tableName}{whereSql};";
            var countStart = Stopwatch.GetTimestamp();
            await using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = countSql;
                if (!string.IsNullOrWhiteSpace(cleanQuery))
                {
                    countCmd.Parameters.AddWithValue(ParamSearchQuery, $"%{cleanQuery}%");
                }

                var countObj = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                totalCount = Convert.ToInt32(countObj, CultureInfo.InvariantCulture);
            }
            var countElapsedMs = Stopwatch.GetElapsedTime(countStart).TotalMilliseconds;
            AppLogger.Trace($"[PlayerDatabase:QueryPaged] COUNT query executed in {countElapsedMs:F2}ms (MatchingRows={totalCount}).");

            string orderBySql = ResolveOrderBySql(parameters.SortBy, parameters.SortAscending, isBe);

            string querySql = isBe
                ? $@"
                    SELECT 
                        BattlEyeGuid, Name, LastIpPort, Ping, IsOnline, Comment,
                        IsWatchlisted, HasAliases, CountryCode, CountryName, Location, TimeZone,
                        Aliases, FirstSeenUtc, LastSeenUtc
                    FROM BattlEyePlayers
                    {whereSql}
                    ORDER BY {orderBySql}
                    LIMIT @Limit OFFSET @Offset;"
                : $@"
                    SELECT 
                        ReforgerUid, Name, IsOnline, Comment, IsWatchlisted, HasAliases,
                        Aliases, FirstSeenUtc, LastSeenUtc
                    FROM ReforgerPlayers
                    {whereSql}
                    ORDER BY {orderBySql}
                    LIMIT @Limit OFFSET @Offset;";

            var fetchStart = Stopwatch.GetTimestamp();
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = querySql;
                if (!string.IsNullOrWhiteSpace(cleanQuery))
                {
                    cmd.Parameters.AddWithValue(ParamSearchQuery, $"%{cleanQuery}%");
                }
                cmd.Parameters.AddWithValue(ParamLimit, safePageSize);
                cmd.Parameters.AddWithValue(ParamOffset, offset);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                int rowNumber = offset + 1;

                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (isBe)
                    {
                        var guid = reader.GetString(0);
                        var name = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                        var lastIpPort = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(2);
                        var ping = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? 0 : reader.GetInt32(3);
                        var isOnline = !await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) && reader.GetInt32(4) == 1;
                        var comment = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(5);
                        var isWatchlisted = !await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) && reader.GetInt32(6) == 1;
                        var hasAliases = !await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) && reader.GetInt32(7) == 1;
                        var countryCode = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? "xx" : reader.GetString(8);
                        var countryName = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? "Unknown Region" : reader.GetString(9);
                        var location = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(10);
                        var timeZone = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(11);
                        var aliasesJson = await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false) ? "[]" : reader.GetString(12);
                        var lastSeenStr = await reader.IsDBNullAsync(14, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(14);

                        DateTime.TryParse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen);

                        items.Add(new DatabasePlayerModel
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
                        });
                    }
                    else
                    {
                        var uid = reader.GetString(0);
                        var name = await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(1);
                        var isOnline = !await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) && reader.GetInt32(2) == 1;
                        var comment = await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(3);
                        var isWatchlisted = !await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) && reader.GetInt32(4) == 1;
                        var hasAliases = !await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) && reader.GetInt32(5) == 1;
                        var aliasesJson = await reader.IsDBNullAsync(6, cancellationToken).ConfigureAwait(false) ? "[]" : reader.GetString(6);
                        var lastSeenStr = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false) ? string.Empty : reader.GetString(8);

                        DateTime.TryParse(lastSeenStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeen);

                        items.Add(new DatabasePlayerModel
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
                        });
                    }
                }
            }
            var fetchElapsedMs = Stopwatch.GetElapsedTime(fetchStart).TotalMilliseconds;

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context["items_returned"] = items.Count;
            context["total_matches"] = totalCount;
            context["fetch_latency_ms"] = fetchElapsedMs;
            context["total_latency_ms"] = elapsedMs;

            AppLogger.TrackEvent("database_query_executed", new Dictionary<string, object>
            {
                ["protocol"] = parameters.Protocol.ToString(),
                ["page_index"] = safePageIndex,
                ["page_size"] = safePageSize,
                ["has_search_query"] = !string.IsNullOrWhiteSpace(cleanQuery),
                ["search_type"] = parameters.SearchType ?? "All",
                ["sort_field"] = parameters.SortBy ?? "Default",
                ["sort_ascending"] = parameters.SortAscending,
                ["total_records"] = totalCount,
                ["query_latency_ms"] = elapsedMs
            });

            AppLogger.Debug($"[PlayerDatabase:QueryPaged] Paginated fetch complete in {elapsedMs:F2}ms ({items.Count}/{totalCount} items, FetchOnly={fetchElapsedMs:F2}ms).", context);
            return new PagedResult<DatabasePlayerModel>(items, totalCount, safePageIndex, safePageSize);
        }
        catch (OperationCanceledException opEx)
        {
            transaction.Finish(SpanStatus.Cancelled);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[PlayerDatabase:QueryPaged] Query canceled after {elapsedMs:F2}ms for Page {safePageIndex}: {opEx.Message}", context);
            return new PagedResult<DatabasePlayerModel>([], 0, safePageIndex, safePageSize);
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextSqliteErrorCode] = sqlEx.SqliteErrorCode;
            context["sqlite_extended_code"] = sqlEx.SqliteExtendedErrorCode;
            AppLogger.Error($"[PlayerDatabase:QueryPaged] SQLite error in GetPagedAsync after {elapsedMs:F2}ms: {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"SQLite query failure (Code: {sqlEx.SqliteErrorCode}): {sqlEx.Message}");
            return new PagedResult<DatabasePlayerModel>([], 0, safePageIndex, safePageSize);
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[PlayerDatabase:QueryPaged] Error in GetPagedAsync after {elapsedMs:F2}ms: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed querying player database records: {ex.Message}");
            return new PagedResult<DatabasePlayerModel>([], 0, safePageIndex, safePageSize);
        }
        finally
        {
            DbLock.Release();
        }
    }

    private static string ResolveOrderBySql(string? sortBy, bool ascending, bool isBattlEye)
    {
        string dir = ascending ? "ASC" : "DESC";

        if (isBattlEye)
        {
            return sortBy switch
            {
                "Status" => $"(CASE WHEN IsWatchlisted = 1 THEN 3 WHEN IsOnline = 1 THEN 2 WHEN HasAliases = 1 THEN 1 ELSE 0 END) {dir}, LastSeenUtc DESC",
                "Country" => $"CountryName COLLATE NOCASE {dir}, Name COLLATE NOCASE ASC",
                "Name" => $"Name COLLATE NOCASE {dir}",
                "BattlEye GUID" => $"BattlEyeGuid {dir}",
                "IP:Port" => $"LastIpPort {dir}",
                "Ping" => $"Ping {dir}",
                "Comment" => $"Comment COLLATE NOCASE {dir}",
                _ => $"LastSeenUtc {dir}"
            };
        }

        return sortBy switch
        {
            "Status" => $"(CASE WHEN IsWatchlisted = 1 THEN 3 WHEN IsOnline = 1 THEN 2 WHEN HasAliases = 1 THEN 1 ELSE 0 END) {dir}, LastSeenUtc DESC",
            "Name" => $"Name COLLATE NOCASE {dir}",
            "BattlEye GUID" or "Player UID" => $"ReforgerUid {dir}",
            "Comment" => $"Comment COLLATE NOCASE {dir}",
            _ => $"LastSeenUtc {dir}"
        };
    }

    public static async Task<List<DatabasePlayerModel>> GetAllAsync(RconProtocol protocol)
    {
        AppLogger.Debug($"[PlayerDatabase:GetAll] GetAllAsync invoked for {protocol}. Forwarding to GetPagedAsync with PageSize=100000...");
        var result = await GetPagedAsync(new DatabaseQueryParameters(protocol, 1, 100000, null, null, null, false)).ConfigureAwait(false);
        return result.Items;
    }

    public static async Task UpdateCommentAsync(string identifier, string comment, RconProtocol protocol)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            ["identifier"] = identifier,
            ["protocol"] = protocol.ToString(),
            ["comment_len"] = comment?.Length ?? 0,
            [ContextThreadId] = threadId
        };

        await InitializeAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            AppLogger.Warn("[PlayerDatabase:Comment] UpdateCommentAsync rejected: Target identifier is empty.", null, context);
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.UpdateCommentAsync('{identifier}', {protocol})");
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
            context[ContextRowsAffected] = affected;
            context[ContextDurationMs] = elapsedMs;
            AppLogger.Info($"[PlayerDatabase:Comment] Updated comment for '{identifier}' in {elapsedMs:F2}ms (RowsAffected={affected}).", context);
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextSqliteErrorCode] = sqlEx.SqliteErrorCode;
            AppLogger.Error($"[PlayerDatabase:Comment] SQLite error updating comment for '{identifier}': {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"SQLite error saving comment: {sqlEx.Message}");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[PlayerDatabase:Comment] Error updating comment for '{identifier}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed saving comment to database: {ex.Message}");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task SetWatchlistStatusAsync(string identifier, bool isWatchlisted, RconProtocol protocol)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            ["identifier"] = identifier,
            ["is_watchlisted"] = isWatchlisted,
            ["protocol"] = protocol.ToString(),
            [ContextThreadId] = threadId
        };

        await InitializeAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            AppLogger.Warn("[PlayerDatabase:Watchlist] SetWatchlistStatusAsync rejected: Target identifier is empty.", null, context);
            return;
        }

        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.SetWatchlistStatusAsync('{identifier}', {isWatchlisted}, {protocol})");
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
            context[ContextRowsAffected] = affected;
            context[ContextDurationMs] = elapsedMs;
            AppLogger.Info($"[PlayerDatabase:Watchlist] Set Watchlisted={isWatchlisted} for '{identifier}' in {elapsedMs:F2}ms (RowsAffected={affected}).", context);
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextSqliteErrorCode] = sqlEx.SqliteErrorCode;
            AppLogger.Error($"[PlayerDatabase:Watchlist] SQLite error updating watchlist for '{identifier}': {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"SQLite error updating watchlist status: {sqlEx.Message}");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[PlayerDatabase:Watchlist] Error updating watchlist status for '{identifier}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed updating watchlist status: {ex.Message}");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task ClearDatabaseAsync(RconProtocol? protocol = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?>
        {
            ["protocol"] = protocol?.ToString() ?? "All",
            [ContextThreadId] = threadId
        };

        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure($"PlayerDatabaseStorageService.ClearDatabaseAsync({protocol?.ToString() ?? "All"})");
        var transaction = SentrySdk.StartTransaction("ClearDatabase", "db.sqlite.purge");
        await DbLock.WaitAsync().ConfigureAwait(false);

        int purgedCount = 0;
        long dbSizeBefore = File.Exists(DatabaseFile) ? new FileInfo(DatabaseFile).Length : 0;
        var walFile = $"{DatabaseFile}-wal";
        long walSizeBefore = File.Exists(walFile) ? new FileInfo(walFile).Length : 0;

        context["size_before_bytes"] = dbSizeBefore;
        context["wal_before_bytes"] = walSizeBefore;

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await using var dbTransaction = await connection.BeginTransactionAsync().ConfigureAwait(false);

            try
            {
                if (protocol == null || protocol == RconProtocol.BattlEye)
                {
                    const string delBeSql = "DELETE FROM BattlEyePlayers;";
                    await using var delBe = connection.CreateCommand();
                    delBe.Transaction = (SqliteTransaction)dbTransaction;
                    delBe.CommandText = delBeSql;
                    int deletedBe = await delBe.ExecuteNonQueryAsync().ConfigureAwait(false);
                    purgedCount += deletedBe;
                    AppLogger.Info($"[PlayerDatabase:Purge] Purged {deletedBe} BattlEye player records.");
                }

                if (protocol == null || protocol == RconProtocol.ReforgerBuiltIn)
                {
                    const string delRefSql = "DELETE FROM ReforgerPlayers;";
                    await using var delRef = connection.CreateCommand();
                    delRef.Transaction = (SqliteTransaction)dbTransaction;
                    delRef.CommandText = delRefSql;
                    int deletedRef = await delRef.ExecuteNonQueryAsync().ConfigureAwait(false);
                    purgedCount += deletedRef;
                    AppLogger.Info($"[PlayerDatabase:Purge] Purged {deletedRef} Reforger player records.");
                }

                await dbTransaction.CommitAsync().ConfigureAwait(false);
            }
            catch (Exception txEx)
            {
                await dbTransaction.RollbackAsync().ConfigureAwait(false);
                var txElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                context[ContextErrorMessage] = txEx.Message;
                context[ContextDurationMs] = txElapsedMs;
                AppLogger.Error($"[PlayerDatabase:Purge] Purge transaction rolled back after {txElapsedMs:F2}ms: {txEx.Message}", txEx, context);
                ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Database purge transaction failed: {txEx.Message}");
                throw;
            }

            var vacuumStart = Stopwatch.GetTimestamp();
            await using (var vacuumCmd = connection.CreateCommand())
            {
                vacuumCmd.CommandText = "VACUUM;";
                await vacuumCmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var vacuumElapsedMs = Stopwatch.GetElapsedTime(vacuumStart).TotalMilliseconds;
            AppLogger.Debug($"[PlayerDatabase:Purge] SQLite VACUUM completed in {vacuumElapsedMs:F2}ms.");

            transaction.Finish(SpanStatus.Ok);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context["purged_count"] = purgedCount;
            context[ContextDurationMs] = elapsedMs;

            AppLogger.TrackEvent("database_maintenance_performed", new Dictionary<string, object>
            {
                ["operation"] = "clear_records",
                ["protocol"] = protocol?.ToString() ?? "All",
                ["records_purged"] = purgedCount,
                ["database_size_bytes_before"] = dbSizeBefore,
                ["wal_size_bytes"] = walSizeBefore,
                ["duration_ms"] = elapsedMs
            });

            AppLogger.Info($"[PlayerDatabase:Purge] Historical database purge complete in {elapsedMs:F2}ms ({purgedCount} rows purged).", context);
        }
        catch (SqliteException sqlEx)
        {
            transaction.Finish(SpanStatus.InternalError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextSqliteErrorCode] = sqlEx.SqliteErrorCode;
            AppLogger.Error($"[PlayerDatabase:Purge] SQLite error during purge: {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"SQLite error clearing database: {sqlEx.Message}");
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextDurationMs] = elapsedMs;
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[PlayerDatabase:Purge] Error clearing player database in SQLite: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed clearing player database: {ex.Message}");
        }
        finally
        {
            DbLock.Release();
        }
    }

    public static async Task<DatabaseStatistics> GetDatabaseStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var context = new Dictionary<string, object?> { [ContextThreadId] = threadId };

        await InitializeAsync().ConfigureAwait(false);
        using var timing = AppLogger.Measure("PlayerDatabaseStorageService.GetDatabaseStatisticsAsync");
        await DbLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        int totalReforger = 0;
        int totalBattlEye = 0;
        int onlinePlayers = 0;
        int watchlistedPlayers = 0;

        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            const string statsSql = @"
                SELECT 
                    (SELECT COUNT(*) FROM ReforgerPlayers),
                    (SELECT COUNT(*) FROM BattlEyePlayers),
                    (SELECT COUNT(*) FROM ReforgerPlayers WHERE IsOnline = 1) + (SELECT COUNT(*) FROM BattlEyePlayers WHERE IsOnline = 1),
                    (SELECT COUNT(*) FROM ReforgerPlayers WHERE IsWatchlisted = 1) + (SELECT COUNT(*) FROM BattlEyePlayers WHERE IsWatchlisted = 1);
            ";

            await using var command = connection.CreateCommand();
            command.CommandText = statsSql;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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
            context["reforger_count"] = totalReforger;
            context["battleye_count"] = totalBattlEye;
            context["online_count"] = onlinePlayers;
            context["watchlisted_count"] = watchlistedPlayers;
            context["db_size_bytes"] = dbSize;
            context["wal_size_bytes"] = walSize;
            context["query_duration_ms"] = elapsedMs;

            AppLogger.Debug($"[PlayerDatabase:Stats] Database statistics queried in {elapsedMs:F2}ms.", context);
            return new DatabaseStatistics(totalReforger, totalBattlEye, onlinePlayers, watchlistedPlayers, dbSize, walSize, DatabaseFile);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Trace("[PlayerDatabase:Stats] Statistics query canceled by CancellationToken.", context);
            return new DatabaseStatistics(0, 0, 0, 0, 0, 0, DatabaseFile);
        }
        catch (SqliteException sqlEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context["sqlite_error"] = sqlEx.Message;
            context[ContextDurationMs] = elapsedMs;
            AppLogger.Error($"[PlayerDatabase:Stats] SQLite error querying database statistics: {sqlEx.Message}", sqlEx, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"SQLite error retrieving stats: {sqlEx.Message}");
            return new DatabaseStatistics(0, 0, 0, 0, 0, 0, DatabaseFile);
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextErrorMessage] = ex.Message;
            context[ContextDurationMs] = elapsedMs;
            AppLogger.Error($"[PlayerDatabase:Stats] Error querying SQLite database statistics: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError(DatabaseErrorTitle, $"Failed retrieving database statistics: {ex.Message}");
            return new DatabaseStatistics(0, 0, 0, 0, 0, 0, DatabaseFile);
        }
        finally
        {
            DbLock.Release();
        }
    }
}