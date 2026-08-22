using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.BattleNET;
using ReforgerRcon.Models;
using ReforgerRcon.Services.Parsers;
using Sentry;
using SerilogTimings;

namespace ReforgerRcon.Services;

public sealed class RconService : IRconService
{
    private const string ProtocolMetricKey = "protocol";
    private const string ProcessingCommandToken = "processing command";

    private BattlEyeClient? _client;
    private ServerProfile? _currentProfile;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingCommands = new();
    private readonly StringBuilder _aggregatedBuffer = new();
    private readonly Lock _bufferLock = new();
    private readonly SemaphoreSlim _commandExecutionLock = new(1, 1);
    private DateTime _lastMessageChunkUtc = DateTime.UtcNow;
    private int _messageChunksCount;
    private bool _isDisposed;

    private readonly System.Net.NetworkInformation.Ping _icmpPingSender = new();
    private readonly Queue<int> _pingSamples = new();
    private readonly Lock _pingLock = new();
    private CancellationTokenSource? _pingLoopCts;
    private CancellationTokenSource? _activePlayersQueryCts;
    private CancellationTokenSource? _activeBansQueryCts;
    private int _smoothedPingMs;

    public RconProtocol CurrentProtocol => _currentProfile?.Protocol ?? RconProtocol.ReforgerBuiltIn;
    public bool IsConnected => _client is { Connected: true };
    public int PingMs => _smoothedPingMs > 0 ? _smoothedPingMs : (_client?.LastPingMs ?? 0);
    public DateTime LastPacketTime { get; private set; } = DateTime.UtcNow;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<PlayerModel>? PlayerJoined;
    public event EventHandler<PlayerModel>? PlayerLeft;
    public event EventHandler<string>? ConnectionLost;

    private List<PlayerModel> _lastKnownPlayers = [];

    private void RaiseOutputReceived(string message) => OutputReceived?.Invoke(this, message);
    private void RaisePlayerJoined(PlayerModel player) => PlayerJoined?.Invoke(this, player);
    private void RaisePlayerLeft(PlayerModel player) => PlayerLeft?.Invoke(this, player);
    private void RaiseConnectionLost(string reason) => ConnectionLost?.Invoke(this, reason);

    public async Task<bool> ConnectAsync(ServerProfile profile)
    {
        _currentProfile = profile;
        var transaction = SentrySdk.StartTransaction("RCON Connect", "network.rcon.connect");
        using var op = Operation.Begin("Connect RCON to {ServerIp}:{Port} ({Protocol})", profile.ServerIp, profile.Port, profile.Protocol);

        SentrySdk.Metrics.EmitCounter("rcon_connect_attempts", 1,
        [
            new KeyValuePair<string, object>(ProtocolMetricKey, profile.Protocol.ToString())
        ]);

        try
        {
            IPAddress? ip = null;
            if (IPAddress.TryParse(profile.ServerIp, out var parsedIp))
            {
                ip = parsedIp;
            }
            else
            {
                AppLogger.Debug($"[RconService] Resolving hostname '{profile.ServerIp}'...");
                var addresses = await Dns.GetHostAddressesAsync(profile.ServerIp, CancellationToken.None);
                if (addresses.Length > 0)
                {
                    ip = addresses[0];
                    AppLogger.Info($"[RconService] Host '{profile.ServerIp}' resolved to IP: {ip}");
                }
            }

            if (ip == null)
            {
                AppLogger.Error($"[RconService] Could not resolve host or IP: {profile.ServerIp}");
                transaction.Finish(SpanStatus.InvalidArgument);
                return false;
            }

            var credentials = new BattlEyeLoginCredentials(ip, profile.Port, profile.Password);
            _client = new BattlEyeClient(credentials);

            var connectTcs = new TaskCompletionSource<bool>();

            _client.BattlEyeConnected += args =>
            {
                AppLogger.Info($"[RconService] Connected event received: {args.ConnectionResult} ({args.Message})");
                connectTcs.TrySetResult(args.ConnectionResult == BattlEyeConnectionResult.Success);
            };

            _client.BattlEyeDisconnected += async args =>
            {
                AppLogger.Warn($"[RconService] Disconnected event received: {args.DisconnectionType} ({args.Message})");
                RaiseOutputReceived($"[SYSTEM] Disconnected: {args.Message}");

                CancelInFlightQueries();
                StopBackgroundPingMonitor();
                await PlayerDatabaseStorageService.SetAllOfflineAsync();
                _lastKnownPlayers.Clear();

                if (args.DisconnectionType != BattlEyeDisconnectionType.Manual)
                {
                    RaiseConnectionLost(args.Message);
                }
            };

            _client.BattlEyeMessageReceived += OnBattlEyeMessageReceived;

            _ = _client.ConnectAsync(CancellationToken.None);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            cts.Token.Register(() => connectTcs.TrySetResult(false));

            bool success = await connectTcs.Task;
            if (success)
            {
                LastPacketTime = DateTime.UtcNow;
                RaiseOutputReceived($"[SYSTEM] Connected successfully to {profile.ServerIp}:{profile.Port}");
                AppLogger.Info($"[RconService] Live connection verified for {profile.ServerIp}:{profile.Port}");

                StartBackgroundPingMonitor(ip);

                op.Complete();
                transaction.Finish(SpanStatus.Ok);

                SentrySdk.Metrics.EmitCounter("rcon_connect_success", 1,
                [
                    new KeyValuePair<string, object>(ProtocolMetricKey, profile.Protocol.ToString())
                ]);
            }
            else
            {
                AppLogger.Warn($"[RconService] Connection attempt timed out or failed for {profile.ServerIp}:{profile.Port}");
                transaction.Finish(SpanStatus.DeadlineExceeded);
            }

            return success;
        }
        catch (SocketException sockEx)
        {
            transaction.Finish(SpanStatus.Unavailable);
            AppLogger.Error($"[RconService] Socket failure connecting to {profile.ServerIp}:{profile.Port} ({sockEx.SocketErrorCode})", sockEx.Demystify());
            return false;
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);
            AppLogger.Warn($"[RconService] Connection attempt to {profile.ServerIp}:{profile.Port} was cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            AppLogger.Error($"[RconService] Unexpected exception connecting to {profile.ServerIp}:{profile.Port}", ex.Demystify());
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        AppLogger.Info("[RconService] Initiating graceful disconnect...");
        CancelInFlightQueries();
        StopBackgroundPingMonitor();

        if (_client is { Connected: true } && CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            try
            {
                AppLogger.Info("[RconService] Sending '@logout' to Reforger server to terminate session slot...");
                _client.SendCommand("@logout", log: false);
                await Task.Delay(100, CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLogger.Debug($"[RconService] Non-fatal error sending '@logout': {ex.Message}");
            }
        }

        await PlayerDatabaseStorageService.SetAllOfflineAsync();
        _lastKnownPlayers.Clear();

        _client?.Dispose();
        _client = null;

        RaiseOutputReceived("[SYSTEM] Disconnected from server.");
    }

    private void CancelInFlightQueries()
    {
        try
        {
            if (_activePlayersQueryCts is { IsCancellationRequested: false })
            {
                AppLogger.Debug("[RconService] Aborting active in-flight player list query...");
                _activePlayersQueryCts.Cancel();
                _activePlayersQueryCts.Dispose();
                _activePlayersQueryCts = null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[RconService] Notice cancelling in-flight players query: {ex.Message}");
        }

        try
        {
            if (_activeBansQueryCts is { IsCancellationRequested: false })
            {
                AppLogger.Debug("[RconService] Aborting active in-flight bans query...");
                _activeBansQueryCts.Cancel();
                _activeBansQueryCts.Dispose();
                _activeBansQueryCts = null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[RconService] Notice cancelling in-flight bans query: {ex.Message}");
        }
    }

    private void StartBackgroundPingMonitor(IPAddress ip)
    {
        StopBackgroundPingMonitor();

        _pingLoopCts = new CancellationTokenSource();
        var token = _pingLoopCts.Token;

        _ = Task.Run(async () =>
        {
            using var periodicTimer = new PeriodicTimer(TimeSpan.FromSeconds(1.5));
            try
            {
                await SampleNetworkPingAsync(ip, token);

                while (!token.IsCancellationRequested && await periodicTimer.WaitForNextTickAsync(token))
                {
                    await SampleNetworkPingAsync(ip, token);
                }
            }
            catch (OperationCanceledException)
            {
                AppLogger.Debug("[RconService] Background ICMP ping monitor stopped cleanly.");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[RconService] Non-fatal notice in background ping monitor: {ex.Message}");
            }
        }, token);
    }

    private void StopBackgroundPingMonitor()
    {
        _pingLoopCts?.Cancel();
        _pingLoopCts?.Dispose();
        _pingLoopCts = null;

        lock (_pingLock)
        {
            _pingSamples.Clear();
            _smoothedPingMs = 0;
        }
    }

    private async Task SampleNetworkPingAsync(IPAddress ip, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var reply = await _icmpPingSender.SendPingAsync(ip, 1200);
            if (reply.Status == IPStatus.Success && reply.RoundtripTime > 0)
            {
                RecordPingMeasurement((int)reply.RoundtripTime, isIcmp: true);
                return;
            }
        }
        catch (PingException pingEx)
        {
            AppLogger.Trace($"[RconService] ICMP ping notice: {pingEx.Message}. Falling back to UDP sequence ACK latency.");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[RconService] Non-fatal ICMP ping exception: {ex.Message}");
        }

        if (_client is { Connected: true, LastPingMs: > 0 })
        {
            RecordPingMeasurement(_client.LastPingMs, isIcmp: false);
        }
    }

    private void RecordPingMeasurement(int sampleMs, bool isIcmp)
    {
        if (sampleMs <= 0) sampleMs = 1;

        lock (_pingLock)
        {
            _pingSamples.Enqueue(sampleMs);
            while (_pingSamples.Count > 5)
            {
                _pingSamples.Dequeue();
            }

            var samples = _pingSamples.ToArray();
            Array.Sort(samples);
            int median = samples[samples.Length / 2];

            if (_smoothedPingMs <= 0)
            {
                _smoothedPingMs = median;
            }
            else
            {
                _smoothedPingMs = (int)Math.Round((_smoothedPingMs * 0.75) + (median * 0.25));
            }
        }

        AppLogger.Trace($"[RconService] Network Ping Measurement: {sampleMs} ms (ICMP: {isIcmp}, Stable Readout: {_smoothedPingMs} ms)");
    }

    private void OnBattlEyeMessageReceived(BattlEyeMessageEventArgs args)
    {
        LastPacketTime = DateTime.UtcNow;
        var message = args.Message;

        lock (_bufferLock)
        {
            if (_aggregatedBuffer.Length > 0)
            {
                char lastChar = _aggregatedBuffer[^1];
                if (lastChar != '\n' && lastChar != '\r' && !message.StartsWith('\n') && !message.StartsWith('\r'))
                {
                    _aggregatedBuffer.Append('\n');
                }
            }

            _aggregatedBuffer.Append(message);
            _lastMessageChunkUtc = DateTime.UtcNow;
            _messageChunksCount++;
        }

        if (args.Id != 256 && _pendingCommands.TryRemove(args.Id, out var tcs))
        {
            tcs.TrySetResult(message);
        }

        RaiseOutputReceived($"[RCON IN] {message}");
    }

    public async Task<List<PlayerModel>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true }) return [];

        CancelInFlightQueries();
        _activePlayersQueryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queryToken = _activePlayersQueryCts.Token;

        var sw = Stopwatch.StartNew();
        var transaction = SentrySdk.StartTransaction("GetPlayers", "rcon.query.players");
        using var op = Operation.Begin("Query active players list ({Protocol})", CurrentProtocol);
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#players" : "players";

            AppLogger.Debug($"[RconService:Timing] Fetching player list via '{command}'...");
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(5.0), queryToken);
            queryToken.ThrowIfCancellationRequested();

            var networkMs = sw.ElapsedMilliseconds;

            var parseSw = Stopwatch.StartNew();
            List<PlayerModel> currentPlayers = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParsePlayers(rawResponse)
                : BattlEyeResponseParser.ParsePlayers(rawResponse);
            parseSw.Stop();

            queryToken.ThrowIfCancellationRequested();

            var dbSw = Stopwatch.StartNew();
            await PlayerDatabaseStorageService.RecordSeenPlayersAsync(currentPlayers);
            dbSw.Stop();

            queryToken.ThrowIfCancellationRequested();

            var currentKeys = currentPlayers.Select(p => p.Uid).ToHashSet();
            var lastKeys = _lastKnownPlayers.Select(p => p.Uid).ToHashSet();

            foreach (var p in currentPlayers.Where(p => !lastKeys.Contains(p.Uid)))
            {
                RaisePlayerJoined(p);
            }

            foreach (var p in _lastKnownPlayers.Where(p => !currentKeys.Contains(p.Uid)))
            {
                RaisePlayerLeft(p);
            }

            _lastKnownPlayers = [.. currentPlayers];
            sw.Stop();

            AppLogger.Info($"[RconService:Timing] Player query finished in {sw.ElapsedMilliseconds} ms (Network: {networkMs} ms, Parse: {parseSw.ElapsedMilliseconds} ms, SQLite: {dbSw.ElapsedMilliseconds} ms, Total Players: {currentPlayers.Count}).");

            op.Complete("PlayerCount", currentPlayers.Count);
            transaction.Finish(SpanStatus.Ok);

            SentrySdk.Metrics.EmitGauge("rcon_online_players", currentPlayers.Count, MeasurementUnit.None,
            [
                new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
            ]);

            SentrySdk.Metrics.EmitDistribution("rcon_ping_ms", PingMs, MeasurementUnit.Duration.Millisecond);

            return currentPlayers;
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);
            AppLogger.Debug("[RconService] In-flight GetPlayers query was cancelled and cleanly discarded.");
            return [];
        }
        catch (SocketException sockEx)
        {
            transaction.Finish(SpanStatus.Unavailable);
            AppLogger.Error($"[RconService] Socket error querying players: {sockEx.SocketErrorCode}", sockEx.Demystify());
            return [];
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            AppLogger.Error("[RconService] Error querying players from server.", ex.Demystify());
            return [];
        }
    }

    public async Task<List<BanModel>> GetBansAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true }) return [];

        CancelInFlightQueries();
        _activeBansQueryCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queryToken = _activeBansQueryCts.Token;

        var sw = Stopwatch.StartNew();
        var transaction = SentrySdk.StartTransaction("GetBans", "rcon.query.bans");
        using var op = Operation.Begin("Query server bans ({Protocol})", CurrentProtocol);
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#ban list" : "bans";

            AppLogger.Debug($"[RconService:Timing] Fetching ban list via '{command}'...");
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(4.0), queryToken);
            queryToken.ThrowIfCancellationRequested();

            var networkMs = sw.ElapsedMilliseconds;

            var parseSw = Stopwatch.StartNew();
            var bans = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParseBans(rawResponse)
                : BattlEyeResponseParser.ParseBans(rawResponse);
            parseSw.Stop();
            sw.Stop();

            AppLogger.Info($"[RconService:Timing] Ban query finished in {sw.ElapsedMilliseconds} ms (Network: {networkMs} ms, Parse: {parseSw.ElapsedMilliseconds} ms, Bans: {bans.Count}).");

            op.Complete("BanCount", bans.Count);
            transaction.Finish(SpanStatus.Ok);

            SentrySdk.Metrics.EmitGauge("rcon_active_bans", bans.Count, MeasurementUnit.None,
            [
                new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
            ]);

            return bans;
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);
            AppLogger.Debug("[RconService] In-flight GetBans query was cancelled and cleanly discarded.");
            return [];
        }
        catch (SocketException sockEx)
        {
            transaction.Finish(SpanStatus.Unavailable);
            AppLogger.Error($"[RconService] Socket error querying bans: {sockEx.SocketErrorCode}", sockEx.Demystify());
            return [];
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            AppLogger.Error("[RconService] Error querying ban list from server.", ex.Demystify());
            return [];
        }
    }

    public Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default) => PlayerDatabaseStorageService.GetAllAsync();

    public async Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default)
    {
        CancelInFlightQueries();

        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#kick {player.Id} {reason}"
            : $"kick {player.Id} {reason}";

        SentrySdk.Metrics.EmitCounter("player_kicks", 1);
        AppLogger.Info($"[RconService] Dispatching kick command: {player.Name} (ID: {player.Id})...");

        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);

        bool isSuccess = VerifyModerationSuccess(response, ["kicked!", ProcessingCommandToken]);
        if (isSuccess)
        {
            AppLogger.Info($"[RconService] Server confirmed kick for {player.Name}.");
        }
        else
        {
            AppLogger.Warn($"[RconService] Kick command for {player.Name} returned unconfirmed response: '{response}'.");
        }

        return isSuccess;
    }

    public async Task<bool> BanPlayerAsync(PlayerModel player, long durationSeconds, string reason, CancellationToken cancellationToken = default)
    {
        CancelInFlightQueries();

        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban create {player.Id} {durationSeconds} {reason}"
            : $"addBan {player.Guid} {beMinutes} {reason}";

        SentrySdk.Metrics.EmitCounter("player_bans", 1,
        [
            new KeyValuePair<string, object>("permanent", (durationSeconds <= 0).ToString(CultureInfo.InvariantCulture))
        ]);

        AppLogger.Info($"[RconService] Dispatching ban command: {player.Name} (Duration: {durationSeconds}s)...");
        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);

        bool isSuccess = VerifyModerationSuccess(response, ["ban created!", "banned!"]);
        if (isSuccess)
        {
            AppLogger.Info($"[RconService] Server confirmed ban for {player.Name}.");
        }
        else
        {
            AppLogger.Warn($"[RconService] Ban command for {player.Name} returned unconfirmed response: '{response}'.");
        }

        return isSuccess;
    }

    public async Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default)
    {
        CancelInFlightQueries();

        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban create {identity} {durationSeconds} {reason}"
            : $"addBan {identity} {beMinutes} {reason}";

        SentrySdk.Metrics.EmitCounter("offline_bans", 1);
        AppLogger.Info($"[RconService] Dispatching offline ban command: {identity}...");
        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);

        return VerifyModerationSuccess(response, ["ban created!", "banned!"]);
    }

    public async Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default)
    {
        CancelInFlightQueries();

        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban remove {ban.IdentityId}"
            : $"removeBan {ban.BanNumber}";

        SentrySdk.Metrics.EmitCounter("ban_removals", 1);
        AppLogger.Info($"[RconService] Dispatching ban removal: {ban.IdentityId}...");
        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);

        bool isSuccess = VerifyModerationSuccess(response, ["ban removed!"]);
        if (isSuccess)
        {
            AppLogger.Info($"[RconService] Server confirmed ban removal for {ban.IdentityId}.");
        }
        else
        {
            AppLogger.Warn($"[RconService] Ban removal for {ban.IdentityId} returned unconfirmed response: '{response}'.");
        }

        return isSuccess;
    }

    private static bool VerifyModerationSuccess(string response, string[] expectedTokens)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return true;
        }

        if (response.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("Server has no bans to list.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return expectedTokens.Any(t => response.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    public Task SendCommandAsync(string rawCommand)
    {
        if (_client is not { Connected: true })
        {
            RaiseOutputReceived($"[ERROR] Cannot send '{rawCommand}' - Client is disconnected.");
            return Task.CompletedTask;
        }

        SentrySdk.Metrics.EmitCounter("rcon_commands_dispatched", 1,
        [
            new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
        ]);

        RaiseOutputReceived($"[RCON OUT] {rawCommand}");
        _client.SendCommand(rawCommand);
        return Task.CompletedTask;
    }

    public Task RestartServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#restart");
    public Task ShutdownServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#shutdown");
    public Task SendGlobalMessageAsync(string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 {message}");
    public Task SendAnnouncementAsync(string title, string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 [ANNOUNCEMENT: {title}] {message}");

    public Task UpdatePlayerCommentAsync(string uid, string comment)
    {
        if (_lastKnownPlayers.FirstOrDefault(x => x.Uid == uid || x.Guid == uid) is { } p)
        {
            p.Comment = comment;
        }

        return PlayerDatabaseStorageService.UpdateCommentAsync(uid, comment);
    }

    public Task ClearDatabaseAsync() => PlayerDatabaseStorageService.ClearAsync();

    private async Task<string> ExecuteCommandWithAggregateResponseAsync(string command, TimeSpan maxTimeout, CancellationToken cancellationToken = default)
    {
        if (_client == null) return string.Empty;

        await _commandExecutionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            RaiseOutputReceived($"[RCON OUT] {command}");

            string directResponse = await _client.SendCommandWithResponseAsync(command, TimeSpan.FromMilliseconds(500), cancellationToken);

            if (CurrentProtocol == RconProtocol.BattlEye && !string.IsNullOrWhiteSpace(directResponse) && !directResponse.StartsWith('\0'))
            {
                AppLogger.Debug($"[RconService:Timing] BattlEye direct packet response received for '{command}'.");
                return directResponse;
            }

            ResetAggregateBuffer();

            bool isBanList = command.StartsWith("#ban list", StringComparison.OrdinalIgnoreCase) || command.Equals("bans", StringComparison.OrdinalIgnoreCase);
            bool isPlayerList = command.StartsWith("#players", StringComparison.OrdinalIgnoreCase) || command.Equals("players", StringComparison.OrdinalIgnoreCase);
            bool isKick = command.StartsWith("#kick", StringComparison.OrdinalIgnoreCase) || command.StartsWith("kick", StringComparison.OrdinalIgnoreCase);
            bool isBanCreate = command.StartsWith("#ban create", StringComparison.OrdinalIgnoreCase) || command.StartsWith("addBan", StringComparison.OrdinalIgnoreCase);
            bool isBanRemove = command.StartsWith("#ban remove", StringComparison.OrdinalIgnoreCase) || command.StartsWith("removeBan", StringComparison.OrdinalIgnoreCase);

            var quietThreshold = TimeSpan.FromMilliseconds(450);
            var timeoutLimit = DateTime.UtcNow.Add(maxTimeout);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow < timeoutLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(25, cancellationToken);

                lock (_bufferLock)
                {
                    var timeSinceLastChunk = DateTime.UtcNow - _lastMessageChunkUtc;
                    var chunksCount = _messageChunksCount;
                    var currentText = _aggregatedBuffer.ToString();

                    if (CheckUniversalErrorTokens(command, currentText))
                    {
                        break;
                    }

                    if (CheckCommandSpecificTerminalTokens(command, isBanList, isKick, isBanCreate, isBanRemove, currentText))
                    {
                        break;
                    }

                    bool hasActualPayload = DeterminePayloadPresence(isPlayerList, isBanList, currentText, chunksCount);

                    if (hasActualPayload && timeSinceLastChunk >= quietThreshold)
                    {
                        AppLogger.Debug($"[RconService:Timing] Stream collection completed: {chunksCount} chunk(s) collected for '{command}' (Quiet window: {timeSinceLastChunk.TotalMilliseconds:F0} ms).");
                        break;
                    }

                    if (chunksCount == 0 && (DateTime.UtcNow - startTime).TotalMilliseconds >= 1200)
                    {
                        AppLogger.Debug($"[RconService:Timing] No server message chunks received for '{command}' within 1200ms window.");
                        break;
                    }
                }
            }

            lock (_bufferLock)
            {
                return _aggregatedBuffer.ToString();
            }
        }
        finally
        {
            _commandExecutionLock.Release();
        }
    }

    private void ResetAggregateBuffer()
    {
        lock (_bufferLock)
        {
            _aggregatedBuffer.Clear();
            _lastMessageChunkUtc = DateTime.UtcNow;
            _messageChunksCount = 0;
        }
    }

    private static bool CheckUniversalErrorTokens(string command, string currentText)
    {
        if (currentText.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            currentText.Contains("Help for ban command.", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Debug($"[RconService:Timing] Error / Help token detected for '{command}'. Completing stream immediately.");
            return true;
        }
        return false;
    }

    private static bool CheckCommandSpecificTerminalTokens(string command, bool isBanList, bool isKick, bool isBanCreate, bool isBanRemove, string currentText)
    {
        if (isBanList && currentText.Contains("Server has no bans to list.", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Debug($"[RconService:Timing] Empty ban list token detected for '{command}'. Completing stream.");
            return true;
        }

        if (isKick && (currentText.Contains("kicked!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            AppLogger.Debug($"[RconService:Timing] Kick completion token detected for '{command}'.");
            return true;
        }

        if (isBanCreate && (currentText.Contains("banned!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("Ban created!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
        {
            AppLogger.Debug($"[RconService:Timing] Ban creation token detected for '{command}'.");
            return true;
        }

        if (isBanRemove && (currentText.Contains("Ban removed!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            AppLogger.Debug($"[RconService:Timing] Ban removal token detected for '{command}'.");
            return true;
        }

        return false;
    }

    private static bool DeterminePayloadPresence(bool isPlayerList, bool isBanList, string currentText, int chunksCount)
    {
        if (isPlayerList)
        {
            return currentText.Contains("Players on server:", StringComparison.OrdinalIgnoreCase);
        }

        if (isBanList)
        {
            return currentText.Contains("Total bans:", StringComparison.OrdinalIgnoreCase) || currentText.Contains("GUID Bans:", StringComparison.OrdinalIgnoreCase);
        }

        return chunksCount > 0;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        StopBackgroundPingMonitor();
        CancelInFlightQueries();

        _client?.Dispose();
        _client = null;

        _icmpPingSender.Dispose();
        _commandExecutionLock.Dispose();
    }
}