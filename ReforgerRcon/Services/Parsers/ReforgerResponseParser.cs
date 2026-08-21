using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services.Parsers;

public static partial class ReforgerResponseParser
{
    [GeneratedRegex(@"^\s*(\d+)\s*;\s*([a-fA-F0-9\-]{36}|[a-zA-Z0-9_\-]+)\s*;\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlayerRowRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?([a-fA-F0-9\-]{36}|[a-zA-Z0-9_\-]+)\s*(?:[\|\;])\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BanRowWithSeparatorRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?([a-fA-F0-9\-]{36})\s*$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BanRowIdentityOnlyRegex();

    [GeneratedRegex(@"[\u0300-\u036F\u1DC0-\u1DFF\u20D0-\u20FF\uFE20-\uFE2F]{3,}", RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex ExcessiveZalgoRegex();

    public static string SanitizeText(string? raw, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var sb = new StringBuilder(raw.Length);

        foreach (char c in raw)
        {
            if (c is '\r' or '\n' or '\t')
            {
                sb.Append(' ');
                continue;
            }

            if (char.IsControl(c))
            {
                continue;
            }

            if (c is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or
                     '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\u200E' or '\u200F')
            {
                continue;
            }

            sb.Append(c);
        }

        var sanitized = sb.ToString();

        try
        {
            sanitized = ExcessiveZalgoRegex().Replace(sanitized, string.Empty);
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Debug($"[ReforgerResponseParser] Regex evaluation timed out during zalgo filtering: {regexEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[ReforgerResponseParser] Non-fatal exception during text sanitization: {ex.Message}");
        }

        sanitized = sanitized.Trim();

        if (sanitized.Length > 120)
        {
            sanitized = sanitized[..120].TrimEnd();
        }

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    public static string SanitizePlayerName(string? raw) => SanitizeText(raw, "Unnamed Player");
    public static string SanitizeReason(string? raw) => SanitizeText(raw, "Server Ban");

    public static List<PlayerModel> ParsePlayers(string rawResponse)
    {
        var players = new List<PlayerModel>();
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            AppLogger.Trace("[ReforgerResponseParser] Empty player response buffer.");
            return players;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[ReforgerResponseParser] Processing {lines.Length} lines for Reforger player records...");

            foreach (var rawLine in lines)
            {
                if (TryParsePlayerLine(rawLine, out var player) && player != null)
                {
                    players.Add(player);
                }
            }

            AppLogger.Info($"[ReforgerResponseParser] Successfully extracted {players.Count} active Reforger player(s).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser] Critical failure while parsing Reforger player payload: {ex.Message}", ex);
        }

        return players;
    }

    private static bool TryParsePlayerLine(string line, out PlayerModel? player)
    {
        player = null;

        if (line.StartsWith("Processing Command", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Players on server", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("[Player#]", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("---", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Total players", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("unknown command", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var match = PlayerRowRegex().Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
        {
            var uid = match.Groups[2].Value.Trim();
            var rawName = match.Groups[3].Value;

            string sanitizedName = SanitizePlayerName(rawName);

            player = new PlayerModel
            {
                Id = id,
                Uid = uid,
                Guid = uid,
                Name = sanitizedName,
                Ip = "N/A",
                Port = 0,
                Ping = 0,
                Country = new CountryInfo { Code = "un", Name = "Direct Reforger Server" }
            };
            return true;
        }

        return false;
    }

    public static List<BanModel> ParseBans(string rawResponse)
    {
        var bans = new List<BanModel>();
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            AppLogger.Trace("[ReforgerResponseParser] Empty ban response buffer.");
            return bans;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[ReforgerResponseParser] Processing {lines.Length} lines for Reforger ban records...");
            int index = 1;

            foreach (var rawLine in lines)
            {
                if (TryParseBanLine(rawLine, index, out var ban) && ban != null)
                {
                    bans.Add(ban);
                    index++;
                }
            }

            AppLogger.Info($"[ReforgerResponseParser] Successfully extracted {bans.Count} Reforger ban(s).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser] Critical failure while parsing Reforger ban payload: {ex.Message}", ex);
        }

        return bans;
    }

    private static bool TryParseBanLine(string line, int index, out BanModel? ban)
    {
        ban = null;

        if (line.StartsWith("Processing Command", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Total bans:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Help for ban command", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("#ban", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("- is in", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("- is optional", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("- <duration>", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("- <reason>", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("- Identity Id", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Identity Id", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("---", StringComparison.OrdinalIgnoreCase))
        {
            return false;
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
                BanNumber = index,
                IdentityId = identityId,
                BannedName = sanitizedBannedName,
                Reason = "Server Ban",
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
                BanNumber = index,
                IdentityId = identityId,
                BannedName = "Unknown Target",
                Reason = "Server Ban",
                DurationSeconds = 0,
                BannedAt = DateTime.UtcNow
            };
            return true;
        }

        return false;
    }
}