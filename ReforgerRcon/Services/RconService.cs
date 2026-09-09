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

public sealed partial class RconService : IRconService
{
    private enum RconCommandKind
    {
        Other,
        PlayerList,
        BanList,
        Kick,
        BanCreate,
        BanRemove,
        Admins
    }

    private const string ContextProtocol = "protocol";
    private const string ContextPlayerId = "player_id";
    private const string ContextPlayerName = "player_name";
    private const string ContextErrorMessage = "error_message";
    private const string ContextPingMs = "ping_ms";

    private const string ProtocolMetricKey = ContextProtocol;
    private const string ElapsedMsMetricKey = "elapsed_ms";
    private const string CommandMetricKey = "command";
    private const string ResponseMetricKey = "response";
    private const string VerifiedSuccessMetricKey = "verified_success";
    private const string DurationSecondsMetricKey = "duration_seconds";

    private const string TokenBanned = "banned!";
    private const string TokenBanCreated = "ban created!";
    private const string TokenAdminBan = "Admin Ban";
    private const string TokenAdminKick = "Admin Kick";
    private const string TokenKicked = "kicked!";
    private const string TokenBanRemoved = "ban removed!";
    private const string TokenNotFound = "not found";
    private const string PlayersOnServerToken = "Players on server:";

    private BattlEyeClient? _client;
    private ServerProfile? _currentProfile;
    private string _lastConnectionError = string.Empty;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingCommands = new();
    private readonly ConcurrentDictionary<string, long> _recentlyAnnouncedJoins = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, long> _recentlyAnnouncedLeaves = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _aggregatedBuffer = new();
    private readonly SemaphoreSlim _bufferSemaphore = new(1, 1);
    private readonly SemaphoreSlim _commandExecutionLock = new(1, 1);
    private readonly SemaphoreSlim _chunkArrivedSignal = new(0, 100);
    private DateTime _sessionStartTimeUtc = DateTime.UtcNow;
    private DateTime _lastMessageChunkUtc = DateTime.UtcNow;
    private int _lastChunkSizeBytes;
    private int _messageChunksCount;
    private bool _isDisposed;
    private volatile bool _hasInitialPlayerSnapshot;
    private volatile bool _isInitialConnectPhase;
    private volatile bool _isManualDisconnecting;
    private int _protocolMismatchFired;
    private Task<List<PlayerModel>>? _inFlightPlayersTask;

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

    public int PingMs
    {
        get
        {
            if (_client is { LastPingMs: > 0 } client)
            {
                return client.LastPingMs;
            }

            if (_smoothedPingMs > 0)
            {
                return _smoothedPingMs;
            }

            return 0;
        }
    }

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
        var cleanMessage = message ?? string.Empty;
        try
        {
            AppLogger.Trace($"[RconService:OutputReceived] Length: {cleanMessage.Length} chars | Content: {AppLogger.SanitizeSensitiveData(cleanMessage)}");
            OutputReceived?.Invoke(this, cleanMessage);
        }
        catch (Exception ex)
        {
            var context = new Dictionary<string, object?>
            {
                ["message_length"] = cleanMessage.Length,
                ["error"] = ex.Message,
                ["stack_trace"] = ex.StackTrace
            };
            AppLogger.Error("[RconService:OutputReceived] Subscriber handler threw an exception.", ex, context);
            ToastNotificationService.Instance.ShowWarning("Console Dispatch Notice", "A subscriber error occurred while dispatching console output.");
        }
    }

    private void CheckProtocolMismatch(string message)
    {
        if (_protocolMismatchFired != 0) return;

        try
        {
            var detected = ReforgerResponseParser.DetectProtocol(message);
            if (detected.HasValue && detected.Value != CurrentProtocol && Interlocked.CompareExchange(ref _protocolMismatchFired, 1, 0) == 0)
            {
                DetectedProtocolMismatch = detected.Value;
                var context = new Dictionary<string, object?>
                {
                    ["configured_protocol"] = CurrentProtocol.ToString(),
                    ["detected_protocol"] = detected.Value.ToString(),
                    ["payload_preview"] = AppLogger.SanitizeSensitiveData(message.Length > 120 ? message[..120] : message)
                };

                AppLogger.Warn($"[RconService:ProtocolMismatch] Protocol signature mismatch confirmed: configured '{CurrentProtocol}', detected '{detected.Value}'.", null, context);

                AppLogger.TrackEvent("rcon_protocol_mismatch_detected", new Dictionary<string, object>
                {
                    ["configured_protocol"] = CurrentProtocol.ToString(),
                    ["detected_protocol"] = detected.Value.ToString()
                });

                ProtocolMismatchDetected?.Invoke(this, detected.Value);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RconService:ProtocolMismatch] Exception during protocol signature evaluation: " + ex.Message, ex);
        }
    }

    private void RaisePlayerJoined(PlayerModel player)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            [ContextPlayerId] = player.Id,
            [ContextPlayerName] = player.Name,
            ["uid"] = player.Uid,
            ["guid"] = player.Guid,
            ["endpoint"] = player.FormattedEndpoint
        };

        try
        {
            if (!_hasInitialPlayerSnapshot || _isInitialConnectPhase)
            {
                AppLogger.Trace($"[RconService:JoinLifecycle] Suppressed join alert for '{player.Name}' (ID: #{player.Id}) during initial snapshot synchronization.", context);
                return;
            }

            var key = $"{player.Id}_{player.Name.Trim()}";
            var now = Stopwatch.GetTimestamp();

            if (_recentlyAnnouncedJoins.TryGetValue(key, out var lastAnnounced))
            {
                var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
                if (elapsedSec < 6.0)
                {
                    AppLogger.Trace($"[RconService:JoinLifecycle] Debounced duplicate join alert for '{player.Name}' ({elapsedSec:F1}s since last alert).", context);
                    return;
                }
            }

            _recentlyAnnouncedJoins[key] = now;
            _recentlyAnnouncedLeaves.TryRemove(key, out _);

            var dispatchDuration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context["dispatch_ms"] = dispatchDuration;
            AppLogger.Info($"[RconService:JoinLifecycle] PlayerJoined event dispatched in {dispatchDuration:F2}ms: Name='{player.Name}' (ID: #{player.Id}, Endpoint: '{player.FormattedEndpoint}', Location: '{player.DisplayLocation}').", context);
            PlayerJoined?.Invoke(this, player);
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[RconService:JoinLifecycle] Error dispatching PlayerJoined for '{player.Name}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowWarning("Player Alert Notice", $"Error notifying join event for {player.Name}.");
        }
    }

    private void RaisePlayerLeft(PlayerModel player)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            [ContextPlayerId] = player.Id,
            [ContextPlayerName] = player.Name,
            ["uid"] = player.Uid,
            ["guid"] = player.Guid
        };

        try
        {
            if (!_hasInitialPlayerSnapshot || _isInitialConnectPhase)
            {
                return;
            }

            var key = $"{player.Id}_{player.Name.Trim()}";
            var now = Stopwatch.GetTimestamp();

            if (_recentlyAnnouncedLeaves.TryGetValue(key, out var lastAnnounced))
            {
                var elapsedSec = Stopwatch.GetElapsedTime(lastAnnounced).TotalSeconds;
                if (elapsedSec < 6.0)
                {
                    AppLogger.Trace($"[RconService:LeaveLifecycle] Debounced duplicate leave alert for '{player.Name}' ({elapsedSec:F1}s since last alert).", context);
                    return;
                }
            }

            _recentlyAnnouncedLeaves[key] = now;
            _recentlyAnnouncedJoins.TryRemove(key, out _);

            var dispatchDuration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context["dispatch_ms"] = dispatchDuration;
            AppLogger.Info($"[RconService:LeaveLifecycle] PlayerLeft event dispatched in {dispatchDuration:F2}ms: Name='{player.Name}' (ID: #{player.Id}).", context);
            PlayerLeft?.Invoke(this, player);
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[RconService:LeaveLifecycle] Error dispatching PlayerLeft for '{player.Name}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowWarning("Player Alert Notice", $"Error notifying leave event for {player.Name}.");
        }
    }

    private void RaiseConnectionLost(string reason)
    {
        var context = new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["last_packet_utc"] = LastPacketTime.ToString("o", CultureInfo.InvariantCulture),
            [ContextProtocol] = CurrentProtocol.ToString(),
            [ContextPingMs] = PingMs
        };

        try
        {
            AppLogger.Warn($"[RconService:ConnectionState] Socket connection lost. Reason: '{reason}', LastPacketTime: {LastPacketTime:O}, SmoothedPing: {PingMs}ms.", null, context);
            ConnectionLost?.Invoke(this, reason);
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error("[RconService:ConnectionState] Error in ConnectionLost event subscriber: " + ex.Message, ex, context);
            ToastNotificationService.Instance.ShowError("Connection Lost Error", $"Socket connection lost: {reason}");
        }
    }

    public async Task<bool> ConnectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ServerIp);

        var totalStartTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"RconService.ConnectAsync({profile.ServerIp}:{profile.Port}, {profile.Protocol})");

        var context = new Dictionary<string, object?>
        {
            ["host"] = profile.ServerIp,
            ["port"] = profile.Port,
            [ProtocolMetricKey] = profile.Protocol.ToString(),
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[RconService:Connect] Commencing connection workflow for target: {profile.ServerIp}:{profile.Port} ({profile.Protocol})...", context);
        await _rconStateSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _currentProfile = profile;
            _lastConnectionError = string.Empty;
            _hasInitialPlayerSnapshot = false;
            _isInitialConnectPhase = true;
            _isManualDisconnecting = false;
            DetectedProtocolMismatch = null;
            _protocolMismatchFired = 0;
            _sessionStartTimeUtc = DateTime.UtcNow;
            _recentlyAnnouncedJoins.Clear();
            _recentlyAnnouncedLeaves.Clear();

            Volatile.Write(ref _inFlightPlayersTask, null);

            await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _lastKnownPlayers.Clear();
                AppLogger.Trace("[RconService:Connect] Cleared active player tracking list.");
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
                context["ip_family"] = ip.AddressFamily.ToString();
                AppLogger.Debug($"[RconService:Connect] Host resolved as direct numeric IP: {ip} ({ip.AddressFamily}).", context);
            }
            else
            {
                var dnsStart = Stopwatch.GetTimestamp();
                AppLogger.Debug($"[RconService:Connect] Querying DNS resolver for hostname '{profile.ServerIp}'...", context);
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(profile.ServerIp, cancellationToken).ConfigureAwait(false);
                    var dnsElapsedMs = Stopwatch.GetElapsedTime(dnsStart).TotalMilliseconds;
                    if (addresses.Length > 0)
                    {
                        ip = addresses[0];
                        context["resolved_ip"] = ip.ToString();
                        context["candidate_ips_count"] = addresses.Length;
                        context["dns_duration_ms"] = dnsElapsedMs;
                        AppLogger.Info($"[RconService:Connect] DNS resolution successful: '{profile.ServerIp}' -> {ip} in {dnsElapsedMs:F2}ms ({addresses.Length} candidates).", context);
                    }
                    else
                    {
                        _lastConnectionError = $"DNS resolution returned zero candidate addresses for host '{profile.ServerIp}'.";
                        AppLogger.Warn($"[RconService:Connect] {_lastConnectionError}", null, context);
                    }
                }
                catch (Exception dnsEx)
                {
                    _lastConnectionError = $"DNS resolution failed for '{profile.ServerIp}': {dnsEx.Message}";
                    AppLogger.Error($"[RconService:Connect] {_lastConnectionError}", dnsEx, context);
                    ToastNotificationService.Instance.ShowError("DNS Lookup Error", _lastConnectionError);
                }
            }

            if (ip == null)
            {
                if (string.IsNullOrWhiteSpace(_lastConnectionError))
                {
                    _lastConnectionError = $"Unable to resolve host '{profile.ServerIp}' to a valid IP endpoint.";
                }

                AppLogger.Error($"[RconService:Connect] {_lastConnectionError} Aborting connection.", null, context);
                ToastNotificationService.Instance.ShowError("Host Unreachable", _lastConnectionError);
                transaction.Finish(SpanStatus.InvalidArgument);
                return false;
            }

            var rawPassword = profile.Password ?? string.Empty;
            AppLogger.Debug($"[RconService:Connect] Creating BattlEyeClient instance for endpoint {ip}:{profile.Port} (PasswordLength={rawPassword.Length})...", context);
            var credentials = new BattlEyeLoginCredentials(ip, profile.Port, rawPassword);
            _client = new BattlEyeClient(credentials);

            var connectTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _client.BattlEyeConnected += args =>
            {
                AppLogger.Info($"[RconService:Callback] BattlEyeConnected event received: Result={args.ConnectionResult}, Message='{args.Message}'");
                connectTcs.TrySetResult(args.ConnectionResult == BattlEyeConnectionResult.Success);
            };

            _client.BattlEyeDisconnected += async args =>
            {
                if (_isManualDisconnecting) return;

                var uptimeSeconds = Math.Max(0, (int)(DateTime.UtcNow - _sessionStartTimeUtc).TotalSeconds);

                AppLogger.TrackEvent("rcon_reconnect_triggered", new Dictionary<string, object>
                {
                    [ProtocolMetricKey] = CurrentProtocol.ToString(),
                    ["disconnection_type"] = args.DisconnectionType?.ToString() ?? "Unknown",
                    ["uptime_seconds"] = uptimeSeconds,
                    ["is_automatic"] = args.DisconnectionType != BattlEyeDisconnectionType.Manual
                });

                if (args.DisconnectionType == BattlEyeDisconnectionType.Manual)
                {
                    AppLogger.Info($"[RconService:Callback] BattlEyeDisconnected event: Type=Manual, Message='{args.Message}', Uptime={uptimeSeconds}s.");
                }
                else
                {
                    AppLogger.Warn($"[RconService:Callback] BattlEyeDisconnected event: Type={args.DisconnectionType}, Message='{args.Message}', Uptime={uptimeSeconds}s.");
                }
                RaiseOutputReceived($"[SYSTEM] Disconnected: {args.Message}");

                StopBackgroundPingMonitor();
                _hasInitialPlayerSnapshot = false;
                _isInitialConnectPhase = false;

                Volatile.Write(ref _inFlightPlayersTask, null);

                try
                {
                    AppLogger.Debug($"[RconService:Disconnect] Marking {_currentProfile?.Protocol} player records offline in local SQLite database...");
                    await PlayerDatabaseStorageService.SetAllOfflineAsync(_currentProfile?.Protocol).ConfigureAwait(false);
                }
                catch (Exception dbEx)
                {
                    AppLogger.Error("[RconService:Disconnect] Failed setting players offline in SQLite: " + dbEx.Message, dbEx);
                }

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
                    AppLogger.Warn($"[RconService:Connect] Handshake timeout threshold (5000ms) reached for {ip}:{profile.Port}.");
                    connectTcs.TrySetResult(false);
                }
            });

            var clientConnectTask = _client.ConnectAsync(cts.Token);

            bool success = await connectTcs.Task.ConfigureAwait(false);
            await clientConnectTask.ConfigureAwait(false);

            var totalElapsedMs = Stopwatch.GetElapsedTime(totalStartTimestamp).TotalMilliseconds;
            context[ElapsedMsMetricKey] = totalElapsedMs;

            if (success)
            {
                LastPacketTime = DateTime.UtcNow;
                _sessionStartTimeUtc = DateTime.UtcNow;

                if (_client.LastPingMs > 0)
                {
                    RecordPingMeasurement(_client.LastPingMs);
                }

                context[ContextPingMs] = PingMs;
                RaiseOutputReceived($"[SYSTEM] Connected successfully to {profile.ServerIp}:{profile.Port}");
                AppLogger.Info($"[RconService:Connect] RCON session active with {profile.ServerIp}:{profile.Port} via {profile.Protocol} in {totalElapsedMs:F2}ms (MeasuredPing={PingMs}ms).", context);

                StartBackgroundPingMonitor(ip);

                AppLogger.Debug("[RconService:Connect] Kicking off initial asynchronous player pre-fetch task...");
                Volatile.Write(ref _inFlightPlayersTask, Task.Run(() => GetPlayersInternalAsync(CancellationToken.None), CancellationToken.None));

                op.Complete();
                transaction.Finish(SpanStatus.Ok);

                AppLogger.TrackEvent("rcon_connect_success", new Dictionary<string, object>
                {
                    [ProtocolMetricKey] = profile.Protocol.ToString(),
                    [ContextPingMs] = PingMs,
                    ["duration_ms"] = totalElapsedMs
                });
            }
            else
            {
                if (string.IsNullOrWhiteSpace(_lastConnectionError))
                {
                    _lastConnectionError = _client.LastErrorDiagnostic;
                }

                AppLogger.Warn($"[RconService:Connect] Authentication handshake failed for {profile.ServerIp}:{profile.Port} after {totalElapsedMs:F2}ms. Reason: {LastConnectionError}", null, context);
                transaction.Finish(SpanStatus.DeadlineExceeded);

                AppLogger.TrackEvent("rcon_connect_failed", new Dictionary<string, object>
                {
                    [ProtocolMetricKey] = profile.Protocol.ToString(),
                    ["duration_ms"] = totalElapsedMs,
                    ["reason"] = LastConnectionError
                });

                ToastNotificationService.Instance.ShowError("Connection Failed", $"Could not connect to {profile.ServerIp}:{profile.Port}: {LastConnectionError}");
            }

            return success;
        }
        catch (SocketException sockEx)
        {
            _lastConnectionError = $"Socket exception ({sockEx.SocketErrorCode}): {sockEx.Message}";
            AppLogger.Error($"[RconService:Connect] SocketException encountered during connect: {sockEx.SocketErrorCode} ({sockEx.NativeErrorCode}): {sockEx.Message}", sockEx, context);
            ToastNotificationService.Instance.ShowError("Socket Connection Fault", _lastConnectionError);
            return false;
        }
        catch (OperationCanceledException opEx)
        {
            _lastConnectionError = "Connection attempt was canceled or timed out.";
            AppLogger.Trace($"[RconService:Connect] Connect operation canceled by token: {opEx.Message}", context);
            ToastNotificationService.Instance.ShowWarning("Connection Canceled", _lastConnectionError);
            return false;
        }
        catch (Exception ex)
        {
            _lastConnectionError = $"Unexpected connection error: {ex.Message}";
            AppLogger.Error($"[RconService:Connect] Unhandled error during ConnectAsync: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Connection Error", _lastConnectionError);
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
        _isManualDisconnecting = true;
        try
        {
            StopBackgroundPingMonitor();
            _hasInitialPlayerSnapshot = false;
            _isInitialConnectPhase = false;

            Volatile.Write(ref _inFlightPlayersTask, null);

            AppLogger.TrackEvent("rcon_disconnect", new Dictionary<string, object>
            {
                [ProtocolMetricKey] = CurrentProtocol.ToString()
            });

            if (_client is { Connected: true } && CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                AppLogger.Debug("[RconService:Disconnect] Dispatching Reforger @logout handshake packet...");
                await SendReforgerLogoutAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                AppLogger.Debug($"[RconService:Disconnect] Resetting {CurrentProtocol} database player statuses in SQLite...");
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
            AppLogger.Info($"[RconService:Disconnect] Disconnect sequence finalized in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[RconService:Disconnect] Exception during disconnect sequence: " + ex.Message, ex);
            ToastNotificationService.Instance.ShowWarning("Disconnect Warning", $"Encountered an issue during shutdown: {ex.Message}");
        }
        finally
        {
            _isManualDisconnecting = false;
            _rconStateSemaphore.Release();
        }
    }

    private void StartBackgroundPingMonitor(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        StopBackgroundPingMonitor();

        _pingLoopCts = new CancellationTokenSource();
        var token = _pingLoopCts.Token;

        AppLogger.Debug($"[RconService:Ping] Initializing background ICMP ping monitor worker for: {ip}...");

        _ = Task.Run(async () =>
        {
            await Task.Delay(3000, token).ConfigureAwait(false);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    var client = _client;
                    if (client is { Connected: true, LastPingMs: > 0 })
                    {
                        RecordPingMeasurement(client.LastPingMs);
                    }
                    else
                    {
                        await SampleNetworkPingAsync(ip, token).ConfigureAwait(false);
                    }

                    var delayTask = Task.Delay(3000, CancellationToken.None);
                    var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    await using (token.Register(static s => ((TaskCompletionSource?)s)?.TrySetResult(), tcs).ConfigureAwait(false))
                    {
                        await Task.WhenAny(delayTask, tcs.Task).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[RconService:Ping] Monitor loop exception notice: {ex.Message}");
                }
            }
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
        AppLogger.Debug("[RconService:Ping] Background ping monitor stopped and sample queue cleared.");
    }

    private async Task SampleNetworkPingAsync(IPAddress ip, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        try
        {
            var reply = await _icmpPingSender.SendPingAsync(ip, TimeSpan.FromMilliseconds(400), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success && reply.RoundtripTime > 0)
            {
                RecordPingMeasurement((int)reply.RoundtripTime);
                AppLogger.Trace($"[RconService:Ping] ICMP probe successful: RTT={reply.RoundtripTime}ms, Smoothed={PingMs}ms.");
            }
            else
            {
                AppLogger.Trace($"[RconService:Ping] ICMP probe status: {reply.Status}.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[RconService:Ping] ICMP probe exception notice: {ex.Message}");
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
        AppLogger.Trace($"[RconService:Ping] Registered RTT sample: {sampleMs}ms -> SmoothedPing: {PingMs}ms.");
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
            AppLogger.Trace($"[RconService:Message] Processed incoming message ID #{args.Id} in {elapsedMs:F2}ms ({message.Length} chars).");
        }
        catch (Exception ex)
        {
            var context = new Dictionary<string, object?>
            {
                ["packet_id"] = args.Id,
                ["message_len"] = args.Message?.Length ?? 0,
                [ContextErrorMessage] = ex.Message
            };
            AppLogger.Error($"[RconService:Message] Exception processing message ID #{args.Id}: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowWarning("Packet Processing Notice", $"Failed processing packet #{args.Id}: {ex.Message}");
        }
    }

    private void AppendToBuffer(string chunk)
    {
        _bufferSemaphore.Wait(CancellationToken.None);
        try
        {
            if (_aggregatedBuffer.Length > 0 &&
                _aggregatedBuffer[^1] != '\n' &&
                !chunk.StartsWith('\r') &&
                !chunk.StartsWith('\n'))
            {
                _aggregatedBuffer.Append('\n');
            }

            _aggregatedBuffer.Append(chunk);
            _lastMessageChunkUtc = DateTime.UtcNow;
            _lastChunkSizeBytes = Encoding.UTF8.GetByteCount(chunk);
            _messageChunksCount++;

            AppLogger.Trace($"[RconService:Buffer] Appended chunk #{_messageChunksCount} ({chunk.Length} chars, {_lastChunkSizeBytes} bytes). Accumulated buffer length: {_aggregatedBuffer.Length} chars.");

            if (_chunkArrivedSignal.CurrentCount == 0)
            {
                _chunkArrivedSignal.Release();
            }
        }
        finally
        {
            _bufferSemaphore.Release();
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
        var pendingTask = Interlocked.Exchange(ref _inFlightPlayersTask, null);

        if (pendingTask != null)
        {
            try
            {
                AppLogger.Debug("[RconService:GetPlayers] Awaiting concurrent in-flight player pre-fetch task...");
                return await pendingTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[RconService:GetPlayers] Pre-fetch task notice: {ex.Message}. Falling through to fresh query.", ex);
            }
        }

        return await GetPlayersInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<PlayerModel>> GetPlayersInternalAsync(CancellationToken cancellationToken)
    {
        var client = _client;
        if (client is not { Connected: true })
        {
            AppLogger.Warn("[RconService:GetPlayers] Socket is disconnected. Returning empty list.");
            return [];
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"RconService.GetPlayersAsync({CurrentProtocol})");

        try
        {
            List<PlayerModel> currentPlayers;
            double parseElapsed;

            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                (currentPlayers, parseElapsed) = await FetchReforgerPlayersInternalAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                (currentPlayers, parseElapsed) = await FetchBattlEyePlayersInternalAsync(cancellationToken).ConfigureAwait(false);
            }

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
                    AppLogger.Info($"[RconService:GetPlayers] Initial snapshot established ({currentPlayers.Count} players active).");
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
            AppLogger.Debug($"[RconService:GetPlayers] GetPlayersAsync complete in {totalElapsed:F2}ms (Parse={parseElapsed:F2}ms, OnlineCount={currentPlayers.Count}).");
            return currentPlayers;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Trace("[RconService:GetPlayers] GetPlayersAsync operation canceled.");
            throw;
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService:GetPlayers] SocketException during player query: {sockEx.SocketErrorCode}", sockEx);
            ToastNotificationService.Instance.ShowError("Network Socket Error", $"Socket connection error querying players: {sockEx.SocketErrorCode}");
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:GetPlayers] Unexpected error querying player list: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("RCON Query Error", $"Failed retrieving active players: {ex.Message}");
            return [];
        }
    }

    public async Task<List<BanModel>> GetBansAsync(int maxPages = 0, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is not { Connected: true })
        {
            AppLogger.Warn("[RconService:GetBans] Socket is disconnected. Returning empty ban list.");
            return [];
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"RconService.GetBansAsync({CurrentProtocol}, MaxPages: {maxPages})");

        try
        {
            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                return await GetReforgerBansInternalAsync(maxPages, startTimestamp, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return await GetBattlEyeBansInternalAsync(startTimestamp, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Trace("[RconService:GetBans] Ban query operation canceled.");
            throw;
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService:GetBans] SocketException during ban query: {sockEx.SocketErrorCode}", sockEx);
            ToastNotificationService.Instance.ShowError("Network Socket Error", $"Socket error retrieving bans: {sockEx.SocketErrorCode}");
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:GetBans] Unexpected error querying ban list: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Ban Query Error", $"Failed retrieving ban list: {ex.Message}");
            return [];
        }
    }

    public async Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();
        AppLogger.Debug($"[RconService:Database] Querying all database records for protocol {CurrentProtocol}...");
        try
        {
            var result = await PlayerDatabaseStorageService.GetAllAsync(CurrentProtocol).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[RconService:Database] Retrieved {result.Count} database records in {elapsedMs:F2}ms.");
            return result;
        }
        catch (OperationCanceledException)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Trace($"[RconService:Database] GetDatabasePlayersAsync cancelled after {elapsedMs:F2}ms.");
            throw;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Error($"[RconService:Database] Failed retrieving database players after {elapsedMs:F2}ms: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Database Error", $"Failed loading player database: {ex.Message}");
            throw;
        }
    }

    public async Task<PagedResult<DatabasePlayerModel>> GetPagedDatabasePlayersAsync(DatabaseQueryParameters parameters, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = Stopwatch.GetTimestamp();
        var sanitizedQuery = AppLogger.SanitizeSensitiveData(parameters.SearchQuery);
        AppLogger.Debug($"[RconService:Database] Forwarding paginated query: Protocol={parameters.Protocol}, Page={parameters.PageIndex}, Size={parameters.PageSize}, Query='{sanitizedQuery}', Type='{parameters.SearchType ?? "All"}', Sort='{parameters.SortBy ?? "Default"}'...");

        try
        {
            var result = await PlayerDatabaseStorageService.GetPagedAsync(parameters, cancellationToken).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[RconService:Database] Paginated retrieval completed in {elapsedMs:F2}ms (Returned={result.Items.Count}, Total={result.TotalCount}, Page={result.PageIndex}/{Math.Max(1, (int)Math.Ceiling((double)result.TotalCount / Math.Max(1, result.PageSize)))}).");
            return result;
        }
        catch (OperationCanceledException)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Trace($"[RconService:Database] Paginated query cancelled after {elapsedMs:F2}ms.");
            throw;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Error($"[RconService:Database] Failure in GetPagedDatabasePlayersAsync after {elapsedMs:F2}ms: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Database Error", $"Failed querying player database page: {ex.Message}");
            throw;
        }
    }

    public async Task<bool> KickPlayerAsync(PlayerModel player, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var startTimestamp = Stopwatch.GetTimestamp();
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? BuildReforgerKickCommand(player.Id)
            : BuildBattlEyeKickCommand(player.Id, cleanReason);

        var context = new Dictionary<string, object?>
        {
            [ContextPlayerId] = player.Id,
            [ContextPlayerName] = player.Name,
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            [CommandMetricKey] = cmd,
            ["has_reason"] = !string.IsNullOrEmpty(cleanReason)
        };

        AppLogger.Info($"[RconService:Moderation] KickPlayerAsync starting: '{player.Name}' (ID: #{player.Id}, Protocol: {CurrentProtocol})...", context);

        AppLogger.TrackEvent("moderation_kick", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            [ContextPlayerId] = player.Id,
            ["has_reason"] = !string.IsNullOrEmpty(cleanReason)
        });

        try
        {
            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                return await KickReforgerPlayerAsync(player, cmd, context, startTimestamp, cancellationToken).ConfigureAwait(false);
            }

            return await KickBattlEyePlayerAsync(player, cmd, context, startTimestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:Moderation] Critical failure executing kick for '{player.Name}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Kick Command Failed", $"Unable to execute kick: {ex.Message}", cmd);
            return false;
        }
    }

    public Task<bool> BanPlayerAsync(PlayerModel player, long durationSeconds, string reason, CancellationToken cancellationToken = default)
    {
        return BanPlayerWithOptionalIpAsync(player, durationSeconds, reason, banIp: true, cancellationToken);
    }

    public async Task<bool> BanPlayerWithOptionalIpAsync(PlayerModel player, long durationSeconds, string reason, bool banIp, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(player);

        var startTimestamp = Stopwatch.GetTimestamp();
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        var context = new Dictionary<string, object?>
        {
            [ContextPlayerId] = player.Id,
            [ContextPlayerName] = player.Name,
            ["ip"] = player.Ip,
            [DurationSecondsMetricKey] = durationSeconds,
            ["be_minutes"] = beMinutes,
            ["ban_ip"] = banIp,
            [ProtocolMetricKey] = CurrentProtocol.ToString()
        };

        AppLogger.Info($"[RconService:Moderation] BanPlayerWithOptionalIpAsync starting: Name='{player.Name}', ID=#{player.Id}...", context);

        AppLogger.TrackEvent("moderation_ban", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            [DurationSecondsMetricKey] = durationSeconds,
            ["is_permanent"] = durationSeconds <= 0,
            ["ban_ip"] = banIp
        });

        try
        {
            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                return await BanReforgerPlayerAsync(player, durationSeconds, cleanReason, context, startTimestamp, cancellationToken).ConfigureAwait(false);
            }

            return await BanBattlEyePlayerAsync(player, beMinutes, cleanReason, banIp, context, startTimestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:Moderation] Critical error executing ban for '{player.Name}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Ban Failed", $"Ban operation failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);

        var startTimestamp = Stopwatch.GetTimestamp();
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cleanReason = string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.Trim();

        var context = new Dictionary<string, object?>
        {
            ["identity"] = identity,
            [DurationSecondsMetricKey] = durationSeconds,
            ["be_minutes"] = beMinutes,
            ["is_ip"] = isIp,
            [ProtocolMetricKey] = CurrentProtocol.ToString()
        };

        AppLogger.Info($"[RconService:Moderation] OfflineBanAsync starting: Identity='{identity}'...", context);

        AppLogger.TrackEvent("moderation_offline_ban", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["is_ip"] = isIp,
            [DurationSecondsMetricKey] = durationSeconds
        });

        try
        {
            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                return await OfflineBanReforgerAsync(identity, durationSeconds, cleanReason, context, startTimestamp, cancellationToken).ConfigureAwait(false);
            }

            return await OfflineBanBattlEyeAsync(identity, beMinutes, cleanReason, context, startTimestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:Moderation] Critical error executing offline ban for '{identity}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Offline Ban Failed", $"Offline ban failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> RemoveBanAsync(BanModel ban, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ban);

        var startTimestamp = Stopwatch.GetTimestamp();
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban remove {ban.IdentityId}"
            : $"removeBan {ban.BanNumber}";

        var context = new Dictionary<string, object?>
        {
            ["ban_number"] = ban.BanNumber,
            ["identity_id"] = ban.IdentityId,
            [CommandMetricKey] = cmd,
            [ProtocolMetricKey] = CurrentProtocol.ToString()
        };

        AppLogger.Info($"[RconService:Moderation] RemoveBanAsync starting for Ban #{ban.BanNumber} (Identity: '{ban.IdentityId}')...", context);

        AppLogger.TrackEvent("moderation_remove_ban", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["ban_number"] = ban.BanNumber
        });

        try
        {
            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                return await RemoveReforgerBanAsync(ban, cmd, context, startTimestamp, cancellationToken).ConfigureAwait(false);
            }

            return await RemoveBattlEyeBanAsync(ban, cmd, context, startTimestamp, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:Moderation] Critical error executing remove ban for '{ban.IdentityId}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Unban Failed", $"Failed removing ban: {ex.Message}", cmd);
            return false;
        }
    }

    private static bool VerifyModerationSuccess(string? response, string[] expectedTokens, bool acceptEmptyAck = false)
    {
        if (response == null)
        {
            return false;
        }

        if (acceptEmptyAck && response.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            return false;
        }

        if (response.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("unknown command", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
            response.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            response.Contains(TokenNotFound, StringComparison.OrdinalIgnoreCase) ||
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
            ToastNotificationService.Instance.ShowWarning("Socket Disconnected", "Cannot dispatch command: socket connection is offline.");
            return Task.CompletedTask;
        }

        AppLogger.TrackEvent("rcon_command_dispatched", new Dictionary<string, object>
        {
            [ProtocolMetricKey] = CurrentProtocol.ToString(),
            ["command_head"] = rawCommand.Split(' ')[0]
        });

        try
        {
            RaiseOutputReceived($"[RCON OUT] {rawCommand}");
            client.SendCommand(rawCommand);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Trace($"[RconService:Command] Dispatched '{AppLogger.SanitizeSensitiveData(rawCommand)}' in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:Command] Critical error sending raw command '{AppLogger.SanitizeSensitiveData(rawCommand)}': {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Command Error", $"Failed dispatching command: {ex.Message}");
        }

        return Task.CompletedTask;
    }

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

        try
        {
            await PlayerDatabaseStorageService.UpdateCommentAsync(uid, comment ?? string.Empty, CurrentProtocol).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[RconService:Comment] Comment persisted to SQLite in {elapsedMs:F2}ms for '{uid}'.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:Comment] Error persisting comment to SQLite for '{uid}': {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Database Error", $"Failed persisting player comment: {ex.Message}");
        }
    }

    public Task ClearDatabaseAsync(CancellationToken cancellationToken = default)
    {
        AppLogger.Warn($"[RconService:Database] Database purge requested for protocol {CurrentProtocol}.");
        return PlayerDatabaseStorageService.ClearDatabaseAsync(CurrentProtocol);
    }

    private RconCommandKind ClassifyCommand(string command) =>
        CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? ClassifyReforgerCommand(command)
            : ClassifyBattlEyeCommand(command);

    private async Task<string> ExecuteCommandWithAggregateResponseAsync(string command, TimeSpan maxTimeout, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var client = _client;
        if (client is not { Connected: true }) return string.Empty;

        cancellationToken.ThrowIfCancellationRequested();

        var aggregateContext = new Dictionary<string, object?>
        {
            ["command"] = AppLogger.SanitizeSensitiveData(command),
            [ContextProtocol] = CurrentProtocol.ToString(),
            ["max_timeout_ms"] = maxTimeout.TotalMilliseconds,
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Debug($"[RconService:Aggregate] Initiating command aggregation for: '{AppLogger.SanitizeSensitiveData(command)}'...", aggregateContext);

        await _commandExecutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RaiseOutputReceived($"[RCON OUT] {command}");

            if (CurrentProtocol == RconProtocol.BattlEye)
            {
                string? directResponse = await ExecuteBattlEyeDirectResponseAsync(client, command, maxTimeout, startTimestamp, cancellationToken).ConfigureAwait(false);
                if (directResponse != null)
                {
                    return directResponse;
                }
            }

            await ResetAggregateBufferAsync(cancellationToken).ConfigureAwait(false);

            if (CurrentProtocol == RconProtocol.ReforgerBuiltIn)
            {
                client.SendCommand(command, log: true);
            }

            var commandKind = ClassifyCommand(command);
            var timeoutLimit = DateTime.UtcNow.Add(maxTimeout);
            bool isTimeoutExceeded = false;

            while (DateTime.UtcNow < timeoutLimit)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingTimeout = timeoutLimit - DateTime.UtcNow;
                if (remainingTimeout <= TimeSpan.Zero)
                {
                    isTimeoutExceeded = true;
                    break;
                }

                var waitMs = Math.Min(25, (int)remainingTimeout.TotalMilliseconds);
                await _chunkArrivedSignal.WaitAsync(waitMs, cancellationToken).ConfigureAwait(false);

                await _bufferSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var timeSinceLastChunk = DateTime.UtcNow - _lastMessageChunkUtc;
                    var chunksCount = _messageChunksCount;
                    var lastChunkSize = _lastChunkSizeBytes;
                    var currentText = _aggregatedBuffer.ToString();

                    if (CheckUniversalErrorTokens(currentText))
                    {
                        AppLogger.Trace($"[RconService:Aggregate] Universal terminal token detected ({currentText.Length} chars). Halting aggregation.");
                        break;
                    }

                    bool isTerminal = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                        ? CheckReforgerCommandTerminalTokens(commandKind, currentText, chunksCount, lastChunkSize)
                        : CheckBattlEyeCommandTerminalTokens(commandKind, currentText, chunksCount);

                    if (isTerminal)
                    {
                        AppLogger.Trace($"[RconService:Aggregate] Command terminal token matched ({currentText.Length} chars, ChunksCount={chunksCount}, LastSize={lastChunkSize} bytes). Terminating aggregation loop.");
                        break;
                    }

                    bool hasActualPayload = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                        ? DetermineReforgerPayloadPresence(commandKind, currentText, chunksCount)
                        : DetermineBattlEyePayloadPresence(commandKind, currentText, chunksCount);

                    if (hasActualPayload)
                    {
                        bool isLikelyMultiChunkFollowup = lastChunkSize >= 800;
                        int adaptiveQuietMs = isLikelyMultiChunkFollowup
                            ? Math.Max(400, (int)(PingMs * 2.0))
                            : Math.Max(80, (int)(PingMs * 0.6));

                        if (timeSinceLastChunk.TotalMilliseconds >= adaptiveQuietMs)
                        {
                            AppLogger.Trace($"[RconService:Aggregate] Adaptive quiet threshold reached ({timeSinceLastChunk.TotalMilliseconds:F1}ms >= {adaptiveQuietMs}ms, LastChunkSize={lastChunkSize} bytes, ChunksCount={chunksCount}). Halting aggregation.");
                            break;
                        }
                    }
                }
                finally
                {
                    _bufferSemaphore.Release();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            await _bufferSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var aggregated = _aggregatedBuffer.ToString();
                var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

                if (isTimeoutExceeded && _messageChunksCount == 0)
                {
                    var timeoutContext = new Dictionary<string, object?>
                    {
                        [ContextProtocol] = CurrentProtocol.ToString(),
                        ["command"] = command,
                        ["timeout_seconds"] = maxTimeout.TotalSeconds,
                        [ContextPingMs] = PingMs,
                        ["elapsed_ms"] = totalElapsed
                    };
                    AppLogger.Warn($"[RconService:Aggregate] Command '{command}' timed out waiting for initial server packet after {totalElapsed:F2}ms.", null, timeoutContext);

                    AppLogger.TrackEvent("rcon_packet_timeout_detected", new Dictionary<string, object>
                    {
                        [ProtocolMetricKey] = CurrentProtocol.ToString(),
                        ["command_head"] = command.Split(' ')[0],
                        ["chunks_received"] = _messageChunksCount,
                        ["last_chunk_bytes"] = _lastChunkSizeBytes,
                        [ContextPingMs] = PingMs,
                        [ElapsedMsMetricKey] = totalElapsed
                    });

                    ToastNotificationService.Instance.ShowWarning("Command Timed Out", $"No response packet received from server for '{command}'.");
                }

                CheckProtocolMismatch(aggregated);
                AppLogger.Debug($"[RconService:Aggregate] Command aggregation complete for '{AppLogger.SanitizeSensitiveData(command)}' in {totalElapsed:F2}ms: {aggregated.Length} chars across {_messageChunksCount} chunks.");
                return aggregated;
            }
            finally
            {
                _bufferSemaphore.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var errContext = new Dictionary<string, object?>
            {
                ["command"] = command,
                [ContextProtocol] = CurrentProtocol.ToString(),
                ["error"] = ex.Message,
                ["stack_trace"] = ex.StackTrace
            };
            AppLogger.Error($"[RconService:Aggregate] Critical error during command aggregation: {ex.Message}", ex, errContext);
            ToastNotificationService.Instance.ShowError("Command Execution Failure", $"Failed executing '{command}': {ex.Message}");
            return string.Empty;
        }
        finally
        {
            _commandExecutionLock.Release();
        }
    }

    private async Task ResetAggregateBufferAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await _bufferSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _aggregatedBuffer.Clear();
            _lastMessageChunkUtc = DateTime.UtcNow;
            _messageChunksCount = 0;
            _lastChunkSizeBytes = 0;

            while (_chunkArrivedSignal.CurrentCount > 0)
            {
                await _chunkArrivedSignal.WaitAsync(0, ct).ConfigureAwait(false);
            }
            AppLogger.Trace("[RconService:Buffer] Aggregate buffer cleared and arrival signal drained.");
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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        var startTimestamp = Stopwatch.GetTimestamp();
        AppLogger.Info("[RconService:Dispose] Disposing RconService instance and terminating background resources...");

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

        try
        {
            if (_inFlightPlayersTask is { IsCompleted: true })
            {
                _inFlightPlayersTask.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[RconService:Dispose] Task disposal notice: {ex.Message}");
        }
        _inFlightPlayersTask = null;

        _icmpPingSender.Dispose();
        _rconStateSemaphore.Dispose();
        _playersSemaphore.Dispose();
        _bufferSemaphore.Dispose();
        _commandExecutionLock.Dispose();
        _chunkArrivedSignal.Dispose();

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Debug($"[RconService:Dispose] RconService disposal complete in {elapsedMs:F2}ms.");
    }
}