using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
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

public class RconService : IRconService
{
    private const string ProtocolMetricKey = "protocol";

    private BattlEyeClient? _client;
    private ServerProfile? _currentProfile;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pendingCommands = new();
    private readonly StringBuilder _aggregatedBuffer = new();
    private readonly Lock _bufferLock = new();

    public RconProtocol CurrentProtocol => _currentProfile?.Protocol ?? RconProtocol.ReforgerBuiltIn;
    public bool IsConnected => _client is { Connected: true };
    public int PingMs => _client?.LastPingMs ?? 0;
    public DateTime LastPacketTime { get; private set; } = DateTime.UtcNow;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<PlayerModel>? PlayerJoined;
    public event EventHandler<PlayerModel>? PlayerLeft;

    private List<PlayerModel> _lastKnownPlayers = [];

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
                var addresses = await Dns.GetHostAddressesAsync(profile.ServerIp);
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

            _client.BattlEyeDisconnected += args =>
            {
                AppLogger.Warn($"[RconService] Disconnected event received: {args.DisconnectionType} ({args.Message})");
                OutputReceived?.Invoke(this, $"[SYSTEM] Disconnected: {args.Message}");
            };

            _client.BattlEyeMessageReceived += OnBattlEyeMessageReceived;

            _ = _client.ConnectAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            cts.Token.Register(() => connectTcs.TrySetResult(false));

            bool success = await connectTcs.Task;
            if (success)
            {
                LastPacketTime = DateTime.UtcNow;
                OutputReceived?.Invoke(this, $"[SYSTEM] Connected successfully to {profile.ServerIp}:{profile.Port}");
                AppLogger.Info($"[RconService] Live connection verified for {profile.ServerIp}:{profile.Port}");
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
        AppLogger.Info("[RconService] Disconnecting client...");
        await PlayerDatabaseStorageService.SetAllOfflineAsync();

        _client?.Dispose();
        _client = null;

        OutputReceived?.Invoke(this, "[SYSTEM] Disconnected from server.");
    }

    private void OnBattlEyeMessageReceived(BattlEyeMessageEventArgs args)
    {
        LastPacketTime = DateTime.UtcNow;
        var message = args.Message;

        lock (_bufferLock)
        {
            _aggregatedBuffer.AppendLine(message);
        }

        if (args.Id != 256 && _pendingCommands.TryRemove(args.Id, out var tcs))
        {
            tcs.TrySetResult(message);
        }

        OutputReceived?.Invoke(this, $"[RCON IN] {message}");
    }

    public async Task<List<PlayerModel>> GetPlayersAsync()
    {
        if (_client is not { Connected: true }) return [];

        var transaction = SentrySdk.StartTransaction("GetPlayers", "rcon.query.players");
        using var op = Operation.Begin("Query active players list ({Protocol})", CurrentProtocol);
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#players" : "players";
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromMilliseconds(1200));

            List<PlayerModel> currentPlayers = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParsePlayers(rawResponse)
                : BattlEyeResponseParser.ParsePlayers(rawResponse);

            await PlayerDatabaseStorageService.RecordSeenPlayersAsync(currentPlayers);

            var currentKeys = currentPlayers.Select(p => p.Uid).ToHashSet();
            var lastKeys = _lastKnownPlayers.Select(p => p.Uid).ToHashSet();

            foreach (var p in currentPlayers.Where(p => !lastKeys.Contains(p.Uid)))
            {
                PlayerJoined?.Invoke(this, p);
            }

            foreach (var p in _lastKnownPlayers.Where(p => !currentKeys.Contains(p.Uid)))
            {
                PlayerLeft?.Invoke(this, p);
            }

            _lastKnownPlayers = [.. currentPlayers];
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
            AppLogger.Debug("[RconService] GetPlayers query cancelled.");
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

    public async Task<List<BanModel>> GetBansAsync()
    {
        if (_client is not { Connected: true }) return [];

        var transaction = SentrySdk.StartTransaction("GetBans", "rcon.query.bans");
        using var op = Operation.Begin("Query server bans ({Protocol})", CurrentProtocol);
        try
        {
            string command = CurrentProtocol == RconProtocol.ReforgerBuiltIn ? "#ban list" : "bans";
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync(command, TimeSpan.FromMilliseconds(1200));

            var bans = CurrentProtocol == RconProtocol.ReforgerBuiltIn
                ? ReforgerResponseParser.ParseBans(rawResponse)
                : BattlEyeResponseParser.ParseBans(rawResponse);

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
            AppLogger.Debug("[RconService] GetBans query cancelled.");
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

    public Task<List<DatabasePlayerModel>> GetDatabasePlayersAsync() => PlayerDatabaseStorageService.GetAllAsync();

    public Task KickPlayerAsync(PlayerModel player, string reason)
    {
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#kick {player.Id} {reason}"
            : $"kick {player.Id} {reason}";

        SentrySdk.Metrics.EmitCounter("player_kicks", 1);
        return SendCommandAsync(cmd);
    }

    public Task BanPlayerAsync(PlayerModel player, long durationSeconds, string reason)
    {
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban create {player.Uid} {durationSeconds} {reason}"
            : $"addBan {player.Guid} {beMinutes} {reason}";

        SentrySdk.Metrics.EmitCounter("player_bans", 1,
        [
            new KeyValuePair<string, object>("permanent", (durationSeconds <= 0).ToString(CultureInfo.InvariantCulture))
        ]);
        return SendCommandAsync(cmd);
    }

    public Task OfflineBanAsync(string identity, long durationSeconds, string reason, bool isIp)
    {
        long beMinutes = durationSeconds <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(durationSeconds / 60.0));
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban create {identity} {durationSeconds} {reason}"
            : $"addBan {identity} {beMinutes} {reason}";

        SentrySdk.Metrics.EmitCounter("offline_bans", 1);
        return SendCommandAsync(cmd);
    }

    public Task RemoveBanAsync(BanModel ban)
    {
        string cmd = CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban remove {ban.IdentityId}"
            : $"removeBan {ban.BanNumber}";

        SentrySdk.Metrics.EmitCounter("ban_removals", 1);
        return SendCommandAsync(cmd);
    }

    public Task SendCommandAsync(string rawCommand)
    {
        if (_client is not { Connected: true })
        {
            OutputReceived?.Invoke(this, $"[ERROR] Cannot send '{rawCommand}' - Client is disconnected.");
            return Task.CompletedTask;
        }

        SentrySdk.Metrics.EmitCounter("rcon_commands_dispatched", 1,
        [
            new KeyValuePair<string, object>(ProtocolMetricKey, CurrentProtocol.ToString())
        ]);

        OutputReceived?.Invoke(this, $"[RCON OUT] {rawCommand}");
        _client.SendCommand(rawCommand);
        return Task.CompletedTask;
    }

    public Task RestartServerAsync() => SendCommandAsync("#restart");
    public Task ShutdownServerAsync() => SendCommandAsync("#shutdown");
    public Task SendGlobalMessageAsync(string message) => SendCommandAsync($"#say -1 {message}");
    public Task SendAnnouncementAsync(string title, string message) => SendCommandAsync($"#say -1 [ANNOUNCEMENT: {title}] {message}");
    public Task UpdatePlayerCommentAsync(string uid, string comment) => PlayerDatabaseStorageService.UpdateCommentAsync(uid, comment);
    public Task ClearDatabaseAsync() => PlayerDatabaseStorageService.ClearAsync();

    private async Task<string> ExecuteCommandWithAggregateResponseAsync(string command, TimeSpan waitDuration)
    {
        if (_client == null) return string.Empty;

        lock (_bufferLock)
        {
            _aggregatedBuffer.Clear();
        }

        OutputReceived?.Invoke(this, $"[RCON OUT] {command}");
        string directResponse = await _client.SendCommandWithResponseAsync(command, waitDuration);

        if (CurrentProtocol == RconProtocol.BattlEye && !string.IsNullOrWhiteSpace(directResponse))
        {
            return directResponse;
        }

        await Task.Delay(waitDuration);

        lock (_bufferLock)
        {
            return _aggregatedBuffer.ToString();
        }
    }
}