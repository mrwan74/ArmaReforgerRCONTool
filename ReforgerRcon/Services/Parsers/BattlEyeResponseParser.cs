using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services.Parsers;

public static partial class BattlEyeResponseParser
{
    // Accommodates 1-5 digit IDs, bracketed IPv6 or IPv4, 1-5 digit ports, signed pings (e.g. -1 in dump Frame 8), 32-hex GUID with optional (?) suffix, and lobby names
    [GeneratedRegex(@"^\s*(\d+)\s+((?:\[[a-fA-F0-9:]+\]|[\d\.]+)):(\d+)\s+(-?\d+|\?+)\s+([a-fA-F0-9]{32}|\-)(?:\([^\)]*\))?\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex PlayerRowRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+([a-fA-F0-9]{32}|(?:\[[a-fA-F0-9:]+\]|[\d\.]+))\s+(\w+|-?\d+|-)\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex BanRowRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+((?:\[[a-fA-F0-9:]+\]|[\d\.]+)):(\d+)\s*$", RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex AdminRowRegex();

    [GeneratedRegex(@"\((\d+)\s+players\s+in\s+total\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 200)]
    private static partial Regex TotalPlayersFooterRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+\((.+?):(\d+)\)\s+connected", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    public static partial Regex PlayerConnectedStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+-\s+BE\s+GUID:\s+([a-fA-F0-9]{32})", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    public static partial Regex PlayerGuidStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+disconnected", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    public static partial Regex PlayerDisconnectedStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+\(([a-fA-F0-9]{32})\)\s+has\s+been\s+kicked\s+by\s+BattlEye:\s+Admin\s+Kick(?:\s*\((.*?)\))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    public static partial Regex PlayerKickedStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+\(([a-fA-F0-9]{32})\)\s+has\s+been\s+kicked\s+by\s+BattlEye:\s+Admin\s+Ban(?:\s*\((.*?)\))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    public static partial Regex PlayerBannedStreamRegex();

    [GeneratedRegex(@"^RCon\s+admin\s+#(\d+)\s+\((.+?)\)\s+logged\s+in", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    public static partial Regex AdminConnectedStreamRegex();

    public static List<PlayerModel> ParsePlayers(string rawResponse)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BattlEyeResponseParser.ParsePlayers");
        var players = new List<PlayerModel>();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            AppLogger.Trace("[BattlEyeResponseParser:Players] Empty response buffer.");
            return players;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[BattlEyeResponseParser:Players] Parsing {lines.Length} lines for BattlEye players ({rawResponse.Length} chars)...");

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownPlayerHeaderLine(rawLine))
                {
                    continue;
                }

                if (TryParsePlayerLine(rawLine, i, out var player) && player != null)
                {
                    players.Add(player);
                }
                else
                {
                    ReforgerResponseParser.LogParserAnomaly("BattlEye Player List", i + 1, rawLine, "Line did not match standard '[#] [IP:Port] [Ping] [GUID] [Name]'.");
                }
            }

            var totalMatch = TotalPlayersFooterRegex().Match(rawResponse);
            if (totalMatch.Success &&
                int.TryParse(totalMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int expectedCount) &&
                players.Count != expectedCount)
            {
                AppLogger.Warn($"[BattlEyeResponseParser:Players] Discrepancy: Server footer reported {expectedCount} players, parsed {players.Count} rows.");
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[BattlEyeResponseParser:Players] Parsed {players.Count} active player(s) in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeResponseParser:Players] Critical failure parsing players. Dump:\n{ReforgerResponseParser.ToForensicDump(rawResponse)}", ex);
        }

        return players;
    }

    public static List<AdminModel> ParseAdmins(string rawResponse)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BattlEyeResponseParser.ParseAdmins");
        var admins = new List<AdminModel>();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return admins;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("Connected RCon admins:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[#]", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("---", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var match = AdminRowRegex().Match(line);
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) &&
                    int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                {
                    var ip = match.Groups[2].Value.Trim();
                    var geo = GeoIpService.GetLocation(ip);

                    admins.Add(new AdminModel
                    {
                        Id = id,
                        Ip = ip,
                        Port = port,
                        Country = new CountryInfo { Code = geo.CountryCode, Name = geo.CountryName },
                        Location = geo.NaturalLocation,
                        TimeZone = geo.TimeZone
                    });
                }
                else
                {
                    ReforgerResponseParser.LogParserAnomaly("BattlEye Admins List", i + 1, line, "Line did not match '[#] [IP:Port]'.");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[BattlEyeResponseParser:Admins] Parsed {admins.Count} admin(s) in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeResponseParser:Admins] Error parsing admins: {ex.Message}", ex);
        }

        return admins;
    }

    private static bool IsKnownPlayerHeaderLine(string line)
    {
        return line.StartsWith("Players on server:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[#]", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("---", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("(", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Connected RCon", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("RCon admin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParsePlayerLine(string line, int lineIndex, out PlayerModel? player)
    {
        player = null;

        // Try heuristic tokenizer first as a zero-backtracking fast path (matches Frame 8: "0   68.88.103.125:60464   -1   0ccf...(?)  Jbagofdonuts (Lobby)")
        if (TryHeuristicPlayerLine(line, out player))
        {
            return true;
        }

        try
        {
            var match = PlayerRowRegex().Match(line);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) &&
                int.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
            {
                var pingToken = match.Groups[4].Value;
                int ping = 0;
                if (int.TryParse(pingToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pVal))
                {
                    ping = pVal;
                }

                var ip = match.Groups[2].Value.Trim();
                var guid = match.Groups[5].Value.Trim();
                var rawName = match.Groups[6].Value;

                string cleanName = ReforgerResponseParser.SanitizePlayerName(rawName);

                if (cleanName.EndsWith(" (Lobby)", StringComparison.OrdinalIgnoreCase))
                {
                    cleanName = cleanName[..^8].TrimEnd();
                }

                CountryInfo country = new() { Code = "xx", Name = "Unknown Region" };
                string city = string.Empty;
                string state = string.Empty;
                string location = string.Empty;
                string timezone = string.Empty;

                if (GeoIpService.TryGetCachedLocation(ip, out var geo))
                {
                    country = new CountryInfo { Code = geo.CountryCode, Name = geo.CountryName };
                    city = geo.CityName;
                    state = geo.SubdivisionName;
                    location = geo.NaturalLocation;
                    timezone = geo.TimeZone;
                }

                player = new PlayerModel
                {
                    Id = id,
                    Uid = guid == "-" ? $"init_{id}" : guid,
                    Guid = guid == "-" ? "Initializing..." : guid,
                    BattlEyeGuid = guid == "-" ? string.Empty : guid,
                    ReforgerUid = string.Empty,
                    Name = cleanName,
                    Ip = ip,
                    Port = port,
                    Ping = ping,
                    Country = country,
                    LocationCity = city,
                    LocationState = state,
                    DisplayLocation = location,
                    TimeZone = timezone
                };

                return true;
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[BattlEyeResponseParser:Players] Regex timeout on line #{lineIndex + 1}: '{line}': {regexEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[BattlEyeResponseParser:Players] Error parsing player line #{lineIndex + 1}: '{line}': {ex.Message}");
        }

        return false;
    }

    private static bool TryHeuristicPlayerLine(string line, out PlayerModel? player)
    {
        player = null;

        try
        {
            // Accommodates variable multi-space delimiters between [#], [IP:Port], [Ping], and [GUID]
            var tokens = line.Split([' ', '\t'], 5, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 5 &&
                int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) &&
                tokens[1].Contains(':'))
            {
                var endpointParts = tokens[1].Split(':', 2);
                if (endpointParts.Length == 2 && int.TryParse(endpointParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int port))
                {
                    int ping = 0;
                    if (int.TryParse(tokens[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPing))
                    {
                        ping = parsedPing;
                    }

                    var ip = endpointParts[0].Trim('[', ']');
                    var guidToken = tokens[3].Trim();

                    // Handles the (?) unverified status suffix captured in Frame 8 (e.g. "2a14da...(?)" -> "2a14da...")
                    int parenIdx = guidToken.IndexOf('(');
                    var guid = parenIdx >= 0 ? guidToken[..parenIdx].Trim() : guidToken;

                    var rawName = tokens[4].Trim();
                    string cleanName = ReforgerResponseParser.SanitizePlayerName(rawName);
                    if (cleanName.EndsWith(" (Lobby)", StringComparison.OrdinalIgnoreCase))
                    {
                        cleanName = cleanName[..^8].TrimEnd();
                    }

                    CountryInfo country = new() { Code = "xx", Name = "Unknown Region" };
                    string city = string.Empty;
                    string state = string.Empty;
                    string location = string.Empty;
                    string timezone = string.Empty;

                    if (GeoIpService.TryGetCachedLocation(ip, out var geo))
                    {
                        country = new CountryInfo { Code = geo.CountryCode, Name = geo.CountryName };
                        city = geo.CityName;
                        state = geo.SubdivisionName;
                        location = geo.NaturalLocation;
                        timezone = geo.TimeZone;
                    }

                    player = new PlayerModel
                    {
                        Id = id,
                        Uid = guid == "-" ? $"init_{id}" : guid,
                        Guid = guid == "-" ? "Initializing..." : guid,
                        BattlEyeGuid = guid == "-" ? string.Empty : guid,
                        ReforgerUid = string.Empty,
                        Name = cleanName,
                        Ip = ip,
                        Port = port,
                        Ping = ping,
                        Country = country,
                        LocationCity = city,
                        LocationState = state,
                        DisplayLocation = location,
                        TimeZone = timezone
                    };
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[BattlEyeResponseParser:Heuristic] Heuristic player parsing notice for '{line}': {ex.Message}");
        }

        return false;
    }

    public static List<BanModel> ParseBans(string rawResponse)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BattlEyeResponseParser.ParseBans");
        var bans = new List<BanModel>();

        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return bans;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownBanHeaderLine(rawLine))
                {
                    continue;
                }

                if (TryParseBanLine(rawLine, i, out var ban) && ban != null)
                {
                    bans.Add(ban);
                }
                else
                {
                    ReforgerResponseParser.LogParserAnomaly("BattlEye Ban List", i + 1, rawLine, "Line does not match standard '[#] [GUID/IP] [Duration] [Reason]' pattern.");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[BattlEyeResponseParser:Bans] Parsed {bans.Count} ban(s) in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeResponseParser:Bans] Error parsing bans. Dump:\n{ReforgerResponseParser.ToForensicDump(rawResponse)}", ex);
        }

        return bans;
    }

    private static bool IsKnownBanHeaderLine(string line)
    {
        return line.Equals("bans", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("GUID Bans:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("IP Bans:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[#]", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("---", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseBanLine(string line, int lineIndex, out BanModel? ban)
    {
        ban = null;

        try
        {
            var match = BanRowRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int banNumber))
            {
                var identity = match.Groups[2].Value.Trim();
                var durationStr = match.Groups[3].Value.Trim();
                var rawReason = match.Groups[4].Value;

                long durationSeconds;

                if (durationStr.Equals("-", StringComparison.OrdinalIgnoreCase) ||
                    durationStr.Equals("expired", StringComparison.OrdinalIgnoreCase))
                {
                    durationSeconds = -1;
                }
                else if (durationStr.Equals("perm", StringComparison.OrdinalIgnoreCase))
                {
                    durationSeconds = 0;
                }
                else if (long.TryParse(durationStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out long minutes))
                {
                    durationSeconds = minutes <= 0 ? 30 : minutes * 60;
                }
                else
                {
                    durationSeconds = -1;
                }

                var cleanReason = ReforgerResponseParser.SanitizeReason(rawReason);

                ban = new BanModel
                {
                    BanNumber = banNumber,
                    IdentityId = identity,
                    BannedName = "Banned Target",
                    Reason = cleanReason,
                    DurationSeconds = durationSeconds,
                    BannedAt = DateTime.UtcNow
                };

                return true;
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[BattlEyeResponseParser:Bans] Regex timeout on line #{lineIndex + 1}: '{line}': {regexEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[BattlEyeResponseParser:Bans] Unexpected error parsing ban line #{lineIndex + 1}: '{line}': {ex.Message}");
        }

        return false;
    }
}