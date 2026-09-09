using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services.Parsers;

public static partial class ReforgerResponseParser
{
    private const string DefaultServerBanReason = "Server Ban";
    private const string DefaultUnknownRegion = "Unknown Region";

    [GeneratedRegex(@"^\s*(\d+)\s*;\s*([a-fA-F0-9\-]{36}|[a-zA-Z0-9_\-]+)\s*;\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex PlayerRowRegex();

    [GeneratedRegex(@"(?<=[^\r\n])\s*(\d+\s*;\s*[a-fA-F0-9\-]{36})", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex MissingNewlinePlayerSplitRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?([a-fA-F0-9\-]{36}|[a-zA-Z0-9_\-]+)\s*(?:[\|\;])\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex BanRowWithSeparatorRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?([a-fA-F0-9\-]{36})\s*$", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex BanRowIdentityOnlyRegex();

    [GeneratedRegex(@"Total bans:\s*(\d+)\s*\|\s*Page:\s*(\d+)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex TotalBansPaginationRegex();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex AnsiEscapeRegex();

    [GeneratedRegex(@"RCon\s+admin\s+#\d+\s+\([^)]+\)\s+logged\s+in", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex BattlEyeAdminLoginRegex();

    [GeneratedRegex(@"\(\d+\s+players\s+in\s+total\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex TotalPlayersCountRegex();

    public static (int TotalBans, int CurrentPage, int TotalPages) ParseBanListPagination(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse)) return (0, 1, 1);
        try
        {
            var match = TotalBansPaginationRegex().Match(rawResponse);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int totalBans) &&
                int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int curPage) &&
                int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int totalPages))
            {
                AppLogger.Debug($"[ReforgerResponseParser:Pagination] Extracted pagination: TotalBans={totalBans}, CurrentPage={curPage}, TotalPages={totalPages}.");
                return (totalBans, curPage, Math.Max(1, totalPages));
            }
        }
        catch (RegexMatchTimeoutException timeoutEx)
        {
            AppLogger.Warn($"[ReforgerResponseParser:PaginationTimeout] Regex timeout parsing ban list pagination: {timeoutEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser:Pagination] Error parsing ban list pagination: {ex.Message}", ex);
        }

        return (0, 1, 1);
    }

    public static RconProtocol? DetectProtocol(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        try
        {
            if (text.Contains("Logged In! Client ID:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Processing Command:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Players on server: [Player#]", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("[Player#] ; [Player UID] ; [Player Name]", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("- Identity Id | Banned name", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Total bans:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("Help for ban command", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("#ban create", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("#ban list", StringComparison.OrdinalIgnoreCase))
            {
                return RconProtocol.ReforgerBuiltIn;
            }

            if (BattlEyeAdminLoginRegex().IsMatch(text) ||
                text.Contains("Connected RCon admins:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("List of available commands:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("[#] [IP Address]:[Port] [Ping] [GUID]", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("GUID Bans:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("IP Bans:", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("(0 players in total)", StringComparison.OrdinalIgnoreCase) ||
                TotalPlayersCountRegex().IsMatch(text))
            {
                return RconProtocol.BattlEye;
            }
        }
        catch (RegexMatchTimeoutException regexTimeout)
        {
            AppLogger.Warn($"[ReforgerResponseParser:DetectProtocol] Regex timeout evaluating protocol signatures: {regexTimeout.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser:DetectProtocol] Unexpected error evaluating protocol signatures: {ex.Message}", ex);
        }

        return null;
    }

    public static bool HasReforgerSignature(string? text) => DetectProtocol(text) == RconProtocol.ReforgerBuiltIn;

    public static bool HasBattlEyeSignature(string? text) => DetectProtocol(text) == RconProtocol.BattlEye;

    public static string SanitizeText(string? raw, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var trimmed = raw.Trim();

        bool hasSpecial = false;
        for (int i = 0; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            if (c < 32 || c > 126)
            {
                hasSpecial = true;
                break;
            }
        }

        if (!hasSpecial)
        {
            if (trimmed.Length > 120) trimmed = trimmed[..120].TrimEnd();
            return trimmed.Length == 0 ? fallback : trimmed;
        }

        try
        {
            var sb = new StringBuilder(trimmed.Length);
            bool containsEscape = false;

            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];

                if (c is '\r' or '\n' or '\t')
                {
                    sb.Append(' ');
                    continue;
                }

                if (c == '\x1B')
                {
                    containsEscape = true;
                    continue;
                }

                if (char.IsControl(c))
                {
                    continue;
                }

                if (c is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or
                         '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\u200E' or '\u200F' or
                         '\u200B' or '\uFEFF' or '\u00AD' or '\u200C' or '\u200D')
                {
                    continue;
                }

                sb.Append(c);
            }

            var sanitized = sb.ToString();

            if (containsEscape)
            {
                sanitized = AnsiEscapeRegex().Replace(sanitized, string.Empty);
            }

            sanitized = sanitized.Trim();
            if (sanitized.Length > 120)
            {
                sanitized = sanitized[..120].TrimEnd();
            }

            return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser:Sanitize] Unexpected error sanitizing text '{raw}': {ex.Message}", ex);
            return fallback;
        }
    }

    public static string SanitizePlayerName(string? raw) => SanitizeText(raw, "Unnamed Player");
    public static string SanitizeReason(string? raw) => SanitizeText(raw, DefaultServerBanReason);

    public static List<PlayerModel> ParsePlayers(string rawResponse)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("ReforgerResponseParser.ParsePlayers");
        var players = new List<PlayerModel>();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return players;
        }

        try
        {
            string normalizedResponse = rawResponse;
            try
            {
                normalizedResponse = MissingNewlinePlayerSplitRegex().Replace(rawResponse, "\n$1");
            }
            catch (RegexMatchTimeoutException ex)
            {
                AppLogger.Trace($"[ReforgerResponseParser:Regex] Missing newline split regex timed out: {ex.Message}");
            }

            var lines = normalizedResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownPlayerHeaderLine(rawLine))
                {
                    continue;
                }

                if (TryParsePlayerLine(rawLine, out var player) && player != null)
                {
                    players.Add(player);
                }
                else
                {
                    LogParserAnomaly("Reforger Player List", i + 1, rawLine, "Line does not match standard '[Player#] ; [Player UID] ; [Player Name]' pattern.");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[ReforgerResponseParser:Players] Successfully parsed {players.Count} Reforger players from {lines.Length} lines in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser:Players] Fatal error parsing player list. Dump:\n{ToForensicDump(rawResponse)}", ex);
        }

        return players;
    }

    private static bool IsKnownPlayerHeaderLine(string line)
    {
        return line.StartsWith("Processing Command", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Players on server", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[Player#]", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("---", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Total players", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("unknown command", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Help for", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Client ID:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Logged In", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Logged in successfully", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParsePlayerLine(string line, out PlayerModel? player)
    {
        player = null;

        try
        {
            var firstSemi = line.IndexOf(';');
            if (firstSemi > 0)
            {
                var secondSemi = line.IndexOf(';', firstSemi + 1);
                if (secondSemi > firstSemi)
                {
                    var idSpan = line.AsSpan(0, firstSemi).Trim();
                    var uidSpan = line.AsSpan(firstSemi + 1, secondSemi - firstSemi - 1).Trim();
                    var nameSpan = line.AsSpan(secondSemi + 1).Trim();

                    if (int.TryParse(idSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && uidSpan.Length >= 8)
                    {
                        var uid = uidSpan.ToString();
                        var cleanName = SanitizePlayerName(nameSpan.ToString());

                        player = new PlayerModel
                        {
                            Id = id,
                            Uid = uid,
                            Guid = string.Empty,
                            ReforgerUid = uid,
                            BattlEyeGuid = string.Empty,
                            Name = cleanName,
                            Ip = "N/A",
                            Port = 0,
                            Ping = 0,
                            Country = new CountryInfo { Code = "xx", Name = DefaultUnknownRegion },
                            DisplayLocation = string.Empty
                        };
                        return true;
                    }
                }
            }

            var match = PlayerRowRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int fallbackId))
            {
                var uid = match.Groups[2].Value.Trim();
                var rawName = match.Groups[3].Value;

                string sanitizedName = SanitizePlayerName(rawName);

                player = new PlayerModel
                {
                    Id = fallbackId,
                    Uid = uid,
                    Guid = string.Empty,
                    ReforgerUid = uid,
                    BattlEyeGuid = string.Empty,
                    Name = sanitizedName,
                    Ip = "N/A",
                    Port = 0,
                    Ping = 0,
                    Country = new CountryInfo { Code = "xx", Name = DefaultUnknownRegion },
                    DisplayLocation = string.Empty
                };
                return true;
            }
        }
        catch (RegexMatchTimeoutException timeoutEx)
        {
            AppLogger.Warn($"[ReforgerResponseParser:PlayerRegexTimeout] Timeout parsing player line '{line}': {timeoutEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[ReforgerResponseParser:PlayerLineError] Error parsing player line '{line}': {ex.Message}");
        }

        return false;
    }

    public static List<BanModel> ParseBans(string rawResponse)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("ReforgerResponseParser.ParseBans");
        var bans = new List<BanModel>();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return bans;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            int banSequence = 1;

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownBanHeaderLine(rawLine))
                {
                    continue;
                }

                if (TryParseBanLine(rawLine, banSequence, out var ban) && ban != null)
                {
                    bans.Add(ban);
                    banSequence++;
                }
                else
                {
                    LogParserAnomaly("Reforger Ban List", i + 1, rawLine, "Line does not match standard '- <IdentityId> | <BannedName>' pattern.");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[ReforgerResponseParser:Bans] Parsed {bans.Count} Reforger ban(s) in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser:Bans] Error parsing bans. Dump:\n{ToForensicDump(rawResponse)}", ex);
        }

        return bans;
    }

    private static bool IsKnownBanHeaderLine(string line)
    {
        return line.StartsWith("Processing Command", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Total bans:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Help for ban command", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Server has no bans", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("#ban", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("ban lis", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("- is in", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("- <duration>", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("- <reason>", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("- is optional", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("- Identity Id", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Identity Id", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("---", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Page:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Players on server", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBanLine(string line, int banIndex, out BanModel? ban)
    {
        ban = null;

        try
        {
            if (line.StartsWith("Processing Command", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Server has no bans", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Players on server", StringComparison.OrdinalIgnoreCase) ||
                line.Contains(';'))
            {
                return false;
            }

            var pipeIdx = line.IndexOf('|');
            if (pipeIdx > 0)
            {
                var idPart = line.AsSpan(0, pipeIdx).Trim().TrimStart('-').Trim().ToString();
                var namePart = line.AsSpan(pipeIdx + 1).Trim().ToString();

                if (idPart.Length >= 10 && !idPart.Equals("Identity Id", StringComparison.OrdinalIgnoreCase))
                {
                    string sanitizedName = SanitizePlayerName(namePart);
                    ban = new BanModel
                    {
                        BanNumber = banIndex,
                        IdentityId = idPart,
                        BannedName = sanitizedName,
                        Reason = DefaultServerBanReason,
                        DurationSeconds = 0,
                        BannedAt = DateTime.UtcNow
                    };
                    return true;
                }
            }

            var matchWithSeparator = BanRowWithSeparatorRegex().Match(line);
            if (matchWithSeparator.Success)
            {
                var identityId = matchWithSeparator.Groups[1].Value.Trim();
                var rawBannedName = matchWithSeparator.Groups[2].Value;

                if (identityId.Equals("Identity Id", StringComparison.OrdinalIgnoreCase) ||
                    rawBannedName.Trim().Equals("Banned name", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string sanitizedBannedName = SanitizePlayerName(rawBannedName);

                ban = new BanModel
                {
                    BanNumber = banIndex,
                    IdentityId = identityId,
                    BannedName = sanitizedBannedName,
                    Reason = DefaultServerBanReason,
                    DurationSeconds = 0,
                    BannedAt = DateTime.UtcNow
                };
                return true;
            }

            var matchIdentityOnly = BanRowIdentityOnlyRegex().Match(line);
            if (matchIdentityOnly.Success)
            {
                var identityId = matchIdentityOnly.Groups[1].Value.Trim();
                ban = new BanModel
                {
                    BanNumber = banIndex,
                    IdentityId = identityId,
                    BannedName = "Unknown Target",
                    Reason = DefaultServerBanReason,
                    DurationSeconds = 0,
                    BannedAt = DateTime.UtcNow
                };
                return true;
            }
        }
        catch (RegexMatchTimeoutException timeoutEx)
        {
            AppLogger.Warn($"[ReforgerResponseParser:BanRegexTimeout] Timeout parsing ban line '{line}': {timeoutEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[ReforgerResponseParser:BanLineError] Error parsing ban line '{line}': {ex.Message}");
        }

        return false;
    }

    public static void LogParserAnomaly(string parserContext, int lineIndex, string rawLine, string reason)
    {
        AppLogger.Warn($"[PARSER_ANOMALY] Context: [{parserContext}] | Line #{lineIndex}: {reason} | Raw: \"{rawLine}\"");
    }

    public static string ToForensicDump(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "  (Empty or Null Payload)";

        var sb = new StringBuilder();
        var utf8Bytes = Encoding.UTF8.GetBytes(input);

        sb.AppendLine(CultureInfo.InvariantCulture, $"  Length:        {input.Length} char(s) ({utf8Bytes.Length} UTF-8 bytes)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Hex Dump:      {Convert.ToHexString(utf8Bytes)}");
        return sb.ToString().TrimEnd();
    }
}