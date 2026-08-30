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
    private DateTime _lastMessageChunkUtc = DateTime.UtcNow;
    private int _messageChunksCount;
    private bool _isDisposed;
    private volatile bool _hasInitialPlayerSnapshot;
    private volatile bool _isInitialConnectPhase;

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

    private void RaiseOutputReceived(string message)
    {
        try
        {
            OutputReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error dispatching OutputReceived event: {ex.Message}", ex);
        }
    }

    private void RaisePlayerJoined(PlayerModel player)
    {
        try
        {
            if (!_hasInitialPlayerSnapshot || _isInitialConnectPhase) return;

            var key = $"{player.Id}_{player.Name.Trim()}";
            var now = Stopwatch.GetTimestamp();

            if (_recentlyAnnouncedJoins.TryGetValue(key, out var lastAnnounced))
            {
                var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
                if (elapsedSec < 6.0)
                {
                    return;
                }
            }

            _recentlyAnnouncedJoins[key] = now;
            _recentlyAnnouncedLeaves.TryRemove(key, out _);

            AppLogger.Info($"[RconService] Dispatching PlayerJoined event for '{player.Name}' (ID: #{player.Id}, GUID: '{player.Guid}', Location: '{player.DisplayLocation}').");
            PlayerJoined?.Invoke(this, player);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error dispatching PlayerJoined: {ex.Message}", ex);
        }
    }

    private void RaisePlayerLeft(PlayerModel player)
    {
        try
        {
            if (!_hasInitialPlayerSnapshot || _isInitialConnectPhase) return;

            var key = $"{player.Id}_{player.Name.Trim()}";
            var now = Stopwatch.GetTimestamp();

            if (_recentlyAnnouncedLeaves.TryGetValue(key, out var lastAnnounced))
            {
                var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
                if (elapsedSec < 6.0)
                {
                    return;
                }
            }

            _recentlyAnnouncedLeaves[key] = now;
            _recentlyAnnouncedJoins.TryRemove(key, out _);

            AppLogger.Info($"[RconService] Dispatching PlayerLeft event for '{player.Name}' (ID: #{player.Id}).");
            PlayerLeft?.Invoke(this, player);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error dispatching PlayerLeft: {ex.Message}", ex);
        }
    }

    private void RaiseConnectionLost(string reason)
    {
        try
        {
            ConnectionLost?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error in ConnectionLost handler: {ex.Message}", ex);
        }
    }

    public async Task<bool> ConnectAsync(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ServerIp);

        using var timing = AppLogger.Measure($"RconService.ConnectAsync({profile.ServerIp}:{profile.Port})");
        _currentProfile = profile;
        _hasInitialPlayerSnapshot = false;
        _isInitialConnectPhase = true;
        _recentlyAnnouncedJoins.Clear();
        _recentlyAnnouncedLeaves.Clear();

        lock (_playersLock)
        {
            _lastKnownPlayers.Clear();
        }

        var transaction = SentrySdk.StartTransaction("RCON Connect", "network.rcon.connect");
        using var op = Operation.Begin("Connect RCON to {ServerIp}:{Port} ({Protocol})", profile.ServerIp, profile.Port, profile.Protocol);

        AppLogger.TrackEvent("rcon_connect_attempt", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = profile.Protocol.ToString(),
            ["port"] = profile.Port
        });

        try
        {
            IPAddress? ip = null;
            if (IPAddress.TryParse(profile.ServerIp, out var parsedIp))
            {
                ip = parsedIp;
            }
            else
            {
                var addresses = await Dns.GetHostAddressesAsync(profile.ServerIp, CancellationToken.None).ConfigureAwait(false);
                if (addresses.Length > 0)
                {
                    ip = addresses[0];
                }
            }

            if (ip == null)
            {
                AppLogger.Error($"[RconService] Host resolution failed for address '{profile.ServerIp}'.");
                transaction.Finish(SpanStatus.InvalidArgument);
                return false;
            }

            var credentials = new BattlEyeLoginCredentials(ip, profile.Port, profile.Password);
            _client = new BattlEyeClient(credentials);

            var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _client.BattlEyeConnected += args => connectTcs.TrySetResult(args.ConnectionResult == BattlEyeConnectionResult.Success);

            _client.BattlEyeDisconnected += async args =>
            {
                AppLogger.Warn($"[RconService] Socket disconnect notification: Type={args.DisconnectionType}, Message='{args.Message}'");
                RaiseOutputReceived($"[SYSTEM] Disconnected: {args.Message}");

                StopBackgroundPingMonitor();
                _hasInitialPlayerSnapshot = false;
                _isInitialConnectPhase = false;
                await PlayerDatabaseStorageService.SetAllOfflineAsync(_currentProfile?.Protocol).ConfigureAwait(false);

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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.4));
            cts.Token.Register(() => connectTcs.TrySetResult(false));

            _ = _client.ConnectAsync(cts.Token);

            bool success = await connectTcs.Task.ConfigureAwait(false);
            if (success)
            {
                LastPacketTime = DateTime.UtcNow;
                RaiseOutputReceived($"[SYSTEM] Connected successfully to {profile.ServerIp}:{profile.Port}");
                AppLogger.Info($"[RconService] Live connection verified to {profile.ServerIp}:{profile.Port} via {profile.Protocol}.");

                StartBackgroundPingMonitor(ip);

                op.Complete();
                transaction.Finish(SpanStatus.Ok);

                AppLogger.TrackEvent("rcon_connect_success", new Dictionary<string, object>
                {
                    [ProtocolMetricKey] = profile.Protocol.ToString(),
                    ["ping_ms"] = PingMs
                });
            }
            else
            {
                AppLogger.Warn($"[RconService] Connection attempt timed out or handshake was rejected for {profile.ServerIp}:{profile.Port}");
                transaction.Finish(SpanStatus.DeadlineExceeded);

                AppLogger.TrackEvent("rcon_connect_failed", new Dictionary<string, object>
                {
                    [ProtocolMetricKey] = profile.Protocol.ToString()
                });
            }

            return success;
        }
        catch (SocketException sockEx)
        {
            transaction.Finish(SpanStatus.Unavailable);
            AppLogger.Error($"[RconService] Socket error during connection to {profile.ServerIp}:{profile.Port}: {sockEx.SocketErrorCode}", sockEx);
            return false;
        }
        catch (OperationCanceledException opEx)
        {
            transaction.Finish(SpanStatus.Cancelled);
            AppLogger.Trace($"[RconService] ConnectAsync canceled: {opEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            transaction.Finish(SpanStatus.InternalError);
            AppLogger.Error($"[RconService] Unexpected error connecting to {profile.ServerIp}:{profile.Port}: {ex.Message}", ex);
            return false;
        }
    }

    public async Task DisconnectAsync()
    {
        using var timing = AppLogger.Measure("RconService.DisconnectAsync");
        AppLogger.Info("[RconService] Disconnecting session...");
        StopBackgroundPingMonitor();
        _hasInitialPlayerSnapshot = false;
        _isInitialConnectPhase = false;

        AppLogger.TrackEvent("rcon_disconnect", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString()
        });

        if (_client is { Connected: true } && CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            try
            {
                _client.SendCommand("@logout", log: false);
                await Task.Delay(40, CancellationToken.None).ConfigureAwait(false);
            }
            catch (SocketException sockEx)
            {
                AppLogger.Debug($"[RconService] Socket error during @logout: {sockEx.SocketErrorCode}");
            }
            catch (ObjectDisposedException dispEx)
            {
                AppLogger.Debug($"[RconService] Socket disposed during @logout: {dispEx.Message}");
            }
        }

        try
        {
            await PlayerDatabaseStorageService.SetAllOfflineAsync(CurrentProtocol).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RconService] Failed setting players offline during disconnect.", ex);
        }

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
            await SampleNetworkPingAsync(ip, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                await SafeDelayAsync(2000, token).ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    break;
                }

                await SampleNetworkPingAsync(ip, token).ConfigureAwait(false);
            }
        }, token);
    }

    private static async Task SafeDelayAsync(int millisecondsDelay, CancellationToken cancellationToken)
    {
        if (millisecondsDelay <= 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state => ((TaskCompletionSource?)state)?.TrySetResult(), tcs).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.Delay(millisecondsDelay, CancellationToken.None), tcs.Task).ConfigureAwait(false);
        }
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
            catch (ObjectDisposedException)
            {
                // Disposed cleanly
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
        if (ct.IsCancellationRequested) return;

        try
        {
            var reply = await _icmpPingSender.SendPingAsync(ip, 800).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success && reply.RoundtripTime > 0)
            {
                RecordPingMeasurement((int)reply.RoundtripTime);
                return;
            }
        }
        catch (PingException pingEx)
        {
            AppLogger.Trace($"[RconService] ICMP ping exception: {pingEx.Message}");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Trace($"[RconService] ICMP socket exception: {sockEx.SocketErrorCode}");
        }

        if (_client is { Connected: true, LastPingMs: > 0 })
        {
            RecordPingMeasurement(_client.LastPingMs);
        }
    }

    private void RecordPingMeasurement(int sampleMs)
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
    }

    private void OnBattlEyeMessageReceived(BattlEyeMessageEventArgs args)
    {
        try
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
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error processing incoming BattlEye message: {ex.Message}", ex);
        }
    }

    private void ProcessLiveStreamEvent(string message)
    {
        try
        {
            var disconnMatch = BattlEyeResponseParser.PlayerDisconnectedStreamRegex().Match(message);
            if (disconnMatch.Success && int.TryParse(disconnMatch.Groups[1].Value, out int discId))
            {
                var name = disconnMatch.Groups[2].Value.Trim();
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
                if (!_isInitialConnectPhase)
                {
                    AdminConnectedStream?.Invoke(this, (adminId, endpoint));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[RconService] Error parsing live event stream: {ex.Message}");
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

        var sw = Stopwatch.StartNew();
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#players" : "players";
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(1.0), cancellationToken).ConfigureAwait(false);

            List<PlayerModel> currentPlayers = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParsePlayers(rawResponse)
                : BattlEyeResponseParser.ParsePlayers(rawResponse);

            _ = PlayerDatabaseStorageService.RecordSeenPlayersAsync(currentPlayers, CurrentProtocol);

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

                _isInitialConnectPhase = false;
            }

            sw.Stop();
            AppLogger.Debug($"[RconService:Timing] Player query completed in {sw.ElapsedMilliseconds} ms (Total Players: {currentPlayers.Count}).");
            return currentPlayers;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService] Socket error during player query: {sockEx.SocketErrorCode}", sockEx);
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error querying players: {ex.Message}", ex);
            return [];
        }
    }

    public async Task<List<BanModel>> GetBansAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true }) return [];

        var sw = Stopwatch.StartNew();
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#ban list" : "bans";
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(1.0), cancellationToken).ConfigureAwait(false);

            var bans = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParseBans(rawResponse)
                : BattlEyeResponseParser.ParseBans(rawResponse);

            sw.Stop();
            AppLogger.Debug($"[RconService:Timing] Ban query completed in {sw.ElapsedMilliseconds} ms (Total Bans: {bans.Count}).");
            return bans;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService] Socket error during ban query: {sockEx.SocketErrorCode}", sockEx);
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error querying bans: {ex.Message}", ex);
            return [];
        }
    }

    public async Task<List<AdminModel>> GetAdminsAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not { Connected: true }) return [];

        try
        {
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync("admins", TimeSpan.FromSeconds(0.8), cancellationToken).ConfigureAwait(false);
            return BattlEyeResponseParser.ParseAdmins(rawResponse);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService] Socket error querying admins: {sockEx.SocketErrorCode}", sockEx);
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Failed querying connected admins: {ex.Message}", ex);
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

        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        string cmd = BuildKickCommand(player.Id, cleanReason, CurrentProtocol);

        AppLogger.TrackEvent("moderation_kick", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["player_id"] = player.Id,
            ["has_reason"] = !string.IsNullOrEmpty(cleanReason)
        });

        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        return VerifyModerationSuccess(response, ["kicked!", "Admin Kick", ProcessingCommandToken]);
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

        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        AppLogger.TrackEvent("moderation_ban", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["duration_seconds"] = durationSeconds,
            ["is_permanent"] = durationSeconds <= 0,
            ["ban_ip"] = banIp
        });

        if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            string cmd = BuildReforgerBanCommand(player.Id, durationSeconds, cleanReason);
            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            return VerifyModerationSuccess(response, ["ban created!", "banned!"]);
        }

        string beCmd = BuildBattlEyeBanCommand(player.Id, beMinutes, cleanReason);
        string beResponse = await ExecuteCommandWithAggregateResponseAsync(beCmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        bool banSuccess = VerifyModerationSuccess(beResponse, ["Admin Ban", "kicked by BattlEye"]);

        if (banIp && !string.IsNullOrWhiteSpace(player.Ip) && !player.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(player.Ip, out _))
        {
            string addBanIpCmd = BuildBattlEyeAddBanIpCommand(player.Ip, beMinutes, cleanReason);
            await SendCommandAsync(addBanIpCmd).ConfigureAwait(false);
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync("loadBans").ConfigureAwait(false);
        }

        return banSuccess;
    }

    public async Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        AppLogger.TrackEvent("moderation_offline_ban", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["is_ip"] = isIp,
            ["duration_seconds"] = durationSeconds
        });

        if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            string cmd = string.IsNullOrEmpty(cleanReason)
                ? $"#ban create {identity} {durationSeconds}"
                : $"#ban create {identity} {durationSeconds} {cleanReason}";

            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            return VerifyModerationSuccess(response, ["ban created!", "banned!"]);
        }

        string beAddCmd = string.IsNullOrEmpty(cleanReason)
            ? $"addBan {identity} {beMinutes}"
            : $"addBan {identity} {beMinutes} {cleanReason}";

        await SendCommandAsync(beAddCmd).ConfigureAwait(false);
        await Task.Delay(30, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("loadBans").ConfigureAwait(false);
        return true;
    }

    public async Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ban);

        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban remove {ban.IdentityId}"
            : $"removeBan {ban.BanNumber}";

        AppLogger.TrackEvent("moderation_remove_ban", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["ban_number"] = ban.BanNumber
        });

        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);

        if (CurrentProtocol == RconProtocol.BattlEye)
        {
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync("writeBans").ConfigureAwait(false);
            return true;
        }

        return VerifyModerationSuccess(response, ["ban removed!"]);
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

        AppLogger.TrackEvent("rcon_command_dispatched", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["command_head"] = rawCommand.Split(' ')[0]
        });

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

        cancellationToken.ThrowIfCancellationRequested();
        RaiseOutputReceived($"[RCON OUT] {command}");

        string directResponse = await _client.SendCommandWithResponseAsync(command, TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(directResponse) && !directResponse.StartsWith('\0'))
        {
            AppLogger.Debug($"[RconService:Timing] Direct response received for '{command}' ({directResponse.Length} chars).");
            return directResponse;
        }

        ResetAggregateBuffer();

        bool isBanList = command.StartsWith("#ban list", StringComparison.OrdinalIgnoreCase) || command.Equals("bans", StringComparison.OrdinalIgnoreCase) || command.Equals("ban list", StringComparison.OrdinalIgnoreCase);
        bool isPlayerList = command.StartsWith("#players", StringComparison.OrdinalIgnoreCase) || command.Equals("players", StringComparison.OrdinalIgnoreCase);
        bool isKick = command.StartsWith("#kick", StringComparison.OrdinalIgnoreCase) || command.StartsWith("kick", StringComparison.OrdinalIgnoreCase);
        bool isBanCreate = command.StartsWith("#ban create", StringComparison.OrdinalIgnoreCase) || command.StartsWith("ban ", StringComparison.OrdinalIgnoreCase) || command.StartsWith("addBan", StringComparison.OrdinalIgnoreCase);
        bool isBanRemove = command.StartsWith("#ban remove", StringComparison.OrdinalIgnoreCase) || command.StartsWith("removeBan", StringComparison.OrdinalIgnoreCase);

        var quietThreshold = TimeSpan.FromMilliseconds(20);
        var timeoutLimit = DateTime.UtcNow.Add(maxTimeout);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow < timeoutLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);

            lock (_bufferLock)
            {
                var timeSinceLastChunk = DateTime.UtcNow - _lastMessageChunkUtc;
                var chunksCount = _messageChunksCount;
                var currentText = _aggregatedBuffer.ToString();

                if (CheckUniversalErrorTokens(currentText))
                {
                    break;
                }

                if (CheckCommandSpecificTerminalTokens(isBanList, isKick, isBanCreate, isBanRemove, currentText))
                {
                    break;
                }

                bool hasActualPayload = DeterminePayloadPresence(isPlayerList, isBanList, currentText, chunksCount);

                if (hasActualPayload && timeSinceLastChunk >= quietThreshold)
                {
                    break;
                }

                if (chunksCount == 0 && (DateTime.UtcNow - startTime).TotalMilliseconds >= 150)
                {
                    break;
                }
            }
        }

        lock (_bufferLock)
        {
            return _aggregatedBuffer.ToString();
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

    private static bool CheckUniversalErrorTokens(string currentText)
    {
        return currentText.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
               currentText.Contains("Help for ban command.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CheckCommandSpecificTerminalTokens(bool isBanList, bool isKick, bool isBanCreate, bool isBanRemove, string currentText)
    {
        if (isBanList && (currentText.Contains("Server has no bans to list.", StringComparison.OrdinalIgnoreCase) || currentText.Contains("IP Address] [Minutes left] [Reason]", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (isKick && (currentText.Contains("kicked!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("Admin Kick", StringComparison.OrdinalIgnoreCase) || currentText.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (isBanCreate && (currentText.Contains("banned!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("Admin Ban", StringComparison.OrdinalIgnoreCase) || currentText.Contains("Ban created!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (isBanRemove && (currentText.Contains("Ban removed!", StringComparison.OrdinalIgnoreCase) || currentText.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool DeterminePayloadPresence(bool isPlayerList, bool isBanList, string currentText, int chunksCount)
    {
        if (isPlayerList)
        {
            return currentText.Contains("Players on server:", StringComparison.OrdinalIgnoreCase) || currentText.Contains("players in total", StringComparison.OrdinalIgnoreCase);
        }

        if (isBanList)
        {
            return currentText.Contains("Total bans:", StringComparison.OrdinalIgnoreCase) || currentText.Contains("IP Bans:", StringComparison.OrdinalIgnoreCase);
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
    }
}