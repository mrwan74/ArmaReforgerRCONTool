using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ReforgerRcon.Models;
using ReforgerRcon.Services.Parsers;

namespace ReforgerRcon.Services;

public sealed partial class RconService
{
    private async Task<(List<PlayerModel> Players, double ParseElapsedMs)> FetchReforgerPlayersInternalAsync(CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            [ContextProtocol] = "ReforgerBuiltIn",
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Debug("[RconService:GetPlayers] Dispatching query command 'players' (ReforgerBuiltIn)...", context);

        try
        {
            string rawResponse = await ExecuteCommandWithAggregateResponseAsync("players", TimeSpan.FromSeconds(4.0), cancellationToken).ConfigureAwait(false);
            var parseStart = Stopwatch.GetTimestamp();
            var currentPlayers = ReforgerResponseParser.ParsePlayers(rawResponse);
            var parseElapsed = Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;

            if (currentPlayers.Count == 0 && (string.IsNullOrWhiteSpace(rawResponse) || rawResponse.Contains("unknown command", StringComparison.OrdinalIgnoreCase)))
            {
                AppLogger.Debug("[RconService:GetPlayers] 'players' produced no player rows. Attempting fallback '#players'...", context);
                var fallbackStart = Stopwatch.GetTimestamp();
                string fallbackResponse = await ExecuteCommandWithAggregateResponseAsync("#players", TimeSpan.FromSeconds(4.0), cancellationToken).ConfigureAwait(false);
                var fallbackPlayers = ReforgerResponseParser.ParsePlayers(fallbackResponse);
                if (fallbackPlayers.Count > 0)
                {
                    currentPlayers = fallbackPlayers;
                    rawResponse = fallbackResponse;
                    parseElapsed = Stopwatch.GetElapsedTime(fallbackStart).TotalMilliseconds;
                }
            }

            var totalElapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["players_parsed"] = currentPlayers.Count;
            context["parse_ms"] = parseElapsed;
            context["total_ms"] = totalElapsed;
            context["response_length"] = rawResponse.Length;

            AppLogger.Info($"[RconService:GetPlayers] Reforger player query successful in {totalElapsed:F2}ms: {currentPlayers.Count} players parsed from {rawResponse.Length} chars.", context);
            return (currentPlayers, parseElapsed);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Trace("[RconService:GetPlayers] Reforger player query canceled by CancellationToken.", context);
            throw;
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            context["stack_trace"] = ex.StackTrace;
            AppLogger.Error("[RconService:GetPlayers] Exception during Reforger player query: " + ex.Message, ex, context);
            ToastNotificationService.Instance.ShowError("Player Query Error", $"Failed executing player list: {ex.Message}");
            return ([], 0);
        }
    }

    private async Task<List<BanModel>> GetReforgerBansInternalAsync(int maxPages, long startTimestamp, CancellationToken cancellationToken)
    {
        var context = new Dictionary<string, object?>
        {
            ["configured_max_pages"] = maxPages,
            [ContextProtocol] = "ReforgerBuiltIn",
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Debug($"[RconService:GetBans] Dispatching Reforger '#ban list' query (ConfiguredMaxPages={maxPages})...", context);

        try
        {
            string firstPageResponse = await ExecuteCommandWithAggregateResponseAsync("#ban list", TimeSpan.FromSeconds(3.5), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(firstPageResponse) || firstPageResponse.Contains("unknown command", StringComparison.OrdinalIgnoreCase))
            {
                firstPageResponse = await ExecuteCommandWithAggregateResponseAsync("bans", TimeSpan.FromSeconds(3.5), cancellationToken).ConfigureAwait(false);
            }

            var (totalBans, _, totalPages) = ReforgerResponseParser.ParseBanListPagination(firstPageResponse);
            var allParsedBans = ReforgerResponseParser.ParseBans(firstPageResponse);

            context["server_reported_total_bans"] = totalBans;
            context["server_reported_total_pages"] = totalPages;
            context["page_1_parsed_bans"] = allParsedBans.Count;

            AppLogger.Debug($"[RconService:GetBans] Page 1 result: {allParsedBans.Count} bans parsed (ReportedTotal={totalBans}, TotalPages={totalPages}).", context);

            if (maxPages == 1 || totalPages <= 1)
            {
                var singleElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                context["elapsed_ms"] = singleElapsed;
                AppLogger.Info($"[RconService:GetBans] Reforger ban retrieval finalized on Page 1 ({allParsedBans.Count}/{totalBans} bans) in {singleElapsed:F2}ms.", context);
                return allParsedBans;
            }

            int targetPages = maxPages > 1 ? Math.Min(totalPages, maxPages) : Math.Min(totalPages, 50);
            AppLogger.Info($"[RconService:GetBans] Reforger server reports {totalBans} bans across {totalPages} pages. Fetching pages 2 through {targetPages} (TargetPages={targetPages})...", context);

            for (int page = 2; page <= targetPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pageStart = Stopwatch.GetTimestamp();
                string pageResponse = await ExecuteCommandWithAggregateResponseAsync($"#ban list {page}", TimeSpan.FromSeconds(3.5), cancellationToken).ConfigureAwait(false);
                var pageBans = ReforgerResponseParser.ParseBans(pageResponse);
                int addedThisPage = 0;

                foreach (var b in pageBans.Where(b => !allParsedBans.Any(existing => existing.IdentityId.Equals(b.IdentityId, StringComparison.OrdinalIgnoreCase))))
                {
                    b.BanNumber = allParsedBans.Count + 1;
                    allParsedBans.Add(b);
                    addedThisPage++;
                }

                var pageElapsed = Stopwatch.GetElapsedTime(pageStart).TotalMilliseconds;
                AppLogger.Trace($"[RconService:GetBans] Page {page}/{targetPages} fetched in {pageElapsed:F2}ms (+{addedThisPage} new bans, TotalAccumulated={allParsedBans.Count}).");
            }

            var totalElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context["elapsed_ms"] = totalElapsed;
            context["total_retrieved"] = allParsedBans.Count;
            AppLogger.Info($"[RconService:GetBans] Reforger ban retrieval complete in {totalElapsed:F2}ms: {allParsedBans.Count}/{totalBans} bans retrieved across {targetPages} pages.", context);
            return allParsedBans;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Trace("[RconService:GetBans] Reforger ban query canceled.", context);
            throw;
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            context["stack_trace"] = ex.StackTrace;
            AppLogger.Error("[RconService:GetBans] Exception during Reforger ban retrieval: " + ex.Message, ex, context);
            ToastNotificationService.Instance.ShowError("Ban Retrieval Error", $"Failed querying ban list: {ex.Message}");
            return [];
        }
    }

    private static string BuildReforgerKickCommand(int playerId) => $"#kick {playerId}";

    private async Task<bool> KickReforgerPlayerAsync(
        PlayerModel player,
        string cmd,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        AppLogger.Debug($"[RconService:Moderation] Sending Reforger kick command: '{cmd}' for Player #{player.Id} ({player.Name})...", context);
        try
        {
            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
            bool success = VerifyModerationSuccess(response, [TokenKicked, TokenAdminKick]);

            context[ResponseMetricKey] = response;
            context[VerifiedSuccessMetricKey] = success;

            if (success)
            {
                await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int removed = _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, player));
                    AppLogger.Trace($"[RconService:Moderation] Removed {removed} matching instance(s) of '{player.Name}' from live tracking.");
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = !string.IsNullOrWhiteSpace(player.ReforgerUid) ? player.ReforgerUid : player.Uid;
                _ = Task.Run(() => PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol), CancellationToken.None);
            }
            else
            {
                AppLogger.Warn($"[RconService:Moderation] Server rejected kick command for '{player.Name}': Response='{response}'", null, context);
                ToastNotificationService.Instance.ShowError("Kick Rejected", $"Server rejected kick for {player.Name}: {response}", cmd);
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsMetricKey] = elapsedMs;
            AppLogger.Info($"[RconService:Moderation] Reforger kick sequence completed in {elapsedMs:F2}ms. Success={success}", context);
            return success;
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[RconService:Moderation] Exception executing kick for {player.Name}: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Kick Error", $"Failed kicking {player.Name}: {ex.Message}");
            return false;
        }
    }

    private static string BuildReforgerBanCommand(int playerId, long durationSeconds, string reason)
    {
        return string.IsNullOrEmpty(reason)
            ? $"#ban create {playerId} {durationSeconds}"
            : $"#ban create {playerId} {durationSeconds} {reason}";
    }

    private async Task<bool> BanReforgerPlayerAsync(
        PlayerModel player,
        long durationSeconds,
        string cleanReason,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        string cmd = BuildReforgerBanCommand(player.Id, durationSeconds, cleanReason);
        context[CommandMetricKey] = cmd;
        AppLogger.Debug($"[RconService:Moderation] Executing Reforger ban command: '{cmd}'...", context);

        try
        {
            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            bool success = VerifyModerationSuccess(response, [TokenBanCreated, TokenBanned]);
            context[ResponseMetricKey] = response;
            context[VerifiedSuccessMetricKey] = success;
            context[ElapsedMsMetricKey] = elapsedMs;

            if (success)
            {
                await _playersSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    int removed = _lastKnownPlayers.RemoveAll(p => IsSamePlayer(p, player));
                    AppLogger.Trace($"[RconService:Moderation] Removed {removed} live instance(s) for banned player '{player.Name}'.");
                }
                finally
                {
                    _playersSemaphore.Release();
                }

                var identifier = !string.IsNullOrWhiteSpace(player.ReforgerUid) ? player.ReforgerUid : player.Uid;
                _ = Task.Run(() => PlayerDatabaseStorageService.SetPlayerOfflineAsync(identifier, CurrentProtocol), CancellationToken.None);
            }
            else
            {
                AppLogger.Warn($"[RconService:Moderation] Server rejected ban command for '{player.Name}': Response='{response}'", null, context);
                ToastNotificationService.Instance.ShowError("Ban Rejected", $"Server rejected ban command for {player.Name}: {response}", cmd);
            }

            AppLogger.Info($"[RconService:Moderation] Reforger ban execution complete in {elapsedMs:F2}ms: Success={success}", context);
            return success;
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[RconService:Moderation] Exception executing ban for {player.Name}: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Ban Execution Error", $"Failed executing ban command for {player.Name}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> OfflineBanReforgerAsync(
        string identity,
        long durationSeconds,
        string cleanReason,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        string cmd = string.IsNullOrEmpty(cleanReason)
            ? $"#ban create {identity} {durationSeconds}"
            : $"#ban create {identity} {durationSeconds} {cleanReason}";

        context[CommandMetricKey] = cmd;
        AppLogger.Debug($"[RconService:Moderation] Executing Reforger offline ban: '{cmd}'...", context);

        try
        {
            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            bool success = VerifyModerationSuccess(response, [TokenBanCreated, TokenBanned]);
            context[ResponseMetricKey] = response;
            context[VerifiedSuccessMetricKey] = success;
            context[ElapsedMsMetricKey] = elapsedMs;

            if (!success)
            {
                AppLogger.Warn($"[RconService:Moderation] Server rejected offline ban for '{identity}': Response='{response}'", null, context);
                ToastNotificationService.Instance.ShowError("Offline Ban Rejected", $"Server rejected ban command: {response}", cmd);
            }
            AppLogger.Info($"[RconService:Moderation] Reforger offline ban complete in {elapsedMs:F2}ms: Success={success}", context);
            return success;
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[RconService:Moderation] Exception executing offline ban for {identity}: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Offline Ban Error", $"Failed executing ban for {identity}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> RemoveReforgerBanAsync(
        BanModel ban,
        string cmd,
        Dictionary<string, object?> context,
        long startTimestamp,
        CancellationToken cancellationToken)
    {
        AppLogger.Debug($"[RconService:Moderation] Executing Reforger unban command: '{cmd}'...", context);
        try
        {
            string response = await ExecuteCommandWithAggregateResponseAsync(cmd, TimeSpan.FromSeconds(2.5), cancellationToken).ConfigureAwait(false);
            var reforgerElapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            bool success = VerifyModerationSuccess(response, [TokenBanRemoved]);
            context[ResponseMetricKey] = response;
            context[VerifiedSuccessMetricKey] = success;
            context[ElapsedMsMetricKey] = reforgerElapsed;

            if (!success)
            {
                AppLogger.Warn($"[RconService:Moderation] Server rejected ban removal for '{ban.IdentityId}': Response='{response}'", null, context);
                ToastNotificationService.Instance.ShowError("Unban Rejected", $"Server rejected ban removal: {response}", cmd);
            }
            AppLogger.Info($"[RconService:Moderation] Reforger ban removal complete in {reforgerElapsed:F2}ms for '{ban.IdentityId}': Success={success}", context);
            return success;
        }
        catch (Exception ex)
        {
            context[ContextErrorMessage] = ex.Message;
            AppLogger.Error($"[RconService:Moderation] Exception removing ban {ban.IdentityId}: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Unban Error", $"Failed removing ban for {ban.IdentityId}: {ex.Message}");
            return false;
        }
    }

    private async Task SendReforgerLogoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            AppLogger.Trace("[RconService:Disconnect] Sending '@logout' packet to Reforger server...");
            _client?.SendCommand("@logout", log: false);
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            AppLogger.Trace("[RconService:Disconnect] @logout packet transmitted.");
        }
        catch (SocketException sockEx)
        {
            AppLogger.Debug($"[RconService:Disconnect] SocketException on @logout (harmless during shutdown): {sockEx.SocketErrorCode}");
        }
        catch (ObjectDisposedException dispEx)
        {
            AppLogger.Debug($"[RconService:Disconnect] Socket already disposed on @logout: {dispEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[RconService:Disconnect] Non-critical warning during @logout: {ex.Message}", ex);
        }
    }

    private static RconCommandKind ClassifyReforgerCommand(string command)
    {
        if (command.StartsWith("#players", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("players", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.PlayerList;
        }

        if (command.StartsWith("#ban list", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("bans", StringComparison.OrdinalIgnoreCase) ||
            command.Equals("ban list", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.BanList;
        }

        if (command.StartsWith("#kick", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("kick", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.Kick;
        }

        if (command.StartsWith("#ban create", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("ban", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("addBan", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.BanCreate;
        }

        if (command.StartsWith("#ban remove", StringComparison.OrdinalIgnoreCase) ||
            command.StartsWith("removeBan", StringComparison.OrdinalIgnoreCase))
        {
            return RconCommandKind.BanRemove;
        }

        return RconCommandKind.Other;
    }

    private static bool CheckReforgerCommandTerminalTokens(
            RconCommandKind commandKind,
            string currentText,
            int chunksCount,
            int lastChunkSize) =>
            commandKind switch
            {
                RconCommandKind.PlayerList =>
                    ((currentText.Contains(PlayersOnServerToken, StringComparison.OrdinalIgnoreCase) ||
                      currentText.Contains(';')) &&
                     lastChunkSize < 900 &&
                     !IsOnlyProcessingCommandHeader(currentText)),

                RconCommandKind.BanList =>
                    currentText.Contains("Server has no bans to list.", StringComparison.OrdinalIgnoreCase) ||
                    (currentText.Contains("Total bans:", StringComparison.OrdinalIgnoreCase) && lastChunkSize < 900) ||
                    (currentText.Contains("Identity Id | Banned name", StringComparison.OrdinalIgnoreCase) && lastChunkSize < 900),

                RconCommandKind.Kick =>
                    currentText.Contains(TokenKicked, StringComparison.OrdinalIgnoreCase) ||
                    currentText.Contains(TokenNotFound, StringComparison.OrdinalIgnoreCase) ||
                    currentText.Contains("failed", StringComparison.OrdinalIgnoreCase),

                RconCommandKind.BanCreate =>
                    currentText.Contains(TokenBanCreated, StringComparison.OrdinalIgnoreCase) ||
                    currentText.Contains(TokenBanned, StringComparison.OrdinalIgnoreCase) ||
                    currentText.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                    currentText.Contains(TokenNotFound, StringComparison.OrdinalIgnoreCase),

                RconCommandKind.BanRemove =>
                    currentText.Contains(TokenBanRemoved, StringComparison.OrdinalIgnoreCase) ||
                    currentText.Contains(TokenNotFound, StringComparison.OrdinalIgnoreCase),

                _ => chunksCount >= 1 && lastChunkSize < 900 && !IsOnlyProcessingCommandHeader(currentText)
            };

    private static bool DetermineReforgerPayloadPresence(RconCommandKind commandKind, string currentText, int chunksCount) =>
        commandKind switch
        {
            RconCommandKind.PlayerList =>
                currentText.Contains(PlayersOnServerToken, StringComparison.OrdinalIgnoreCase) ||
                currentText.Contains(';') ||
                currentText.Contains('|'),

            RconCommandKind.BanList =>
                currentText.Contains("Server has no bans to list.", StringComparison.OrdinalIgnoreCase) ||
                currentText.Contains("Total bans:", StringComparison.OrdinalIgnoreCase) ||
                currentText.Contains('|'),

            _ => chunksCount > 0 && !IsOnlyProcessingCommandHeader(currentText)
        };

    private static bool IsOnlyProcessingCommandHeader(string text)
    {
        var trimmed = text.Trim();
        return trimmed.StartsWith("Processing Command:", StringComparison.OrdinalIgnoreCase) &&
               !trimmed.Contains('\n') &&
               !trimmed.Contains('\r');
    }

    public Task RestartServerAsync(CancellationToken cancellationToken = default)
    {
        AppLogger.Info("[RconService:ServerControl] Dispatching #restart command...");
        return SendCommandAsync("#restart", cancellationToken);
    }

    public Task ShutdownServerAsync(CancellationToken cancellationToken = default)
    {
        AppLogger.Info("[RconService:ServerControl] Dispatching #shutdown command...");
        return SendCommandAsync("#shutdown", cancellationToken);
    }

    public Task SendGlobalMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        AppLogger.Info($"[RconService:Chat] Dispatching global chat message ({message.Length} chars)...");
        return SendCommandAsync($"#say -1 {message}", cancellationToken);
    }

    public Task SendAnnouncementAsync(string title, string message, CancellationToken cancellationToken = default)
    {
        AppLogger.Info($"[RconService:Chat] Dispatching announcement banner: Title='{title}', MessageLength={message.Length}...");
        return SendCommandAsync($"#say -1 [ANNOUNCEMENT: {title}] {message}", cancellationToken);
    }
}