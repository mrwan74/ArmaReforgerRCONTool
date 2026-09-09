using ReforgerRcon.BattleNET;
using ReforgerRcon.Models;
using ReforgerRcon.Services.Parsers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public sealed partial class RconService
{
    private static string ResolveBattlEyeIdentifier(PlayerModel player)
    {
        if (!string.IsNullOrWhiteSpace(player.BattlEyeGuid))
        {
            return player.BattlEyeGuid;
        }

        if (!string.IsNullOrWhiteSpace(player.Guid))
        {
            return player.Guid;
        }

        return player.Uid;
    }

    private async Task<(List<PlayerModel> Players, double ParseElapsedMs)> FetchBattlEyePlayersInternalAsync(CancellationToken cancellationToken)
    {
        AppLogger.Debug("[RconService:GetPlayers] Dispatching query command 'players' (BattlEye)...");
        string rawResponse = await ExecuteCommandWithAggregateResponseAsync("players", TimeSpan.FromSeconds(4.0), cancellationToken).ConfigureAwait(false);
        var parseStart = Stopwatch.GetTimestamp();
        var currentPlayers = BattlEyeResponseParser.ParsePlayers(rawResponse);
        var parseElapsed = Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;
        return (currentPlayers, parseElapsed);
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
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync("admins", TimeSpan.FromSeconds(1.8), cancellationToken).ConfigureAwait(false);
            var admins = BattlEyeResponseParser.ParseAdmins(rawResponse);
            var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[RconService:GetAdmins] GetAdminsAsync complete in {totalElapsed:F2}ms (Count={admins.Count}).");
            return admins;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SocketException sockEx)
        {
            AppLogger.Error($"[RconService:GetAdmins] Socket error querying admins: {sockEx.SocketErrorCode}", sockEx);
            ToastNotificationService.Instance.ShowError("Network Error", $"Socket error querying admin list: {sockEx.SocketErrorCode}");
            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[RconService:GetAdmins] Failed querying connected admins: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Query Error", $"Failed querying connected admin sessions: {ex.Message}");
            return [];
        }
    }

    private async Task<List<BanModel>> GetBattlEyeBansInternalAsync(long startTimestamp, CancellationToken cancellationToken)
    {
        AppLogger.Debug("[RconService:GetBans] Dispatching BattlEye 'bans' query...");
        string rawResponse = await ExecuteCommandWithAggregateResponseAsync("bans", TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
        var parseStart = Stopwatch.GetTimestamp();
        var bans = BattlEyeResponseParser.ParseBans(rawResponse);
        var parseElapsed = Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;
        var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Debug($"[RconService:GetBans] BattlEye ban retrieval complete in {totalElapsed:F2}ms (Parse={parseElapsed:F2}ms, Count={bans.Count}).");
        return bans;
    }

    private static string BuildBattlEyeKickCommand(int playerId, string reason) =>
        string.IsNullOrEmpty(reason) ? $"kick {playerId}" : $"kick {playerId} {reason}";

    private async Task<bool> KickBattlEyePlayerAsync(
        PlayerModel player,
        string cmd,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(2.0), cancellationToken).ConfigureAwait(false);
        bool success = response.Length == 0 || VerifyModerationSuccess(response, [TokenKicked, TokenAdminKick]);

        context[ResponseMetricKey] = response;
        context[VerifiedSuccessMetricKey] = success;

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

            var identifier = ResolveBattlEyeIdentifier(player);
            _ = Task.Run(() => PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol), CancellationToken.None);
        }
        else
        {
            ToastNotificationService.Instance.ShowError("Kick Rejected", $"Server rejected kick for {player.Name}: {response}", cmd);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        context[ElapsedMsMetricKey] = elapsedMs;
        AppLogger.Info($"[RconService:Moderation] BattlEye Kick complete in {elapsedMs:F2}ms. Success={success}", context);
        return success;
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

    private async Task<bool> BanBattlEyePlayerAsync(
        PlayerModel player,
        long beMinutes,
        string cleanReason,
        bool banIp,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        string beCmd = BuildBattlEyeBanCommand(player.Id, beMinutes, cleanReason);
        context[CommandMetricKey] = beCmd;
        string beResponse = await ExecuteCommandWithAggregateResponseAsync(beCmd, TimeSpan.FromSeconds(2.0), cancellationToken).ConfigureAwait(false);
        bool banSuccess = beResponse.Length == 0 || VerifyModerationSuccess(beResponse, [TokenAdminBan, "kicked by BattlEye", TokenBanned]);
        context[ResponseMetricKey] = beResponse;
        context[VerifiedSuccessMetricKey] = banSuccess;

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

            var identifier = ResolveBattlEyeIdentifier(player);
            _ = Task.Run(() => PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol), CancellationToken.None);
        }
        else
        {
            ToastNotificationService.Instance.ShowError("Ban Rejected", $"BattlEye server rejected ban command: {beResponse}", beCmd);
        }

        if (banIp && !string.IsNullOrWhiteSpace(player.Ip) && !player.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(player.Ip, out _))
        {
            string addBanIpCmd = BuildBattlEyeAddBanIpCommand(player.Ip, beMinutes, cleanReason);
            AppLogger.Info($"[RconService:Moderation] Executing IP ban for '{player.Name}' at {player.Ip}: '{addBanIpCmd}'...");
            await SendCommandAsync(addBanIpCmd, cancellationToken).ConfigureAwait(false);
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync("writeBans", cancellationToken).ConfigureAwait(false);
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            await SendCommandAsync("loadBans", cancellationToken).ConfigureAwait(false);
        }

        var totalDuration = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        context[ElapsedMsMetricKey] = totalDuration;
        AppLogger.Info($"[RconService:Moderation] BattlEye ban sequence complete in {totalDuration:F2}ms for '{player.Name}'. PrimarySuccess={banSuccess}", context);
        return banSuccess;
    }

    private async Task<bool> OfflineBanBattlEyeAsync(
        string identity,
        long beMinutes,
        string cleanReason,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        string beAddCmd = string.IsNullOrEmpty(cleanReason)
            ? $"addBan {identity} {beMinutes}"
            : $"addBan {identity} {beMinutes} {cleanReason}";

        context[CommandMetricKey] = beAddCmd;
        await SendCommandAsync(beAddCmd, cancellationToken).ConfigureAwait(false);
        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("writeBans", cancellationToken).ConfigureAwait(false);
        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("loadBans", cancellationToken).ConfigureAwait(false);
        var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        context[ElapsedMsMetricKey] = totalElapsed;
        AppLogger.Info($"[RconService:Moderation] BattlEye offline ban complete in {totalElapsed:F2}ms for '{identity}'.", context);
        return true;
    }

    private async Task<bool> RemoveBattlEyeBanAsync(
        BanModel ban,
        string cmd,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(2.0), cancellationToken).ConfigureAwait(false);
        await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        await SendCommandAsync("writeBans", cancellationToken).ConfigureAwait(false);
        var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        context[ElapsedMsMetricKey] = totalElapsed;
        AppLogger.Info($"[RconService:Moderation] BattlEye removeBan & writeBans complete in {totalElapsed:F2}ms for Ban #{ban.BanNumber}.", context);
        return true;
    }

    private async Task<string?> ExecuteBattlEyeDirectResponseAsync(
        BattlEyeClient client,
        string command,
        TimeSpan maxTimeout,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        string? directResponse = await client.SendCommandWithResponseAsync(command, maxTimeout, cancellationToken).ConfigureAwait(false);
        if (directResponse?.StartsWith('\0') == false)
        {
            if (directResponse.Length > 0)
            {
                CheckProtocolMismatch(directResponse);
            }
            var directElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[RconService:Aggregate] Direct response received for '{AppLogger.SanitizeSensitiveData(command)}' in {directElapsed:F2}ms ({directResponse.Length} chars).");
            return directResponse;
        }

        return null;
    }

    private static RconCommandKind ClassifyBattlEyeCommand(string command)
    {
        if (command.Equals("players", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.PlayerList;
        }

        if (command.Equals("bans", StringComparison.OrdinalIgnoreCase) || command.Equals("ban list", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.BanList;
        }

        if (command.StartsWith("kick", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.Kick;
        }

        if (command.StartsWith("ban ", StringComparison.OrdinalIgnoreCase) || command.StartsWith("addBan", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.BanCreate;
        }

        if (command.StartsWith("removeBan", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.BanRemove;
        }

        if (command.StartsWith("admins", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.Admins;
        }

        return RconCommandKind.Other;
    }

    private static bool CheckBattlEyeCommandTerminalTokens(RconCommandKind commandKind, string currentText, int chunksCount) =>
        commandKind switch
        {
            RconCommandKind.PlayerList => currentText.Contains("players in total", StringComparison.OrdinalIgnoreCase),
            RconCommandKind.BanList => currentText.Contains("IP Address] [Minutes left] [Reason]", StringComparison.OrdinalIgnoreCase),
            RconCommandKind.Admins => currentText.Contains("Connected RCon admins:", StringComparison.OrdinalIgnoreCase),
            RconCommandKind.Kick => currentText.Contains(TokenAdminKick, StringComparison.OrdinalIgnoreCase) ||
                                   currentText.Contains(TokenKicked, StringComparison.OrdinalIgnoreCase) ||
                                   currentText.Contains("not found", StringComparison.OrdinalIgnoreCase),
            RconCommandKind.BanCreate => currentText.Contains(TokenAdminBan, StringComparison.OrdinalIgnoreCase) ||
                                         currentText.Contains(TokenBanned, StringComparison.OrdinalIgnoreCase) ||
                                         currentText.Contains("invalid", StringComparison.OrdinalIgnoreCase),
            RconCommandKind.BanRemove => chunksCount > 0,
            _ => false
        };

    private static bool DetermineBattlEyePayloadPresence(RconCommandKind commandKind, string currentText, int chunksCount) =>
        commandKind switch
        {
            RconCommandKind.PlayerList => currentText.Contains("players in total", StringComparison.OrdinalIgnoreCase),
            RconCommandKind.BanList => currentText.Contains("IP Bans:", StringComparison.OrdinalIgnoreCase),
            _ => chunksCount > 0
        };

    private void ProcessLiveStreamEvent(string message)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            if (CurrentProtocol != RconProtocol.BattlEye)
            {
                return;
            }

            var disconnMatch = BattlEyeResponseParser.PlayerDisconnectedStreamRegex().Match(message);
            if (disconnMatch.Success && int.TryParse(disconnMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int discId))
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

                var identifier = ResolveBattlEyeIdentifier(matched);
                _ = Task.Run(() => PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol), CancellationToken.None);

                AppLogger.Info($"[RconService:StreamEvent] Player Disconnected stream matched: '{name}' (ID: #{discId}). Remaining online: {_lastKnownPlayers.Count}");
                RaisePlayerLeft(matched);
                return;
            }

            var guidMatch = BattlEyeResponseParser.PlayerGuidStreamRegex().Match(message);
            if (guidMatch.Success && int.TryParse(guidMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int guidPlayerId))
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
                        existing.Uid = guid;
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
                            Uid = guid,
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
                _ = Task.Run(() => PlayerDatabaseStorageService.RecordSeenPlayersAsync([matched], CurrentProtocol), CancellationToken.None);

                if (isNew)
                {
                    RaisePlayerJoined(matched);
                }
                return;
            }

            var banMatch = BattlEyeResponseParser.PlayerBannedStreamRegex().Match(message);
            if (banMatch.Success && int.TryParse(banMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int banId))
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
                        ?? new PlayerModel { Id = banId, Name = name, Uid = guid, Guid = guid, BattlEyeGuid = guid, Ping = 0 };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = ResolveBattlEyeIdentifier(matched);
                _ = Task.Run(() => PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol), CancellationToken.None);

                AppLogger.Info($"[RconService:StreamEvent] Player Banned stream matched: '{name}' (ID: #{banId}, GUID: '{guid}', Reason: '{reason}').");
                RaisePlayerLeft(matched);
                PlayerBannedStream?.Invoke(this, (name, banId, guid, reason));
                return;
            }

            var kickMatch = BattlEyeResponseParser.PlayerKickedStreamRegex().Match(message);
            if (kickMatch.Success && int.TryParse(kickMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kickId))
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
                        ?? new PlayerModel { Id = kickId, Name = name, Uid = guid, Guid = guid, BattlEyeGuid = guid, Ping = 0 };

                    _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, matched));
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = ResolveBattlEyeIdentifier(matched);
                _ = Task.Run(() => PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol), CancellationToken.None);

                AppLogger.Info($"[RconService:StreamEvent] Player Kicked stream matched: '{name}' (ID: #{kickId}, GUID: '{guid}', Reason: '{reason}').");
                RaisePlayerLeft(matched);
                PlayerKickedStream?.Invoke(this, (name, kickId, reason));
                return;
            }

            var connMatch = BattlEyeResponseParser.PlayerConnectedStreamRegex().Match(message);
            if (connMatch.Success && int.TryParse(connMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int connId))
            {
                var name = connMatch.Groups[2].Value.Trim();
                var ip = connMatch.Groups[3].Value.Trim();
                int port = int.TryParse(connMatch.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 2304;
                var geo = GeoIpService.GetLocation(ip);

                var newPlayer = new PlayerModel
                {
                    Id = connId,
                    Name = name,
                    Uid = $"init_{connId}",
                    Guid = "Initializing...",
                    BattlEyeGuid = string.Empty,
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
                _ = Task.Run(() => PlayerDatabaseStorageService.RecordSeenPlayersAsync([newPlayer], CurrentProtocol), CancellationToken.None);
                RaisePlayerJoined(newPlayer);
                return;
            }

            var adminMatch = BattlEyeResponseParser.AdminConnectedStreamRegex().Match(message);
            if (adminMatch.Success && int.TryParse(adminMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int adminId))
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
}