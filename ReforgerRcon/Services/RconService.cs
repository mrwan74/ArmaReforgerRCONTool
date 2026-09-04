using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    private const string TokenBanned = "banned!";
    private const string TokenBanCreated = "ban created!";
    private const string TokenAdminBan = "Admin Ban";
    private const string TokenAdminKick = "Admin Kick";
    private const string TokenKicked = "kicked!";
    private const string TokenBanRemoved = "ban removed!";

    private BattlEyeClient? _client;
    private ServerProfile? _currentProfile;
    private string _lastConnectionError = string.Empty;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingCommands = new();
    private readonly ConcurrentDictionary<string, long> _recentlyAnnouncedJoins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _recentlyAnnouncedLeaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _aggregatedBuffer = new();
    private readonly SemaphoreSlim _bufferSemaphore = new(1, 1);
    private readonly SemaphoreSlim _commandExecutionLock = new(1, 1);
    private DateTime _lastMessageChunkUtc = DateTime.UtcNow;
    private int _messageChunksCount;
    private bool _isDisposed;
    private volatile bool _hasInitialPlayerSnapshot;
    private volatile bool _isInitialConnectPhase;
    private int _protocolMismatchFired;

    private readonly SemaphoreSlim _rconStateSemaphore = new(1, 1);
    private readonly SemaphoreSlim _playersSemaphore = new(1, 1);
    private readonly List<PlayerModel> _lastKnownPlayers = [];

    private readonly System.Net.NetworkInformation.Ping _icmpPingSender = new();
    private readonly Queue<int> _pingSamples = new();
    private readonly Lock _pingLock = new();
    private CancellationTokenSource? _pingLoopCts;
    private int _smoothedPingMs;

    public RconProtocol CurrentProtocol
    {
        get
        {
            var profile = _currentProfile;
            return profile?.Protocol ?? RconProtocol.ReforgerBuiltIn;
        }
    }

    public bool IsConnected => _client is { Connected: true };
    public int PingMs => _smoothedPingMs > 0 ? _smoothedPingMs : (_client?.LastPingMs ?? 0);
    public DateTime LastPacketTime { get; private set; } = DateTime.UtcNow;
    public string LastConnectionError => !string.IsNullOrWhiteSpace(_client?.LastErrorDiagnostic) ? _client.LastErrorDiagnostic : _lastConnectionError;
    public RconProtocol? DetectedProtocolMismatch { get; private set; }

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<PlayerModel>? PlayerJoined;
    public event EventHandler<PlayerModel>? PlayerLeft;
    public event EventHandler<(string Name, int Id, string Reason)>? PlayerKickedStream;
    public event EventHandler<(string Name, int Id, string Guid, string Reason)>? PlayerBannedStream;
    public event EventHandler<(int AdminId, string Endpoint)>? AdminConnectedStream;
    public event EventHandler<string>? ConnectionLost;
    public event EventHandler<RconProtocol>? ProtocolMismatchDetected;

    private void RaiseOutputReceived(string message)
    {
        try
        {
            AppLogger.Trace($"[RconService:OutputReceived] Length: {message.Length} chars | Content: {AppLogger.SanitizeSensitiveData(message)}");
            OutputReceived?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error in OutputReceived handler: {ex.Message}", ex);
        }
    }

    private void CheckProtocolMismatch(string message)
    {
        if (_protocolMismatchFired != 0) return;

        var detected = ReforgerResponseParser.DetectProtocol(message);
        if (detected.HasValue && detected.Value != CurrentProtocol && Interlocked.CompareExchange(ref _protocolMismatchFired, 1, 0) == 0)
        {
            DetectedProtocolMismatch = detected.Value;
            AppLogger.Warn($"[RconService:ProtocolMismatch] Detected {detected.Value} signature while connected in {CurrentProtocol} mode! Payload: '{AppLogger.SanitizeSensitiveData(message)}'");
            ProtocolMismatchDetected?.Invoke(this, detected.Value);
        }
    }

    private void RaisePlayerJoined(PlayerModel player)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            if (!_hasInitialPlayerSnapshot || _isInitialConnectPhase)
            {
                AppLogger.Trace($"[RconService:JoinLifecycle] Suppressed join notification for '{player.Name}' (ID: #{player.Id}) during initial sync.");
                return;
            }

            var key = $"{player.Id}_{player.Name.Trim()}";
            var now = Stopwatch.GetTimestamp();

            if (_recentlyAnnouncedJoins.TryGetValue(key, out var lastAnnounced))
            {
                var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
                if (elapsedSec < 6.0)
                {
                    AppLogger.Trace($"[RconService:JoinLifecycle] Debounced duplicate join alert for '{player.Name}' (Elapsed: {elapsedSec:F2}s < 6.0s threshold).");
                    return;
                }
            }

            _recentlyAnnouncedJoins[key] = now;
            _recentlyAnnouncedLeaves.TryRemove(key, out _);

            var dispatchDuration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[RconService:JoinLifecycle] Dispatched PlayerJoined in {dispatchDuration:F2}ms for '{player.Name}' (ID: #{player.Id}, GUID: '{player.Guid}', Endpoint: '{player.Ip}:{player.Port}', Location: '{player.DisplayLocation}', Watchlisted: {player.IsWatchlisted}).");
            PlayerJoined?.Invoke(this, player);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error dispatching PlayerJoined for '{player.Name}': {ex.Message}", ex);
        }
    }

    private void RaisePlayerLeft(PlayerModel player)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            if (!_hasInitialPlayerSnapshot || _isInitialConnectPhase)
            {
                AppLogger.Trace($"[RconService:LeaveLifecycle] Suppressed leave notification for '{player.Name}' (ID: #{player.Id}) during initial snapshot.");
                return;
            }

            var key = $"{player.Id}_{player.Name.Trim()}";
            var now = Stopwatch.GetTimestamp();

            if (_recentlyAnnouncedLeaves.TryGetValue(key, out var lastAnnounced))
            {
                var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
                if (elapsedSec < 6.0)
                {
                    AppLogger.Trace($"[RconService:LeaveLifecycle] Debounced duplicate leave alert for '{player.Name}' (Elapsed: {elapsedSec:F2}s < 6.0s threshold).");
                    return;
                }
            }

            _recentlyAnnouncedLeaves[key] = now;
            _recentlyAnnouncedJoins.TryRemove(key, out _);

            var dispatchDuration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[RconService:LeaveLifecycle] Dispatched PlayerLeft in {dispatchDuration:F2}ms for '{player.Name}' (ID: #{player.Id}, GUID: '{player.Guid}').");
            PlayerLeft?.Invoke(this, player);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error dispatching PlayerLeft for '{player.Name}': {ex.Message}", ex);
        }
    }

    private void RaiseConnectionLost(string reason)
    {
        try
        {
            AppLogger.Warn($"[RconService:State] Connection lost dispatched. Reason: '{reason}', LastPacketTime: {LastPacketTime:O}");
            ConnectionLost?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService] Error in ConnectionLost handler: {ex.Message}", ex);
        }
    }

    public async Task<bool> ConnectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ServerIp);

        var totalStartTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"RconService.ConnectAsync({profile.ServerIp}:{profile.Port}, {profile.Protocol})");

        AppLogger.Debug($"[RconService:Connect] Acquiring state lock for {profile.ServerIp}:{profile.Port}...");
        await _rconStateSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _currentProfile = profile;
            _lastConnectionError = string.Empty;
            _hasInitialPlayerSnapshot = false;
            _isInitialConnectPhase = true;
            DetectedProtocolMismatch = null;
            _protocolMismatchFired = 0;
            _recentlyAnnouncedJoins.Clear();
            _recentlyAnnouncedLeaves.Clear();

            await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _lastKnownPlayers.Clear();
            }
            finally
            {
                _playersSemaphore.Release();
            }

            var transaction = SentrySdk.StartTransaction("RCON Connect", "network.rcon.connect");
            using var op = Operation.Begin("Connect RCON to {ServerIp}:{Port} ({Protocol})", profile.ServerIp, profile.Port, profile.Protocol);

            AppLogger.TrackEvent("rcon_connect_attempt", new Dictionary<string, object>
            {
                [ProtocolMetricKey] = profile.Protocol.ToString(),
                ["host"] = profile.ServerIp,
                ["port"] = profile.Port
            });

            IPAddress? ip = null;
            if (IPAddress.TryParse(profile.ServerIp, out var parsedIp))
            {
                ip = parsedIp;
                AppLogger.Debug($"[RconService:Connect] Host parsed as direct IP: {ip}");
            }
            else
            {
                var dnsStart = Stopwatch.GetTimestamp();
                AppLogger.Debug($"[RconService:Connect] Resolving DNS for '{profile.ServerIp}'...");
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(profile.ServerIp, cancellationToken).ConfigureAwait(false);
                    var dnsElapsedMs = Stopwatch.GetElapsedTime(dnsStart).TotalMilliseconds;
                    if (addresses.Length > 0)
                    {
                        ip = addresses[0];
                        AppLogger.Info($"[RconService:Connect] DNS resolved '{profile.ServerIp}' -> {ip} in {dnsElapsedMs:F2}ms (Candidates={addresses.Length}).");
                    }
                    else
                    {
                        _lastConnectionError = $"DNS resolution returned no IP addresses for host '{profile.ServerIp}'.";
                    }
                }
                catch (Exception dnsEx)
                {
                    _lastConnectionError = $"DNS resolution failed for '{profile.ServerIp}': {dnsEx.Message}";
                    AppLogger.Error($"[RconService:Connect] {_lastConnectionError}", dnsEx);
                }
            }

            if (ip == null)
            {
                if (string.IsNullOrWhiteSpace(_lastConnectionError))
                {
                    _lastConnectionError = $"Unable to resolve host '{profile.ServerIp}' to a valid IP address.";
                }

                AppLogger.Error($"[RconService:Connect] {_lastConnectionError} Aborting.");
                transaction.Finish(SpanStatus.InvalidArgument);
                return false;
            }

            var rawPassword = profile.Password ?? string.Empty;
            AppLogger.Debug($"[RconService:Connect] Instantiating BattlEyeClient for {ip}:{profile.Port} (PasswordLength: {rawPassword.Length}).");
            var credentials = new BattlEyeLoginCredentials(ip, profile.Port, rawPassword);
            _client = new BattlEyeClient(credentials);

            var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _client.BattlEyeConnected += args =>
            {
                AppLogger.Info($"[RconService:Callback] BattlEyeConnected: Result={args.ConnectionResult}, Message='{args.Message}'");
                connectTcs.TrySetResult(args.ConnectionResult == BattlEyeConnectionResult.Success);
            };

            _client.BattlEyeDisconnected += async args =>
            {
                if (args.DisconnectionType == BattlEyeDisconnectionType.Manual)
                {
                    AppLogger.Info($"[RconService:Callback] BattlEyeDisconnected: Type=Manual, Message='{args.Message}'");
                }
                else
                {
                    AppLogger.Warn($"[RconService:Callback] BattlEyeDisconnected: Type={args.DisconnectionType}, Message='{args.Message}'");
                }
                RaiseOutputReceived($"[SYSTEM] Disconnected: {args.Message}");

                StopBackgroundPingMonitor();
                _hasInitialPlayerSnapshot = false;
                _isInitialConnectPhase = false;

                AppLogger.Debug($"[RconService:State] Setting {_currentProfile?.Protocol} players offline in SQLite...");
                await PlayerDatabaseStorageService.SetAllOfflineAsync(_currentProfile?.Protocol).ConfigureAwait(false);

                await _playersSemaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    _lastKnownPlayers.Clear();
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                _recentlyAnnouncedJoins.Clear();
                _recentlyAnnouncedLeaves.Clear();

                if (args.DisconnectionType != BattlEyeDisconnectionType.Manual)
                {
                    RaiseConnectionLost(args.Message);
                }
            };

            _client.BattlEyeMessageReceived += OnBattlEyeMessageReceived;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5.0));
            cts.Token.Register(() =>
            {
                if (!connectTcs.Task.IsCompleted)
                {
                    AppLogger.Warn($"[RconService:Connect] Handshake timeout reached for {ip}:{profile.Port}.");
                    connectTcs.TrySetResult(false);
                }
            });

            _ = _client.ConnectAsync(cts.Token);

            bool success = await connectTcs.Task.ConfigureAwait(false);
            var totalElapsedMs = Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds;

            if (success)
            {
                LastPacketTime = DateTime.UtcNow;
                RaiseOutputReceived($"[SYSTEM] Connected successfully to {profile.ServerIp}:{profile.Port}");
                AppLogger.Info($"[RconService:Connect] Session active: {profile.ServerIp}:{profile.Port} via {profile.Protocol} in {totalElapsedMs:F2}ms (Ping: {PingMs}ms).");

                StartBackgroundPingMonitor(ip);

                op.Complete();
                transaction.Finish(SpanStatus.Ok);

                AppLogger.TrackEvent("rcon_connect_success", new Dictionary<string, object>
                {
                    [ProtocolMetricKey] = profile.Protocol.ToString(),
                    ["ping_ms"] = PingMs,
                    ["duration_ms"] = totalElapsedMs
                });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_lastConnectionError))
                {
                    _lastConnectionError = _client.LastErrorDiagnostic;
                }

                AppLogger.Warn($"[RconService:Connect] Connection failed for {profile.ServerIp}:{profile.Port} after {totalElapsedMs:F2}ms. Reason: {LastConnectionError}");
                transaction.Finish(SpanStatus.DeadlineExceeded);

                AppLogger.TrackEvent("rcon_connect_failed", new Dictionary<string, object>
                {
                    [ProtocolMetricKey] = profile.Protocol.ToString(),
                    ["duration_ms"] = totalElapsedMs,
                    ["reason"] = LastConnectionError
                });
            }

            return success;
        }
        catch (SocketException sockEx)
        {
            _lastConnectionError = $"Socket error ({sockEx.SocketErrorCode}): {sockEx.Message}";
            AppLogger.Error($"[RconService:Connect] SocketException on connect: {sockEx.SocketErrorCode} ({sockEx.NativeErrorCode}): {sockEx.Message}", sockEx);
            return false;
        }
        catch (OperationCanceledException opEx)
        {
            _lastConnectionError = "Connection attempt timed out or was canceled.";
            AppLogger.Trace($"[RconService:Connect] Connect cancelled: {opEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            _lastConnectionError = $"Connection error: {ex.Message}";
            AppLogger.Error($"[RconService:Connect] Unexpected error during connect: {ex.Message}", ex);
            return false;
        }
        finally
        {
            _rconStateSemaphore.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("RconService.DisconnectAsync");
        AppLogger.Info("[RconService:Disconnect] Commencing graceful disconnect sequence...");

        await _rconStateSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                    AppLogger.Trace("[RconService:Disconnect] Sending '@logout' command...");
                    _client.SendCommand("@logout", log: false);
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException sockEx)
                {
                    AppLogger.Debug($"[RconService:Disconnect] SocketException on @logout: {sockEx.SocketErrorCode}");
                }
                catch (ObjectDisposedException dispEx)
                {
                    AppLogger.Debug($"[RconService:Disconnect] Socket already disposed on @logout: {dispEx.Message}");
                }
            }

            try
            {
                AppLogger.Debug($"[RconService:Disconnect] Resetting {CurrentProtocol} database player status in SQLite...");
                await PlayerDatabaseStorageService.SetAllOfflineAsync(CurrentProtocol).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("[RconService:Disconnect] Failed setting players offline in SQLite: " + ex.Message, ex);
            }

            await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _lastKnownPlayers.Clear();
            }
            finally
            {
                _playersSemaphore.Release();
            }

            _recentlyAnnouncedJoins.Clear();
            _recentlyAnnouncedLeaves.Clear();

            _client?.Dispose();
            _client = null;

            RaiseOutputReceived("[SYSTEM] Disconnected from server.");
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[RconService:Disconnect] Disconnect sequence complete in {elapsedMs:F2}ms.");
        }
        finally
        {
            _rconStateSemaphore.Release();
        }
    }

    private void StartBackgroundPingMonitor(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        StopBackgroundPingMonitor();

        _pingLoopCts = new CancellationTokenSource();
        var token = _pingLoopCts.Token;

        AppLogger.Debug($"[RconService:Ping] Initializing background ICMP ping worker for: {ip}...");

        _ = Task.Run(async () =>
        {
            await SampleNetworkPingAsync(ip, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                var delayTask = Task.Delay(2000, CancellationToken.None);
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using (token.Register(static s => ((TaskCompletionSource?)s)?.TrySetResult(), tcs).ConfigureAwait(false))
                {
                    await Task.WhenAny(delayTask, tcs.Task).ConfigureAwait(false);
                }

                if (token.IsCancellationRequested)
                {
                    break;
                }

                await SampleNetworkPingAsync(ip, token).ConfigureAwait(false);
            }

            AppLogger.Trace($"[RconService:Ping] ICMP ping worker finished for {ip}.");
        }, CancellationToken.None);
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
            catch (ObjectDisposedException ex)
            {
                AppLogger.Trace($"[RconService:Ping] Ping CTS already disposed: {ex.Message}");
            }
            _pingLoopCts = null;
        }

        lock (_pingLock)
        {
            _pingSamples.Clear();
            _smoothedPingMs = 0;
        }
        AppLogger.Debug("[RconService:Ping] Background ping monitor stopped.");
    }

    private async Task SampleNetworkPingAsync(IPAddress ip, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var reply = await _icmpPingSender.SendPingAsync(ip, TimeSpan.FromMilliseconds(900), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            if (reply.Status == IPStatus.Success && reply.RoundtripTime > 0)
            {
                RecordPingMeasurement((int)reply.RoundtripTime);
                AppLogger.Trace($"[RconService:Ping] ICMP Success: RTT={reply.RoundtripTime}ms, Smoothed={PingMs}ms in {elapsedMs:F2}ms.");
                return;
            }
            AppLogger.Trace($"[RconService:Ping] ICMP non-success status: {reply.Status} in {elapsedMs:F2}ms.");
        }
        catch (PingException pingEx)
        {
            AppLogger.Trace($"[RconService:Ping] PingException: {pingEx.Message}");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Trace($"[RconService:Ping] SocketException: {sockEx.SocketErrorCode}");
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var client = _client;
        if (client is { Connected: true, LastPingMs: > 0 })
        {
            RecordPingMeasurement(client.LastPingMs);
            AppLogger.Trace($"[RconService:Ping] Fallback to UDP protocol RTT: {client.LastPingMs}ms (Smoothed={PingMs}ms).");
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
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            LastPacketTime = DateTime.UtcNow;
            var message = args.Message;

            AppendToBuffer(message);
            CheckProtocolMismatch(message);

            if (args.Id != 256 && _pendingCommands.TryRemove(args.Id, out var tcs))
            {
                AppLogger.Debug($"[RconService:Message] Fulfilling pending command TCS for Packet ID #{args.Id} ({message.Length} chars).");
                tcs.TrySetResult(message);
            }

            ProcessLiveStreamEvent(message);
            RaiseOutputReceived($"[RCON IN] {message}");

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[RconService:Message] Handled incoming message ID #{args.Id} in {elapsedMs:F2}ms ({message.Length} chars).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:Message] Exception processing message ID #{args.Id}: {ex.Message}", ex);
        }
    }

    private void AppendToBuffer(string chunk)
    {
        _bufferSemaphore.Wait(CancellationToken.None);
        try
        {
            _aggregatedBuffer.AppendLine(chunk);
            _lastMessageChunkUtc = DateTime.UtcNow;
            _messageChunksCount++;
            AppLogger.Trace($"[RconService:Buffer] Appended chunk #{_messageChunksCount} ({chunk.Length} chars). Total length: {_aggregatedBuffer.Length} chars.");
        }
        finally
        {
            _bufferSemaphore.Release();
        }
    }

    private void ProcessLiveStreamEvent(string message)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn &&
                (message.Contains("Players on server:", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains(" ; ")))
            {
                var liveParsedPlayers = ReforgerResponseParser.ParsePlayers(message);
                if (liveParsedPlayers.Count > 0)
                {
                    AppLogger.Info($"[RconService:StreamEvent] Parsed {liveParsedPlayers.Count} Reforger player(s) directly from stream message ({message.Length} chars).");
                    _playersSemaphore.Wait(CancellationToken.None);
                    try
                    {
                        foreach (var p in liveParsedPlayers)
                        {
                            _lastKnownPlayers.RemoveAll(old => IsSamePlayer(old, p));
                            _lastKnownPlayers.Add(p);
                        }
                    }
                    finally
                    {
                        _playersSemaphore.Release();
                    }

                    _ = PlayerDatabaseStorageService.RecordSeenPlayersAsync(liveParsedPlayers, CurrentProtocol);
                    return;
                }
                else if (message.Contains("Players on server:", StringComparison.OrdinalIgnoreCase))
                {
                    _playersSemaphore.Wait(CancellationToken.None);
                    try
                    {
                        _lastKnownPlayers.Clear();
                    }
                    finally
                    {
                        _playersSemaphore.Release();
                    }
                    _ = PlayerDatabaseStorageService.RecordSeenPlayersAsync([], CurrentProtocol);
                }
            }

            var disconnMatch = BattlEyeResponseParser.PlayerDisconnectedStreamRegex().Match(message);
            if (disconnMatch.Success && int.TryParse(disconnMatch.Groups[1].Value, out int discId))
            {
                var name = disconnMatch.Groups[2].Value.Trim();
                PlayerModel matched;
                _playersSemaphore.Wait(CancellationToken.None);
                try
                {
                    matched = _lastKnownPlayers.FirstOrDefault(p =>
                        p.Id == discId ||
                        (!string.IsNullOrEmpty(name) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        ?? new PlayerModel { Id = discId, Name = name, Ping = 0 };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = CurrentProtocol == RconProtocol.BattlEye ? matched.BattlEyeGuid : matched.ReforgerUid;
                if (string.IsNullOrWhiteSpace(identifier)) identifier = matched.Uid;
                _ = PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol);

                AppLogger.Info($"[RconService:StreamEvent] Player Disconnected stream matched: '{name}' (ID: #{discId}). Remaining online: {_lastKnownPlayers.Count}");
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

                _playersSemaphore.Wait(CancellationToken.None);
                try
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
                            BattlEyeGuid = guid,
                            Ping = 0
                        };
                        _lastKnownPlayers.Add(matched);
                        isNew = true;
                    }
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                AppLogger.Info($"[RconService:StreamEvent] Player GUID stream matched: '{name}' (ID: #{guidPlayerId}, GUID: '{guid}', IsNew: {isNew}). Total tracking: {_lastKnownPlayers.Count}");
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
                var reason = banMatch.Groups[4].Success ? banMatch.Groups[4].Value.Trim() : TokenAdminBan;

                PlayerModel matched;
                _playersSemaphore.Wait(CancellationToken.None);
                try
                {
                    matched = _lastKnownPlayers.FirstOrDefault(p =>
                        p.Id == banId ||
                        (!string.IsNullOrEmpty(guid) && p.Guid == guid) ||
                        (!string.IsNullOrEmpty(name) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        ?? new PlayerModel { Id = banId, Name = name, Guid = guid, BattlEyeGuid = guid, Ping = 0 };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = CurrentProtocol == RconProtocol.BattlEye ? matched.BattlEyeGuid : matched.ReforgerUid;
                if (string.IsNullOrWhiteSpace(identifier)) identifier = matched.Uid;
                _ = PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol);

                AppLogger.Warn($"[RconService:StreamEvent] Player Banned stream matched: '{name}' (ID: #{banId}, GUID: '{guid}', Reason: '{reason}').");
                RaisePlayerLeft(matched);
                PlayerBannedStream?.Invoke(this, (name, banId, guid, reason));
                return;
            }

            var kickMatch = BattlEyeResponseParser.PlayerKickedStreamRegex().Match(message);
            if (kickMatch.Success && int.TryParse(kickMatch.Groups[1].Value, out int kickId))
            {
                var name = kickMatch.Groups[2].Value.Trim();
                var guid = kickMatch.Groups[3].Value.Trim();
                var reason = kickMatch.Groups[4].Success ? kickMatch.Groups[4].Value.Trim() : TokenAdminKick;

                PlayerModel matched;
                _playersSemaphore.Wait(CancellationToken.None);
                try
                {
                    matched = _lastKnownPlayers.FirstOrDefault(p =>
                        p.Id == kickId ||
                        (!string.IsNullOrEmpty(guid) && p.Guid == guid) ||
                        (!string.IsNullOrEmpty(name) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                        ?? new PlayerModel { Id = kickId, Name = name, Guid = guid, BattlEyeGuid = guid, Ping = 0 };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = CurrentProtocol == RconProtocol.BattlEye ? matched.BattlEyeGuid : matched.ReforgerUid;
                if (string.IsNullOrWhiteSpace(identifier)) identifier = matched.Uid;
                _ = PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol);

                AppLogger.Warn($"[RconService:StreamEvent] Player Kicked stream matched: '{name}' (ID: #{kickId}, GUID: '{guid}', Reason: '{reason}').");
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
                    Ping = 0,
                    Country = new CountryInfo { Code = geo.CountryCode, Name = geo.CountryName },
                    LocationCity = geo.CityName,
                    LocationState = geo.SubdivisionName,
                    DisplayLocation = geo.NaturalLocation,
                    TimeZone = geo.TimeZone
                };

                _playersSemaphore.Wait(CancellationToken.None);
                try
                {
                    _lastKnownPlayers.RemoveAll(x => IsSamePlayer(x, newPlayer));
                    _lastKnownPlayers.Add(newPlayer);
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                AppLogger.Info($"[RconService:StreamEvent] Player Connected stream matched: '{name}' (ID: #{connId}, Endpoint: {ip}:{port}, Location: '{geo.NaturalLocation}').");
                _ = PlayerDatabaseStorageService.RecordSeenPlayersAsync([newPlayer], CurrentProtocol);
                RaisePlayerJoined(newPlayer);
                return;
            }

            var adminMatch = BattlEyeResponseParser.AdminConnectedStreamRegex().Match(message);
            if (adminMatch.Success && int.TryParse(adminMatch.Groups[1].Value, out int adminId))
            {
                var endpoint = adminMatch.Groups[2].Value.Trim();
                AppLogger.Info($"[RconService:StreamEvent] RCON Admin logged in: Admin #{adminId} from {endpoint}");
                if (!_isInitialConnectPhase)
                {
                    AdminConnectedStream?.Invoke(this, (adminId, endpoint));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[RconService:StreamEvent] Error parsing live event stream line: {ex.Message}");
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            if (elapsedMs > 5.0)
            {
                AppLogger.Trace($"[RconService:StreamPerf] Stream regex evaluation completed in {elapsedMs:F2}ms.");
            }
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
        var client = _client;
        if (client is not { Connected: true })
        {
            AppLogger.Warn("[RconService:GetPlayers] Socket disconnected. Returning empty list.");
            return [];
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"RconService.GetPlayersAsync({CurrentProtocol})");

        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#players" : "players";
            AppLogger.Debug($"[RconService:GetPlayers] Dispatching query command '{command}' ({CurrentProtocol})...");

            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(3.0), cancellationToken).ConfigureAwait(false);

            var parseStart = Stopwatch.GetTimestamp();
            List<PlayerModel> currentPlayers = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParsePlayers(rawResponse)
                : BattlEyeResponseParser.ParsePlayers(rawResponse);
            var parseElapsed = Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;

            _ = Task.Run(() => PlayerDatabaseStorageService.RecordSeenPlayersAsync(currentPlayers, CurrentProtocol), CancellationToken.None);

            await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
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
                    AppLogger.Info($"[RconService:GetPlayers] Snapshot established ({currentPlayers.Count} players active).");
                }
                else
                {
                    var joined = currentPlayers.Where(p => !_lastKnownPlayers.Any(old => IsSamePlayer(p, old))).ToList();
                    var left = _lastKnownPlayers.Where(p => !currentPlayers.Any(curr => IsSamePlayer(p, curr))).ToList();

                    if (joined.Count > 0)
                    {
                        AppLogger.Info($"[RconService:GetPlayers] Delta: {joined.Count} player join(s) detected.");
                        foreach (var p in joined) RaisePlayerJoined(p);
                    }

                    if (left.Count > 0)
                    {
                        AppLogger.Info($"[RconService:GetPlayers] Delta: {left.Count} player leave(s) detected.");
                        foreach (var p in left) RaisePlayerLeft(p);
                    }

                    _lastKnownPlayers.Clear();
                    _lastKnownPlayers.AddRange(currentPlayers);
                }

                _isInitialConnectPhase = false;
            }
            finally
            {
                _playersSemaphore.Release();
            }

            var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[RconService:GetPlayers] GetPlayersAsync complete in {totalElapsed:F2}ms (Parse={parseElapsed:F2}ms, Online={currentPlayers.Count}).");
            return currentPlayers;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Trace("[RconService:GetPlayers] Operation canceled.");
            return [];
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService:GetPlayers] SocketException during player query: {sockEx.SocketErrorCode}", sockEx);
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:GetPlayers] Unexpected error querying player list: {ex.Message}", ex);
            return [];
        }
    }

    public async Task<List<BanModel>> GetBansAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is not { Connected: true })
        {
            AppLogger.Warn("[RconService:GetBans] Socket disconnected. Returning empty ban list.");
            return [];
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"RconService.GetBansAsync({CurrentProtocol})");

        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#ban list" : "bans";
            AppLogger.Debug($"[RconService:GetBans] Dispatching ban query '{command}' ({CurrentProtocol})...");

            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);

            var parseStart = Stopwatch.GetTimestamp();
            var bans = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParseBans(rawResponse)
                : BattlEyeResponseParser.ParseBans(rawResponse);
            var parseElapsed = Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;

            var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[RconService:GetBans] GetBansAsync complete in {totalElapsed:F2}ms (Parse={parseElapsed:F2}ms, Count={bans.Count}).");
            return bans;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Trace("[RconService:GetBans] Ban query canceled.");
            return [];
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService:GetBans] SocketException during ban query: {sockEx.SocketErrorCode}", sockEx);
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:GetBans] Unexpected error querying ban list: {ex.Message}", ex);
            return [];
        }
    }

    public async Task<List<AdminModel>> GetAdminsAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is not { Connected: true })
        {
            AppLogger.Warn("[RconService:GetAdmins] Socket disconnected. Returning empty admin list.");
            return [];
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("RconService.GetAdminsAsync");

        try
        {
            AppLogger.Debug("[RconService:GetAdmins] Dispatching BattlEye 'admins' query...");
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync("admins", TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            var admins = BattlEyeResponseParser.ParseAdmins(rawResponse);
            var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[RconService:GetAdmins] GetAdminsAsync complete in {totalElapsed:F2}ms (Count={admins.Count}).");
            return admins;
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService:GetAdmins] Socket error querying admins: {sockEx.SocketErrorCode}", sockEx);
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:GetAdmins] Failed querying connected admins: {ex.Message}", ex);
            return [];
        }
    }

    public Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default)
    {
        AppLogger.Debug($"[RconService:Database] Querying SQLite historical records for protocol {CurrentProtocol}...");
        return PlayerDatabaseStorageService.GetAllAsync(CurrentProtocol);
    }

    private static string BuildKickCommand(int playerId, string reason, RconProtocol protocol)
    {
        var prefix = protocol == RconProtocol.ReforgerBuiltIn ? "#kick" : "kick";
        return string.IsNullOrEmpty(reason) ? $"{prefix} {playerId}" : $"{prefix} {playerId} {reason}";
    }

    public async Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var startTimestamp = Stopwatch.GetTimestamp();
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        string cmd = BuildKickCommand(player.Id, cleanReason, CurrentProtocol);

        AppLogger.Info($"[RconService:Moderation] KickPlayerAsync starting: '{player.Name}' (ID: #{player.Id}, Protocol: {CurrentProtocol}, Reason: '{cleanReason}')...");

        AppLogger.TrackEvent("moderation_kick", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["player_id"] = player.Id,
            ["has_reason"] = !string.IsNullOrEmpty(cleanReason)
        });

        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        bool success = VerifyModerationSuccess(response, [TokenKicked, TokenAdminKick, ProcessingCommandToken]);

        if (success)
        {
            await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, player));
            }
            finally
            {
                _playersSemaphore.Release();
            }

            var identifier = CurrentProtocol == RconProtocol.BattlEye ? player.BattlEyeGuid : player.ReforgerUid;
            if (string.IsNullOrWhiteSpace(identifier)) identifier = player.Uid;
            _ = PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[RconService:Moderation] KickPlayerAsync complete in {elapsedMs:F2}ms. Success={success}, Response='{AppLogger.SanitizeSensitiveData(response)}'");
        return success;
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

        var startTimestamp = Stopwatch.GetTimestamp();
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        AppLogger.Info($"[RconService:Moderation] BanPlayerWithOptionalIpAsync: Name='{player.Name}', ID=#{player.Id}, Duration={durationSeconds}s ({beMinutes}m), BanIP={banIp}, IP='{player.Ip}', Protocol={CurrentProtocol}...");

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
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            bool success = VerifyModerationSuccess(response, [TokenBanCreated, TokenBanned, ProcessingCommandToken]);

            if (success)
            {
                await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, player));
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = !string.IsNullOrWhiteSpace(player.ReforgerUid) ? player.ReforgerUid : player.Uid;
                _ = PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol);
            }

            AppLogger.Info($"[RconService:Moderation] Reforger ban execution complete in {elapsedMs:F2}ms: Success={success}, Response='{AppLogger.SanitizeSensitiveData(response)}'");
            return success;
        }

        string beCmd = BuildBattlEyeBanCommand(player.Id, beMinutes, cleanReason);
        string beResponse = await ExecuteCommandWithAggregateResponseAsync(beCmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
        bool banSuccess = VerifyModerationSuccess(beResponse, [TokenAdminBan, "kicked by BattlEye", TokenBanned]);

        if (banSuccess)
        {
            await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, player));
            }
            finally
            {
                _playersSemaphore.Release();
            }

            var identifier = !string.IsNullOrWhiteSpace(player.BattlEyeGuid) ? player.BattlEyeGuid : player.Guid;
            if (string.IsNullOrWhiteSpace(identifier)) identifier = player.Uid;
            _ = PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol);
        }

        if (banIp && !string.IsNullOrWhiteSpace(player.Ip) && !player.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(player.Ip, out _))
        {
            string addBanIpCmd = BuildBattlEyeAddBanIpCommand(player.Ip, beMinutes, cleanReason);
            AppLogger.Info($"[RconService:Moderation] Executing IP ban for '{player.Name}' at {player.Ip}: '{addBanIpCmd}'...");
            await SendCommandAsync(addBanIpCmd, cancellationToken).ConfigureAwait(false);
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync("loadBans", cancellationToken).ConfigureAwait(false);
        }

        var totalDuration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[RconService:Moderation] BattlEye ban sequence complete in {totalDuration:F2}ms for '{player.Name}'. PrimarySuccess={banSuccess}");
        return banSuccess;
    }

    public async Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var startTimestamp = Stopwatch.GetTimestamp();
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        AppLogger.Info($"[RconService:Moderation] OfflineBanAsync: Identity='{identity}' (Duration={durationSeconds}s, IsIP={isIp}, Protocol={CurrentProtocol}, Reason='{cleanReason}')...");

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
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            bool success = VerifyModerationSuccess(response, [TokenBanCreated, TokenBanned, ProcessingCommandToken]);
            AppLogger.Info($"[RconService:Moderation] Reforger offline ban complete in {elapsedMs:F2}ms: Success={success}");
            return success;
        }

        string beAddCmd = string.IsNullOrEmpty(cleanReason)
            ? $"addBan {identity} {beMinutes}"
            : $"addBan {identity} {beMinutes} {cleanReason}";

        await SendCommandAsync(beAddCmd, cancellationToken).ConfigureAwait(false);
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("loadBans", cancellationToken).ConfigureAwait(false);
        var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[RconService:Moderation] BattlEye offline ban complete in {totalElapsed:F2}ms for '{identity}'.");
        return true;
    }

    public async Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ban);

        var startTimestamp = Stopwatch.GetTimestamp();
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban remove {ban.IdentityId}"
            : $"removeBan {ban.BanNumber}";

        AppLogger.Info($"[RconService:Moderation] RemoveBanAsync starting for Ban #{ban.BanNumber} (Identity: '{ban.IdentityId}') via '{cmd}'...");

        AppLogger.TrackEvent("moderation_remove_ban", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["ban_number"] = ban.BanNumber
        });

        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);

        if (CurrentProtocol == RconProtocol.BattlEye)
        {
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync("writeBans", cancellationToken).ConfigureAwait(false);
            var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[RconService:Moderation] BattlEye removeBan & writeBans complete in {totalElapsed:F2}ms for Ban #{ban.BanNumber}.");
            return true;
        }

        var reforgerElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        bool success = VerifyModerationSuccess(response, [TokenBanRemoved, ProcessingCommandToken]);
        AppLogger.Info($"[RconService:Moderation] Reforger ban removal complete in {reforgerElapsed:F2}ms for '{ban.IdentityId}': Success={success}");
        return success;
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
            AppLogger.Warn($"[RconService:Verify] Response indicates failure or unrecognized command: '{AppLogger.SanitizeSensitiveData(response)}'");
            return false;
        }

        bool match = expectedTokens.Any(t => response.Contains(t, StringComparison.OrdinalIgnoreCase));
        AppLogger.Debug($"[RconService:Verify] Moderation token match={match} for: '{AppLogger.SanitizeSensitiveData(response)}'");
        return match;
    }

    public Task SendCommandAsync(string rawCommand, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(rawCommand);
        cancellationToken.ThrowIfCancellationRequested();

        var client = _client;
        if (client is not { Connected: true })
        {
            RaiseOutputReceived($"[ERROR] Cannot dispatch '{AppLogger.SanitizeSensitiveData(rawCommand)}': Socket disconnected.");
            AppLogger.Warn($"[RconService:Command] Aborted: Socket disconnected for '{AppLogger.SanitizeSensitiveData(rawCommand)}'");
            return Task.CompletedTask;
        }

        AppLogger.TrackEvent("rcon_command_dispatched", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["command_head"] = rawCommand.Split(' ')[0]
        });

        RaiseOutputReceived($"[RCON OUT] {rawCommand}");
        client.SendCommand(rawCommand);
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Trace($"[RconService:Command] Dispatched '{AppLogger.SanitizeSensitiveData(rawCommand)}' in {elapsedMs:F2}ms.");
        return Task.CompletedTask;
    }

    public Task RestartServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#restart", cancellationToken);
    public Task ShutdownServerAsync(CancellationToken cancellationToken = default) => SendCommandAsync("#shutdown", cancellationToken);
    public Task SendGlobalMessageAsync(string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 {message}", cancellationToken);
    public Task SendAnnouncementAsync(string title, string message, CancellationToken cancellationToken = default) => SendCommandAsync($"#say -1 [ANNOUNCEMENT: {title}] {message}", cancellationToken);

    public async Task UpdatePlayerCommentAsync(string uid, string comment, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        AppLogger.Info($"[RconService:Comment] UpdatePlayerCommentAsync for UID '{uid}' (Length: {comment?.Length ?? 0})...");

        await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastKnownPlayers.FirstOrDefault(x => x.Uid == uid || x.Guid == uid || x.ReforgerUid == uid || x.BattlEyeGuid == uid) is { } p)
            {
                p.Comment = comment ?? string.Empty;
                AppLogger.Debug($"[RconService:Comment] Updated in-memory comment for '{p.Name}'.");
            }
        }
        finally
        {
            _playersSemaphore.Release();
        }

        await PlayerDatabaseStorageService.UpdateCommentAsync(uid, comment ?? string.Empty, CurrentProtocol).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[RconService:Comment] Comment persisted to SQLite in {elapsedMs:F2}ms for '{uid}'.");
    }

    public Task ClearDatabaseAsync(CancellationToken cancellationToken = default)
    {
        AppLogger.Warn($"[RconService:Database] Database purge requested for protocol {CurrentProtocol}.");
        return PlayerDatabaseStorageService.ClearDatabaseAsync(CurrentProtocol);
    }

    private async Task<string> ExecuteCommandWithAggregateResponseAsync(string command, TimeSpan maxTimeout, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var client = _client;
        if (client is not { Connected: true }) return string.Empty;

        if (cancellationToken.IsCancellationRequested) return string.Empty;

        await _commandExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RaiseOutputReceived($"[RCON OUT] {command}");

            if (CurrentProtocol == RconProtocol.BattlEye)
            {
                string directResponse = await client.SendCommandWithResponseAsync(command, TimeSpan.FromMilliseconds(400), cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(directResponse) && !directResponse.StartsWith('\0'))
                {
                    CheckProtocolMismatch(directResponse);
                    var directElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    AppLogger.Debug($"[RconService:Aggregate] Direct response received for '{AppLogger.SanitizeSensitiveData(command)}' in {directElapsed:F2}ms ({directResponse.Length} chars).");
                    return directResponse;
                }
            }

            await ResetAggregateBufferAsync(cancellationToken).ConfigureAwait(false);

            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                client.SendCommand(command, log: true);
            }

            bool isBanList = command.StartsWith("#ban list", StringComparison.OrdinalIgnoreCase) || command.Equals("bans", StringComparison.OrdinalIgnoreCase) || command.Equals("ban list", StringComparison.OrdinalIgnoreCase);
            bool isPlayerList = command.StartsWith("#players", StringComparison.OrdinalIgnoreCase) || command.Equals("players", StringComparison.OrdinalIgnoreCase);
            bool isKick = command.StartsWith("#kick", StringComparison.OrdinalIgnoreCase) || command.StartsWith("kick", StringComparison.OrdinalIgnoreCase);
            bool isBanCreate = command.StartsWith("#ban create", StringComparison.OrdinalIgnoreCase) || command.StartsWith("ban ", StringComparison.OrdinalIgnoreCase) || command.StartsWith("addBan", StringComparison.OrdinalIgnoreCase);
            bool isBanRemove = command.StartsWith("#ban remove", StringComparison.OrdinalIgnoreCase) || command.StartsWith("removeBan", StringComparison.OrdinalIgnoreCase);

            var quietThreshold = TimeSpan.FromMilliseconds(400);
            var timeoutLimit = DateTime.UtcNow.Add(maxTimeout);
            var startTime = DateTime.UtcNow;
            int initialWaitThresholdMs = Math.Max(2800, PingMs * 6);

            while (DateTime.UtcNow < timeoutLimit)
            {
                if (cancellationToken.IsCancellationRequested) return string.Empty;
                await Task.Delay(15, CancellationToken.None).ConfigureAwait(false);

                await _bufferSemaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                try
                {
                    var timeSinceLastChunk = DateTime.UtcNow - _lastMessageChunkUtc;
                    var chunksCount = _messageChunksCount;
                    var currentText = _aggregatedBuffer.ToString();

                    CheckProtocolMismatch(currentText);

                    if (CheckUniversalErrorTokens(currentText))
                    {
                        AppLogger.Trace($"[RconService:Aggregate] Universal terminal token detected ({currentText.Length} chars). Halting aggregation.");
                        break;
                    }

                    if (CheckCommandSpecificTerminalTokens(isBanList, isKick, isBanCreate, isBanRemove, currentText))
                    {
                        AppLogger.Trace($"[RconService:Aggregate] Command terminal token detected ({currentText.Length} chars). Halting aggregation.");
                        break;
                    }

                    bool hasActualPayload = DeterminePayloadPresence(isPlayerList, isBanList, currentText, chunksCount);

                    if (hasActualPayload && timeSinceLastChunk >= quietThreshold)
                    {
                        AppLogger.Trace($"[RconService:Aggregate] Quiet threshold satisfied ({timeSinceLastChunk.TotalMilliseconds:F1}ms >= {quietThreshold.TotalMilliseconds}ms). Halting aggregation.");
                        break;
                    }

                    if (chunksCount == 0 && (DateTime.UtcNow - startTime).TotalMilliseconds >= initialWaitThresholdMs)
                    {
                        AppLogger.Trace($"[RconService:Aggregate] No initial packet arrived after {(DateTime.UtcNow - startTime).TotalMilliseconds:F0}ms (Threshold: {initialWaitThresholdMs}ms). Halting aggregation.");
                        break;
                    }
                }
                finally
                {
                    _bufferSemaphore.Release();
                }
            }

            await _bufferSemaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                var aggregated = _aggregatedBuffer.ToString();
                CheckProtocolMismatch(aggregated);
                var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                AppLogger.Debug($"[RconService:Aggregate] Aggregation complete for '{AppLogger.SanitizeSensitiveData(command)}' in {totalElapsed:F2}ms: {aggregated.Length} chars across {_messageChunksCount} chunks.");
                return aggregated;
            }
            finally
            {
                _bufferSemaphore.Release();
            }
        }
        finally
        {
            _commandExecutionLock.Release();
        }
    }

    private async Task ResetAggregateBufferAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return;
        await _bufferSemaphore.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _aggregatedBuffer.Clear();
            _lastMessageChunkUtc = DateTime.UtcNow;
            _messageChunksCount = 0;
        }
        finally
        {
            _bufferSemaphore.Release();
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

        if (isKick && (currentText.Contains(TokenKicked, StringComparison.OrdinalIgnoreCase) || currentText.Contains(TokenAdminKick, StringComparison.OrdinalIgnoreCase) || currentText.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (isBanCreate && (currentText.Contains(TokenBanned, StringComparison.OrdinalIgnoreCase) || currentText.Contains(TokenAdminBan, StringComparison.OrdinalIgnoreCase) || currentText.Contains(TokenBanCreated, StringComparison.OrdinalIgnoreCase) || currentText.Contains("invalid", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (isBanRemove && (currentText.Contains(TokenBanRemoved, StringComparison.OrdinalIgnoreCase) || currentText.Contains("not found", StringComparison.OrdinalIgnoreCase)))
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

        var startTimestamp = Stopwatch.GetTimestamp();
        AppLogger.Info("[RconService:Dispose] Disposing RconService...");

        StopBackgroundPingMonitor();

        _rconStateSemaphore.Wait(CancellationToken.None);
        try
        {
            _client?.Dispose();
            _client = null;
        }
        finally
        {
            _rconStateSemaphore.Release();
        }

        _icmpPingSender.Dispose();
        _rconStateSemaphore.Dispose();
        _playersSemaphore.Dispose();
        _bufferSemaphore.Dispose();
        _commandExecutionLock.Dispose();

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Debug($"[RconService:Dispose] RconService disposal complete in {elapsedMs:F2}ms.");
    }
}