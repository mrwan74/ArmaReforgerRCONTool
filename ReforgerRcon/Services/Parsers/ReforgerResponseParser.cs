using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services.Parsers;

public static partial class ReforgerResponseParser
{
    private const string DefaultServerBanReason = "Server Ban";
    private const string DefaultUnknownRegion = "Unknown Region";

    [GeneratedRegex(@"^\s*(\d+)\s*;\s*([a-fA-F0-9\-]{36}|[a-zA-Z0-9_\-]+)\s*;\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlayerRowRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?([a-fA-F0-9\-]{36}|[a-zA-Z0-9_\-]+)\s*(?:[\|\;])\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BanRowWithSeparatorRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?([a-fA-F0-9\-]{36})\s*$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BanRowIdentityOnlyRegex();

    [GeneratedRegex(@"[\u0300-\u036F\u1DC0-\u1DFF\u20D0-\u20FF\uFE20-\uFE2F]{4,}", RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex ExcessiveZalgoRegex();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex AnsiEscapeRegex();

    public static string SanitizeText(string? raw, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            AppLogger.Trace($"[ReforgerResponseParser:Sanitizer] Empty or null input string received. Returning fallback: '{fallback}'");
            return fallback;
        }

        var sb = new StringBuilder(raw.Length);
        var modifications = new List<string>();

        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];

            if (c is '\r' or '\n' or '\t')
            {
                sb.Append(' ');
                modifications.Add(string.Create(CultureInfo.InvariantCulture, $"WhitespaceAt[{i}]:0x{(int)c:X2}->Space"));
                continue;
            }

            if (char.IsControl(c))
            {
                modifications.Add(string.Create(CultureInfo.InvariantCulture, $"ControlCharStrippedAt[{i}]:U+{(int)c:X4}"));
                continue;
            }

            if (c is '\u202A' or '\u202B' or '\u202C' or '\u202D' or '\u202E' or
                     '\u2066' or '\u2067' or '\u2068' or '\u2069' or '\u200E' or '\u200F' or
                     '\u200B' or '\uFEFF' or '\u00AD' or '\u200C' or '\u200D')
            {
                modifications.Add(string.Create(CultureInfo.InvariantCulture, $"BidiInvisibleStrippedAt[{i}]:U+{(int)c:X4}"));
                continue;
            }

            sb.Append(c);
        }

        var sanitized = sb.ToString();

        try
        {
            if (AnsiEscapeRegex().IsMatch(sanitized))
            {
                var beforeAnsi = sanitized;
                sanitized = AnsiEscapeRegex().Replace(sanitized, string.Empty);
                modifications.Add($"AnsiEscapeStripped:Length_{beforeAnsi.Length}->{sanitized.Length}");
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[ReforgerResponseParser:Sanitizer] Regex timeout during ANSI escape sequence filtering: {regexEx.Message}");
        }

        try
        {
            if (ExcessiveZalgoRegex().IsMatch(sanitized))
            {
                var beforeZalgo = sanitized;
                sanitized = ExcessiveZalgoRegex().Replace(sanitized, string.Empty);
                modifications.Add($"ZalgoMarksStripped:Length_{beforeZalgo.Length}->{sanitized.Length}");
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[ReforgerResponseParser:Sanitizer] Regex timeout during Zalgo mark filtering: {regexEx.Message}");
        }

        sanitized = sanitized.Trim();

        if (sanitized.Length > 120)
        {
            modifications.Add($"TruncatedLength:{sanitized.Length}->120");
            sanitized = sanitized[..120].TrimEnd();
        }

        if (modifications.Count > 0)
        {
            AppLogger.Debug(
                $"[ReforgerResponseParser:Sanitizer] Text modifications applied ({modifications.Count}): [{string.Join(", ", modifications)}]\n" +
                $"  Original:  \"{raw}\"\n" +
                $"  Sanitized: \"{sanitized}\""
            );
        }

        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }

    public static string SanitizePlayerName(string? raw) => SanitizeText(raw, "Unnamed Player");
    public static string SanitizeReason(string? raw) => SanitizeText(raw, DefaultServerBanReason);

    public static List<PlayerModel> ParsePlayers(string rawResponse)
    {
        using var timing = AppLogger.Measure("ReforgerResponseParser.ParsePlayers");
        var players = new List<PlayerModel>();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            AppLogger.Trace("[ReforgerResponseParser] Received empty or null raw player response buffer.");
            return players;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[ReforgerResponseParser] Beginning line-by-line parsing for {lines.Length} line(s) ({rawResponse.Length} UTF-8 characters)...");

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownPlayerHeaderLine(rawLine))
                {
                    AppLogger.Trace($"[ReforgerResponseParser] Skipped known player header line #{i + 1}: '{rawLine}'");
                    continue;
                }

                if (TryParsePlayerLine(rawLine, i, out var player) && player != null)
                {
                    players.Add(player);
                }
                else
                {
                    LogParserAnomaly("Reforger Player List", i + 1, rawLine, "Line did not match standard '[Player#] ; [Player UID] ; [Player Name]' pattern and heuristic reconstruction failed.");
                }
            }

            AppLogger.Info($"[ReforgerResponseParser] Finished parsing Reforger player payload. Successfully extracted {players.Count} player record(s) from {lines.Length} line(s).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser] Fatal error during Reforger player list parsing. Forensic Payload Snapshot:\n{ToForensicDump(rawResponse)}", ex);
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

    private static bool TryParsePlayerLine(string line, int lineIndex, out PlayerModel? player)
    {
        player = null;

        try
        {
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

                AppLogger.Trace($"[ReforgerResponseParser] Successfully parsed player #{id} (ReforgerUID: {uid}, Name: '{sanitizedName}') on line #{lineIndex + 1}.");
                return true;
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[ReforgerResponseParser] Regex timeout on player line #{lineIndex + 1}: '{line}'. Exception: {regexEx.Message}");
        }

        return TryHeuristicPlayerLine(line, lineIndex, out player);
    }

    private static bool TryHeuristicPlayerLine(string line, int lineIndex, out PlayerModel? player)
    {
        player = null;

        var tokens = line.Split(';', 3, StringSplitOptions.TrimEntries);
        if (tokens.Length >= 3 && int.TryParse(tokens[0], out int id))
        {
            var uid = tokens[1].Trim();
            var rawName = tokens[2].Trim();

            if (uid.Length >= 8)
            {
                string sanitizedName = SanitizePlayerName(rawName);
                AppLogger.Warn($"[ReforgerResponseParser:Heuristic] Salvaged player record #{id} (UID: {uid}, Name: '{sanitizedName}') on line #{lineIndex + 1} from raw: '{line}'");

                player = new PlayerModel
                {
                    Id = id,
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

        return false;
    }

    public static List<BanModel> ParseBans(string rawResponse)
    {
        using var timing = AppLogger.Measure("ReforgerResponseParser.ParseBans");
        var bans = new List<BanModel>();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            AppLogger.Trace("[ReforgerResponseParser] Received empty raw ban response buffer.");
            return bans;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[ReforgerResponseParser] Beginning ban parsing for {lines.Length} line(s)...");
            int banSequence = 1;

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownBanHeaderLine(rawLine))
                {
                    AppLogger.Trace($"[ReforgerResponseParser] Skipped known ban header line #{i + 1}: '{rawLine}'");
                    continue;
                }

                if (TryParseBanLine(rawLine, i, banSequence, out var ban) && ban != null)
                {
                    bans.Add(ban);
                    banSequence++;
                }
                else
                {
                    LogParserAnomaly("Reforger Ban List", i + 1, rawLine, "Line does not conform to standard '- <IdentityId> | <BannedName>' or identity-only syntax.");
                }
            }

            AppLogger.Info($"[ReforgerResponseParser] Finished parsing Reforger bans. Extracted {bans.Count} ban record(s) from {lines.Length} line(s).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[ReforgerResponseParser] Fatal error during Reforger ban parsing. Forensic Dump:\n{ToForensicDump(rawResponse)}", ex);
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
               line.StartsWith("Page:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBanLine(string line, int lineIndex, int banIndex, out BanModel? ban)
    {
        ban = null;

        try
        {
            var matchWithSeparator = BanRowWithSeparatorRegex().Match(line);
            if (matchWithSeparator.Success)
            {
                var identityId = matchWithSeparator.Groups[1].Value.Trim();
                var rawBannedName = matchWithSeparator.Groups[2].Value;

                if (identityId.Equals("Identity Id", StringComparison.OrdinalIgnoreCase) ||
                    rawBannedName.Trim().Equals("Banned name", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Trace($"[ReforgerResponseParser] Skipped column header table row on line #{lineIndex + 1}.");
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

                AppLogger.Trace($"[ReforgerResponseParser] Parsed ban record #{banIndex} (ID: {identityId}, Name: '{sanitizedBannedName}') on line #{lineIndex + 1}.");
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
                AppLogger.Trace($"[ReforgerResponseParser] Parsed identity-only ban record #{banIndex} (ID: {identityId}) on line #{lineIndex + 1}.");
                return true;
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[ReforgerResponseParser] Regex timeout on ban line #{lineIndex + 1}: '{line}'. Exception: {regexEx.Message}");
        }

        if (line.Contains('|'))
        {
            var parts = line.Split('|', 2, StringSplitOptions.TrimEntries);
            var idPart = parts[0].TrimStart('-', ' ').Trim();
            var namePart = parts[1].Trim();

            if (idPart.Length >= 10 && !idPart.Equals("Identity Id", StringComparison.OrdinalIgnoreCase))
            {
                string sanitizedName = SanitizePlayerName(namePart);
                AppLogger.Warn($"[ReforgerResponseParser:Heuristic] Salvaged ban record #{banIndex} (ID: {idPart}, Name: '{sanitizedName}') on line #{lineIndex + 1} from raw: '{line}'");

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

        return false;
    }

    public static void LogParserAnomaly(string parserContext, int lineIndex, string rawLine, string reason)
    {
        var forensicDump = ToForensicDump(rawLine);
        var charBreakdown = GenerateCharacterForensics(rawLine);

        AppLogger.Warn(
            $"[PARSER_ANOMALY] Context: [{parserContext}] | Line #{lineIndex} failed parsing.\n" +
            $"  Reason:        {reason}\n" +
            $"  Raw Line:      \"{rawLine}\"\n" +
            $"  Hex & Length Dump:\n{forensicDump}\n" +
            $"  Character Forensics:\n{charBreakdown}"
        );
    }

    public static string GenerateCharacterForensics(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "    (Empty / Null string input)";

        var sb = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            var category = char.GetUnicodeCategory(c);
            var isControl = char.IsControl(c);
            var isSurrogate = char.IsSurrogate(c);
            var utf8Hex = Convert.ToHexString(Encoding.UTF8.GetBytes([c]));

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"    [{i:D2}] Char='{(isControl ? ' ' : c)}' | U+{(int)c:X4} | Cat={category,-22} | Ctrl={isControl,-5} | Surr={isSurrogate,-5} | UTF8=[{utf8Hex}]");
        }

        return sb.ToString().TrimEnd();
    }

    public static string ToForensicDump(string? input)
    {
        if (string.IsNullOrEmpty(input)) return "  (Empty or Null Payload)";

        var sb = new StringBuilder();
        var utf8Bytes = Encoding.UTF8.GetBytes(input);

        sb.AppendLine(CultureInfo.InvariantCulture, $"  Length:        {input.Length} char(s) ({utf8Bytes.Length} UTF-8 bytes)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"  Hex Dump:      {Convert.ToHexString(utf8Bytes)}");

        var nonAsciiOrControl = input
            .Select((c, idx) => (Char: c, Index: idx))
            .Where(x => char.IsControl(x.Char) || x.Char > 127)
            .Take(40)
            .ToList();

        if (nonAsciiOrControl.Count > 0)
        {
            sb.AppendLine("  Special / Non-ASCII / Control Characters (Up to 40):");
            foreach (var (c, idx) in nonAsciiOrControl)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"    - Char[{idx}]: '{(char.IsControl(c) ? ' ' : c)}' (U+{(int)c:X4}, Category: {char.GetUnicodeCategory(c)})");
            }
        }

        return sb.ToString().TrimEnd();
    }
}