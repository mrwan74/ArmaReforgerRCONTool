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
    private readonly ConcurrentDictionary<string, long> _recentlyAnnouncedJoins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _recentlyAnnouncedLeaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _aggregatedBuffer = new();
    private readonly Lock _bufferLock = new();
    private readonly SemaphoreSlim _commandExecutionLock = new(1, 1);
    private DateTime _lastMessageChunkUtc = DateTime.UtcNow;
    private int _messageChunksCount;
    private bool _isDisposed;
    private volatile bool _hasInitialPlayerSnapshot;

    private readonly System.Net.NetworkInformation.Ping _icmpPingSender = new();
    private readonly Queue<int> _pingSamples = new();
    private readonly Lock _pingLock = new();
    private CancellationTokenSource? _pingLoopCts;
    private int _smoothedPingMs;

    public RconProtocol CurrentProtocol => _currentProfile?.Protocol ?? RconProtocol.ReforgerBuiltIn;
    public bool IsConnected => _client is { Connected: true };
    public int PingMs => _smoothedPingMs > 0 ? _smoothedPingMs : (_client?.LastPingMs ?? 0);
    public DateTime LastPacketTime { get; private set; } = DateTime.UtcNow;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<PlayerModel>? PlayerJoined;
    public event EventHandler<PlayerModel>? PlayerLeft;
    public event EventHandler<(string Name, int Id, string Reason)>? PlayerKickedStream;
    public event EventHandler<(string Name, int Id, string Guid, string Reason)>? PlayerBannedStream;
    public event EventHandler<(int AdminId, string Endpoint)>? AdminConnectedStream;
    public event EventHandler<string>? ConnectionLost;

    private readonly List<PlayerModel> _lastKnownPlayers = [];
    private readonly Lock _playersLock = new();

    private void RaiseOutputReceived(string message) => OutputReceived?.Invoke(this, message);

    private void RaisePlayerJoined(PlayerModel player)
    {
        var key = $"{player.Id}_{player.Name.Trim()}";
        var now = Stopwatch.GetTimestamp();

        if (_recentlyAnnouncedJoins.TryGetValue(key, out var lastAnnounced))
        {
            var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
            if (elapsedSec < 6.0)
            {
                AppLogger.Debug($"[RconService] Suppressed duplicate PlayerJoined notification for '{player.Name}' (ID: #{player.Id}, Elapsed: {elapsedSec:F1}s).");
                return;
            }
        }

        _recentlyAnnouncedJoins[key] = now;
        _recentlyAnnouncedLeaves.TryRemove(key, out _);

        AppLogger.Info($"[RconService] Dispatching PlayerJoined event for '{player.Name}' (ID: #{player.Id}, GUID: '{player.Guid}', Location: '{player.DisplayLocation}').");
        PlayerJoined?.Invoke(this, player);
    }

    private void RaisePlayerLeft(PlayerModel player)
    {
        var key = $"{player.Id}_{player.Name.Trim()}";
        var now = Stopwatch.GetTimestamp();

        if (_recentlyAnnouncedLeaves.TryGetValue(key, out var lastAnnounced))
        {
            var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
            if (elapsedSec < 6.0)
            {
                AppLogger.Debug($"[RconService] Suppressed duplicate PlayerLeft notification for '{player.Name}' (ID: #{player.Id}, Elapsed: {elapsedSec:F1}s).");
                return;
            }
        }

        _recentlyAnnouncedLeaves[key] = now;
        _recentlyAnnouncedJoins.TryRemove(key, out _);

        AppLogger.Info($"[RconService] Dispatching PlayerLeft event for '{player.Name}' (ID: #{player.Id}).");
        PlayerLeft?.Invoke(this, player);
    }

    private void RaiseConnectionLost(string reason) => ConnectionLost?.Invoke(this, reason);

    public async Task<bool> ConnectAsync(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ServerIp);

        using var timing = AppLogger.Measure($"RconService.ConnectAsync({profile.ServerIp}:{profile.Port})");
        _currentProfile = profile;
        _hasInitialPlayerSnapshot = false;
        _recentlyAnnouncedJoins.Clear();
        _recentlyAnnouncedLeaves.Clear();

        lock (_playersLock)
        {
            _lastKnownPlayers.Clear();
        }

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
                AppLogger.Debug($"[RconService] Resolving DNS hostname for '{profile.ServerIp}'...");
                var addresses = await Dns.GetHostAddressesAsync(profile.ServerIp, CancellationToken.None);
                if (addresses.Length > 0)
                {
                    ip = addresses[0];
                    AppLogger.Info($"[RconService] Hostname '{profile.ServerIp}' resolved to IP: {ip}");
                }
            }

            if (ip == null)
            {
                AppLogger.Error($"[RconService] Unable to resolve hostname: '{profile.ServerIp}'");
                transaction.Finish(SpanStatus.InvalidArgument);
                return false;
            }

            var credentials = new BattlEyeLoginCredentials(ip, profile.Port, profile.Password);
            _client = new BattlEyeClient(credentials);

            var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _client.BattlEyeConnected += args =>
            {
                AppLogger.Info($"[RconService] Handshake result received: {args.ConnectionResult} ('{args.Message}')");
                connectTcs.TrySetResult(args.ConnectionResult == BattlEyeConnectionResult.Success);
            };

            _client.BattlEyeDisconnected += async args =>
            {
                AppLogger.Warn($"[RconService] Connection dropped: Type={args.DisconnectionType}, Message='{args.Message}'");
                RaiseOutputReceived($"[SYSTEM] Disconnected: {args.Message}");

                StopBackgroundPingMonitor();
                _hasInitialPlayerSnapshot = false;
                await PlayerDatabaseStorageService.SetAllOfflineAsync(_currentProfile?.Protocol);

                lock (_playersLock)
                {
                    _lastKnownPlayers.Clear();
                }
                _recentlyAnnouncedJoins.Clear();
                _recentlyAnnouncedLeaves.Clear();

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
                _hasInitialPlayerSnapshot = true;
                RaiseOutputReceived($"[SYSTEM] Connected successfully to {profile.ServerIp}:{profile.Port}");
                AppLogger.Info($"[RconService] Verified live connection to {profile.ServerIp}:{profile.Port} via {profile.Protocol}.");

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
            AppLogger.Error($"[RconService] Socket error during connection: {sockEx.SocketErrorCode}", sockEx.Demystify());
            return false;
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);
            AppLogger.Warn($"[RconService] Connection attempt to {profile.ServerIp}:{profile.Port} was cancelled.");
            return false;
        }
        catch (ArgumentException argEx)
        {
            transaction.Finish(SpanStatus.InvalidArgument);
            AppLogger.Error($"[RconService] Invalid argument during connection: {argEx.Message}", argEx.Demystify());
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        using var timing = AppLogger.Measure("RconService.DisconnectAsync");
        AppLogger.Info("[RconService] Executing graceful disconnection sequence...");
        StopBackgroundPingMonitor();
        _hasInitialPlayerSnapshot = false;

        if (_client is { Connected: true } && CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            try
            {
                AppLogger.Info("[RconService] Sending '@logout' command to release server session slot...");
                _client.SendCommand("@logout", log: false);
                await Task.Delay(100, CancellationToken.None);
            }
            catch (SocketException sockEx)
            {
                AppLogger.Debug($"[RconService] Socket notice while sending '@logout': {sockEx.Message}");
            }
            catch (ObjectDisposedException dispEx)
            {
                AppLogger.Debug($"[RconService] Socket disposed prior to '@logout': {dispEx.Message}");
            }
        }

        await PlayerDatabaseStorageService.SetAllOfflineAsync(CurrentProtocol);

        lock (_playersLock)
        {
            _lastKnownPlayers.Clear();
        }
        _recentlyAnnouncedJoins.Clear();
        _recentlyAnnouncedLeaves.Clear();

        _client?.Dispose();
        _client = null;

        RaiseOutputReceived("[SYSTEM] Disconnected from server.");
    }

    private void StartBackgroundPingMonitor(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
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
                AppLogger.Warn($"[RconService] Background ping monitor notice: {ex.Message}");
            }
        }, token);
    }

    private void StopBackgroundPingMonitor()
    {
        if (_pingLoopCts != null)
        {
            try
            {
                _pingLoopCts.Cancel();
                _pingLoopCts.Dispose();
            }
            catch (ObjectDisposedException dispEx)
            {
                AppLogger.Trace($"[RconService] Ping monitor CTS already disposed: {dispEx.Message}");
            }
            _pingLoopCts = null;
        }

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
            AppLogger.Trace($"[RconService] ICMP ping fallback notice: {pingEx.Message}");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Trace($"[RconService] ICMP socket notice: {sockEx.Message}");
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

        AppLogger.Trace($"[RconService] Network Ping Measurement: {sampleMs} ms (ICMP: {isIcmp}, Filtered: {_smoothedPingMs} ms)");
    }

    private void OnBattlEyeMessageReceived(BattlEyeMessageEventArgs args)
    {
        LastPacketTime = DateTime.UtcNow;
        var message = args.Message;

        lock (_bufferLock)
        {
            _aggregatedBuffer.Append(message);
            _lastMessageChunkUtc = DateTime.UtcNow;
            _messageChunksCount++;
        }

        if (args.Id != 256 && _pendingCommands.TryRemove(args.Id, out var tcs))
        {
            tcs.TrySetResult(message);
        }

        ProcessLiveStreamEvent(message);
        RaiseOutputReceived($"[RCON IN] {message}");
    }

    private void ProcessLiveStreamEvent(string message)
    {
        try
        {
            var disconnMatch = BattlEyeResponseParser.PlayerDisconnectedStreamRegex().Match(message);
            if (disconnMatch.Success && int.TryParse(disconnMatch.Groups[1].Value, out int discId))
            {
                var name = disconnMatch.Groups[2].Value.Trim();
                AppLogger.Info($"[RconService:Stream] Player disconnected event: #{discId} '{name}'");

                PlayerModel matched;
                lock (_playersLock)
                {
                    matched = _lastKnownPlayers.FirstOrDefault(p =>
                        p.Id == discId ||
                        (!string.IsNullOrEmpty(name) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        ?? new PlayerModel { Id = discId, Name = name };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }

                RaisePlayerLeft(matched);
                return;
            }

            var guidMatch = BattlEyeResponseParser.PlayerGuidStreamRegex().Match(message);
            if (guidMatch.Success && int.TryParse(guidMatch.Groups[1].Value, out int guidPlayerId))
            {
                var name = guidMatch.Groups[2].Value.Trim();
                var guid = guidMatch.Groups[3].Value.Trim();
                AppLogger.Info($"[RconService:Stream] Player GUID resolved: #{guidPlayerId} '{name}' -> GUID: {guid}");

                PlayerModel matched;
                bool isNew = false;

                lock (_playersLock)
                {
                    var existing = _lastKnownPlayers.FirstOrDefault(p => p.Id == guidPlayerId || string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        existing.Guid = guid;
                        existing.BattlEyeGuid = guid;
                        matched = existing;
                    }
                    else
                    {
                        matched = new PlayerModel
                        {
                            Id = guidPlayerId,
                            Name = name,
                            Guid = guid,
                            BattlEyeGuid = guid
                        };
                        _lastKnownPlayers.Add(matched);
                        isNew = true;
                    }
                }

                _ = PlayerDatabaseStorageService.RecordSeenPlayersAsync([matched], CurrentProtocol);

                if (isNew)
                {
                    RaisePlayerJoined(matched);
                }
                return;
            }

            var banMatch = BattlEyeResponseParser.PlayerBannedStreamRegex().Match(message);
            if (banMatch.Success && int.TryParse(banMatch.Groups[1].Value, out int banId))
            {
                var name = banMatch.Groups[2].Value.Trim();
                var guid = banMatch.Groups[3].Value.Trim();
                var reason = banMatch.Groups[4].Success ? banMatch.Groups[4].Value.Trim() : "Admin Ban";
                AppLogger.Info($"[RconService:Stream] Player banned event: #{banId} '{name}' ({guid}) - Reason: '{reason}'");

                PlayerModel matched;
                lock (_playersLock)
                {
                    matched = _lastKnownPlayers.FirstOrDefault(p =>
                        p.Id == banId ||
                        (!string.IsNullOrEmpty(guid) && p.Guid == guid) ||
                        (!string.IsNullOrEmpty(name) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        ?? new PlayerModel { Id = banId, Name = name, Guid = guid, BattlEyeGuid = guid };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }

                RaisePlayerLeft(matched);
                PlayerBannedStream?.Invoke(this, (name, banId, guid, reason));
                return;
            }

            var kickMatch = BattlEyeResponseParser.PlayerKickedStreamRegex().Match(message);
            if (kickMatch.Success && int.TryParse(kickMatch.Groups[1].Value, out int kickId))
            {
                var name = kickMatch.Groups[2].Value.Trim();
                var guid = kickMatch.Groups[3].Value.Trim();
                var reason = kickMatch.Groups[4].Success ? kickMatch.Groups[4].Value.Trim() : "Admin Kick";
                AppLogger.Info($"[RconService:Stream] Player kicked event: #{kickId} '{name}' ({guid}) - Reason: '{reason}'");

                PlayerModel matched;
                lock (_playersLock)
                {
                    matched = _lastKnownPlayers.FirstOrDefault(p =>
                        p.Id == kickId ||
                        (!string.IsNullOrEmpty(guid) && p.Guid == guid) ||
                        (!string.IsNullOrEmpty(name) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        ?? new PlayerModel { Id = kickId, Name = name, Guid = guid, BattlEyeGuid = guid };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }

                RaisePlayerLeft(matched);
                PlayerKickedStream?.Invoke(this, (name, kickId, reason));
                return;
            }

            var connMatch = BattlEyeResponseParser.PlayerConnectedStreamRegex().Match(message);
            if (connMatch.Success && int.TryParse(connMatch.Groups[1].Value, out int connId))
            {
                var name = connMatch.Groups[2].Value.Trim();
                var ip = connMatch.Groups[3].Value.Trim();
                int port = int.TryParse(connMatch.Groups[4].Value, out int p) ? p : 2304;
                var geo = GeoIpService.GetLocation(ip);

                AppLogger.Info($"[RconService:Stream] Player connected event: #{connId} '{name}' ({ip}:{port}) -> {geo.NaturalLocation}");
                var newPlayer = new PlayerModel
                {
                    Id = connId,
                    Name = name,
                    Ip = ip,
                    Port = port,
                    Country = new CountryInfo { Code = geo.CountryCode, Name = geo.CountryName },
                    LocationCity = geo.CityName,
                    LocationState = geo.SubdivisionName,
                    DisplayLocation = geo.NaturalLocation,
                    TimeZone = geo.TimeZone
                };

                lock (_playersLock)
                {
                    _lastKnownPlayers.RemoveAll(x => IsSamePlayer(x, newPlayer));
                    _lastKnownPlayers.Add(newPlayer);
                }

                _ = PlayerDatabaseStorageService.RecordSeenPlayersAsync([newPlayer], CurrentProtocol);
                RaisePlayerJoined(newPlayer);
                return;
            }

            var adminMatch = BattlEyeResponseParser.AdminConnectedStreamRegex().Match(message);
            if (adminMatch.Success && int.TryParse(adminMatch.Groups[1].Value, out int adminId))
            {
                var endpoint = adminMatch.Groups[2].Value.Trim();
                AppLogger.Info($"[RconService:Stream] RCon admin logged in: Admin #{adminId} ({endpoint})");
                AdminConnectedStream?.Invoke(this, (adminId, endpoint));
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[RconService] Stream dispatch notice: {ex.Message}");
        }
    }

    public static bool IsSamePlayer(PlayerModel a, PlayerModel b)
    {
        if (!string.IsNullOrEmpty(a.BattlEyeGuid) && !string.IsNullOrEmpty(b.BattlEyeGuid))
        {
            return string.Equals(a.BattlEyeGuid, b.BattlEyeGuid, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrEmpty(a.ReforgerUid) && !string.IsNullOrEmpty(b.ReforgerUid))
        {
            return string.Equals(a.ReforgerUid, b.ReforgerUid, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrEmpty(a.Guid) && !string.IsNullOrEmpty(b.Guid) && !a.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase) && !b.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(a.Guid, b.Guid, StringComparison.OrdinalIgnoreCase);
        }
        if (!string.IsNullOrEmpty(a.Uid) && !string.IsNullOrEmpty(b.Uid) && !a.Uid.StartsWith("init", StringComparison.OrdinalIgnoreCase) && !b.Uid.StartsWith("init", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(a.Uid, b.Uid, StringComparison.OrdinalIgnoreCase);
        }
        return a.Id == b.Id || (!string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(b.Name) && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<List<PlayerModel>> GetPlayersAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true }) return [];

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queryToken = linkedCts.Token;

        var sw = Stopwatch.StartNew();
        var transaction = SentrySdk.StartTransaction("GetPlayers", "rcon.query.players");
        using var op = Operation.Begin("Query active players list ({Protocol})", CurrentProtocol);
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#players" : "players";

            AppLogger.Debug($"[RconService:Timing] Requesting player list via '{command}'...");

            var networkSpan = transaction.StartChild("network.rcon.command", $"Execute '{command}'");
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(4.0), queryToken);
            networkSpan.Finish(SpanStatus.Ok);

            queryToken.ThrowIfCancellationRequested();

            var parseSpan = transaction.StartChild("parser.players", $"Parse {CurrentProtocol} player buffer");
            List<PlayerModel> currentPlayers = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParsePlayers(rawResponse)
                : BattlEyeResponseParser.ParsePlayers(rawResponse);
            parseSpan.Finish(SpanStatus.Ok);

            queryToken.ThrowIfCancellationRequested();

            var dbSpan = transaction.StartChild("db.sqlite.record", "Record seen active players");
            await PlayerDatabaseStorageService.RecordSeenPlayersAsync(currentPlayers, CurrentProtocol);
            dbSpan.Finish(SpanStatus.Ok);

            queryToken.ThrowIfCancellationRequested();

            lock (_playersLock)
            {
                if (!_hasInitialPlayerSnapshot)
                {
                    _hasInitialPlayerSnapshot = true;
                    _lastKnownPlayers.Clear();
                    _lastKnownPlayers.AddRange(currentPlayers);

                    foreach (var p in currentPlayers)
                    {
                        var key = $"{p.Id}_{p.Name.Trim()}";
                        _recentlyAnnouncedJoins[key] = Stopwatch.GetTimestamp();
                    }

                    AppLogger.Info($"[RconService] Baseline player snapshot initialized with {currentPlayers.Count} player(s).");
                }
                else
                {
                    foreach (var p in currentPlayers.Where(p => !_lastKnownPlayers.Any(old => IsSamePlayer(p, old))))
                    {
                        RaisePlayerJoined(p);
                    }

                    foreach (var p in _lastKnownPlayers.Where(p => !currentPlayers.Any(curr => IsSamePlayer(p, curr))))
                    {
                        RaisePlayerLeft(p);
                    }

                    _lastKnownPlayers.Clear();
                    _lastKnownPlayers.AddRange(currentPlayers);
                }
            }

            sw.Stop();
            AppLogger.Info($"[RconService:Timing] Player query completed in {sw.ElapsedMilliseconds} ms (Total Players: {currentPlayers.Count}).");

            op.Complete("PlayerCount", currentPlayers.Count);
            transaction.Finish(SpanStatus.Ok);

            SentrySdk.Metrics.EmitGauge("rcon_online_players", currentPlayers.Count, MeasurementUnit.None,
            [
                new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
            ]);

            return currentPlayers;
        }
        catch (OperationCanceledException)
        {
            op.Cancel();
            transaction.Finish(SpanStatus.Cancelled);
            AppLogger.Debug("[RconService] In-flight GetPlayers query was cancelled.");
            return [];
        }
        catch (SocketException sockEx)
        {
            transaction.Finish(SpanStatus.Unavailable);
            AppLogger.Error($"[RconService] Socket error querying players: {sockEx.SocketErrorCode}", sockEx.Demystify());
            return [];
        }
    }

    public async Task<List<BanModel>> GetBansAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true }) return [];

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queryToken = linkedCts.Token;

        var sw = Stopwatch.StartNew();
        var transaction = SentrySdk.StartTransaction("GetBans", "rcon.query.bans");
        using var op = Operation.Begin("Query server bans ({Protocol})", CurrentProtocol);
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#ban list" : "bans";

            AppLogger.Debug($"[RconService:Timing] Fetching ban list via '{command}'...");

            var networkSpan = transaction.StartChild("network.rcon.command", $"Execute '{command}'");
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(4.0), queryToken);
            networkSpan.Finish(SpanStatus.Ok);

            queryToken.ThrowIfCancellationRequested();

            var parseSpan = transaction.StartChild("parser.bans", $"Parse {CurrentProtocol} ban buffer");
            var bans = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParseBans(rawResponse)
                : BattlEyeResponseParser.ParseBans(rawResponse);
            parseSpan.Finish(SpanStatus.Ok);
            sw.Stop();

            AppLogger.Info($"[RconService:Timing] Ban query completed in {sw.ElapsedMilliseconds} ms (Total Bans: {bans.Count}).");

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
            AppLogger.Debug("[RconService] In-flight GetBans query was cancelled.");
            return [];
        }
        catch (SocketException sockEx)
        {
            transaction.Finish(SpanStatus.Unavailable);
            AppLogger.Error($"[RconService] Socket error querying bans: {sockEx.SocketErrorCode}", sockEx.Demystify());
            return [];
        }
    }

    public async Task<List<AdminModel>> GetAdminsAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true }) return [];

        var transaction = SentrySdk.StartTransaction("GetAdmins", "rcon.query.admins");
        try
        {
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync("admins", TimeSpan.FromSeconds(3.0), cancellationToken);
            var admins = BattlEyeResponseParser.ParseAdmins(rawResponse);
            transaction.Finish(SpanStatus.Ok);
            return admins;
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.UnknownError);
            AppLogger.Error($"[RconService] Failed querying connected admins: {ex.Message}", ex.Demystify());
            return [];
        }
    }

    public Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default) => PlayerDatabaseStorageService.GetAllAsync(CurrentProtocol);

    private static string BuildKickCommand(int playerId, string reason, RconProtocol protocol)
    {
        var prefix = protocol == RconProtocol.ReforgerBuiltIn ? "#kick" : "kick";
        return string.IsNullOrEmpty(reason) ? $"{prefix} {playerId}" : $"{prefix} {playerId} {reason}";
    }

    public async Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var transaction = SentrySdk.StartTransaction("KickPlayer", "rcon.moderation.kick");
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        string cmd = BuildKickCommand(player.Id, cleanReason, CurrentProtocol);

        SentrySdk.Metrics.EmitCounter("player_kicks", 1,
        [
            new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
        ]);
        AppLogger.Info($"[RconService] Executing kick command for '{player.Name}' (ID: #{player.Id})...");

        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);

        bool isSuccess = VerifyModerationSuccess(response, ["kicked!", "Admin Kick", ProcessingCommandToken]);
        if (isSuccess)
        {
            AppLogger.Info($"[RconService] Server confirmed kick for '{player.Name}'.");
            transaction.Finish(SpanStatus.Ok);
        }
        else
        {
            AppLogger.Warn($"[RconService] Kick command for '{player.Name}' returned unverified response: '{response}'.");
            transaction.Finish(SpanStatus.UnknownError);
        }

        return isSuccess;
    }

    public Task<bool> BanPlayerAsync(PlayerModel player, long durationSeconds, string reason, CancellationToken cancellationToken = default)
    {
        return BanPlayerWithOptionalIpAsync(player, durationSeconds, reason, banIp: true, cancellationToken);
    }

    private static string BuildReforgerBanCommand(int playerId, long durationSeconds, string reason)
    {
        return string.IsNullOrEmpty(reason)
            ? $"#ban create {playerId} {durationSeconds}"
            : $"#ban create {playerId} {durationSeconds} {reason}";
    }

    private static string BuildBattlEyeBanCommand(int playerId, long minutes, string reason)
    {
        return string.IsNullOrEmpty(reason)
            ? $"ban {playerId} {minutes}"
            : $"ban {playerId} {minutes} {reason}";
    }

    private static string BuildBattlEyeAddBanIpCommand(string ip, long minutes, string reason)
    {
        return string.IsNullOrEmpty(reason)
            ? $"addBan {ip} {minutes}"
            : $"addBan {ip} {minutes} {reason}";
    }

    public async Task<bool> BanPlayerWithOptionalIpAsync(PlayerModel player, long durationSeconds, string reason, bool banIp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var transaction = SentrySdk.StartTransaction("BanPlayer", "rcon.moderation.ban");
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        SentrySdk.Metrics.EmitCounter("player_bans", 1,
        [
            new KeyValuePair<string, object>("permanent", (durationSeconds <= 0).ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
        ]);

        if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            string cmd = BuildReforgerBanCommand(player.Id, durationSeconds, cleanReason);
            AppLogger.Info($"[RconService] Executing Reforger ban for '{player.Name}' (Duration: {durationSeconds}s)...");
            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);
            bool verified = VerifyModerationSuccess(response, ["ban created!", "banned!"]);
            transaction.Finish(verified ? SpanStatus.Ok : SpanStatus.UnknownError);
            return verified;
        }

        string beCmd = BuildBattlEyeBanCommand(player.Id, beMinutes, cleanReason);
        AppLogger.Info($"[RconService] Executing BattlEye player ban for '{player.Name}' (ID: #{player.Id}, Minutes: {beMinutes})...");
        string beResponse = await ExecuteCommandWithAggregateResponseAsync(beCmd, TimeSpan.FromSeconds(3.5), cancellationToken);

        bool banSuccess = VerifyModerationSuccess(beResponse, ["Admin Ban", "kicked by BattlEye"]);

        if (banIp && !string.IsNullOrWhiteSpace(player.Ip) && !player.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(player.Ip, out _))
        {
            string addBanIpCmd = BuildBattlEyeAddBanIpCommand(player.Ip, beMinutes, cleanReason);
            AppLogger.Info($"[RconService] Executing supplementary BattlEye IP ban for '{player.Ip}'...");
            await SendCommandAsync(addBanIpCmd);
            await Task.Delay(100, cancellationToken);
            await SendCommandAsync("loadBans");
        }

        transaction.Finish(banSuccess ? SpanStatus.Ok : SpanStatus.UnknownError);
        return banSuccess;
    }

    public async Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var transaction = SentrySdk.StartTransaction("OfflineBan", "rcon.moderation.offline_ban");
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        SentrySdk.Metrics.EmitCounter("offline_bans", 1,
        [
            new KeyValuePair<string, object>("is_ip", isIp.ToString(CultureInfo.InvariantCulture)),
            new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
        ]);
        AppLogger.Info($"[RconService] Executing offline ban command for '{identity}' (Duration: {durationSeconds}s)...");

        if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            string cmd = string.IsNullOrEmpty(cleanReason)
                ? $"#ban create {identity} {durationSeconds}"
                : $"#ban create {identity} {durationSeconds} {cleanReason}";

            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);
            bool verified = VerifyModerationSuccess(response, ["ban created!", "banned!"]);
            transaction.Finish(verified ? SpanStatus.Ok : SpanStatus.UnknownError);
            return verified;
        }

        string beAddCmd = string.IsNullOrEmpty(cleanReason)
            ? $"addBan {identity} {beMinutes}"
            : $"addBan {identity} {beMinutes} {cleanReason}";

        await SendCommandAsync(beAddCmd);
        await Task.Delay(100, cancellationToken);
        await SendCommandAsync("loadBans");
        transaction.Finish(SpanStatus.Ok);
        return true;
    }

    public async Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ban);

        var transaction = SentrySdk.StartTransaction("RemoveBan", "rcon.moderation.remove_ban");
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban remove {ban.IdentityId}"
            : $"removeBan {ban.BanNumber}";

        SentrySdk.Metrics.EmitCounter("ban_removals", 1,
        [
            new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
        ]);
        AppLogger.Info($"[RconService] Executing ban removal for '{ban.IdentityId}' (#{ban.BanNumber})...");
        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(3.5), cancellationToken);

        if (CurrentProtocol == RconProtocol.BattlEye)
        {
            await Task.Delay(100, cancellationToken);
            await SendCommandAsync("writeBans");
            transaction.Finish(SpanStatus.Ok);
            return true;
        }

        bool isSuccess = VerifyModerationSuccess(response, ["ban removed!"]);
        if (isSuccess)
        {
            AppLogger.Info($"[RconService] Server confirmed ban removal for '{ban.IdentityId}'.");
            transaction.Finish(SpanStatus.Ok);
        }
        else
        {
            AppLogger.Warn($"[RconService] Ban removal for '{ban.IdentityId}' returned unverified response: '{response}'.");
            transaction.Finish(SpanStatus.UnknownError);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCommand);

        if (_client is not { Connected: true })
        {
            RaiseOutputReceived($"[ERROR] Cannot dispatch '{rawCommand}': Socket disconnected.");
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
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        lock (_playersLock)
        {
            if (_lastKnownPlayers.FirstOrDefault(x => x.Uid == uid || x.Guid == uid || x.ReforgerUid == uid || x.BattlEyeGuid == uid) is { } p)
            {
                p.Comment = comment;
            }
        }

        return PlayerDatabaseStorageService.UpdateCommentAsync(uid, comment, CurrentProtocol);
    }

    public Task ClearDatabaseAsync() => PlayerDatabaseStorageService.ClearDatabaseAsync(CurrentProtocol);

    private async Task<string> ExecuteCommandWithAggregateResponseAsync(string command, TimeSpan maxTimeout, CancellationToken cancellationToken = default)
    {
        if (_client == null) return string.Empty;

        await _commandExecutionLock.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RaiseOutputReceived($"[RCON OUT] {command}");

            string directResponse = await _client.SendCommandWithResponseAsync(command, TimeSpan.FromMilliseconds(400), cancellationToken);

            if (CurrentProtocol == RconProtocol.BattlEye && !string.IsNullOrWhiteSpace(directResponse) && !directResponse.StartsWith('\0'))
            {
                AppLogger.Debug($"[RconService:Timing] BattlEye direct response received for '{command}'.");
                return directResponse;
            }

            ResetAggregateBuffer();

            bool isBanList = command.StartsWith("#ban list", StringComparison.OrdinalIgnoreCase) || command.Equals("bans", StringComparison.OrdinalIgnoreCase) || command.Equals("ban list", StringComparison.OrdinalIgnoreCase);
            bool isPlayerList = command.StartsWith("#players", StringComparison.OrdinalIgnoreCase) || command.Equals("players", StringComparison.OrdinalIgnoreCase);
            bool isKick = command.StartsWith("#kick", StringComparison.OrdinalIgnoreCase) || command.StartsWith("kick", StringComparison.OrdinalIgnoreCase);
            bool isBanCreate = command.StartsWith("#ban create", StringComparison.OrdinalIgnoreCase) || command.StartsWith("ban ", StringComparison.OrdinalIgnoreCase) || command.StartsWith("addBan", StringComparison.OrdinalIgnoreCase);
            bool isBanRemove = command.StartsWith("#ban remove", StringComparison.OrdinalIgnoreCase) || command.StartsWith("removeBan", StringComparison.OrdinalIgnoreCase);

            var quietThreshold = TimeSpan.FromMilliseconds(400);
            var timeoutLimit = DateTime.UtcNow.Add(maxTimeout);
            var startTime = DateTime.UtcNow;

            while (DateTime.UtcNow < timeoutLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(20, cancellationToken);

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
                        AppLogger.Debug($"[RconService:Timing] No server chunks received for '{command}' within initial 1200ms window.");
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
            AppLogger.Debug($"[RconService:Timing] Terminal error/help token observed for '{command}'. Completing stream.");
            return true;
        }
        return false;
    }

    private static bool CheckCommandSpecificTerminalTokens(string command, bool isBanList, bool isKick, bool isBanCreate, bool isBanRemove, string currentText)
    {
        if (isBanList && currentText.Contains("Server has no bans to list.", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Debug($"[RconService:Timing] Empty ban list token detected for '{command}'.");
            return true;
        }

        if (isKick && (currentText.Contains("kicked!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("Admin Kick", StringComparison.OrdinalIgnoreCase) || currentText.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            AppLogger.Debug($"[RconService:Timing] Kick completion token detected for '{command}'.");
            return true;
        }

        if (isBanCreate && (currentText.Contains("banned!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("Admin Ban", StringComparison.OrdinalIgnoreCase) || currentText.Contains("Ban created!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
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

        _client?.Dispose();
        _client = null;

        _icmpPingSender.Dispose();
        _commandExecutionLock.Dispose();
    }
}