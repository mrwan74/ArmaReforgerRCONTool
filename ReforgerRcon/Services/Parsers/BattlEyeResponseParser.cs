using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services.Parsers;

public static partial class BattlEyeResponseParser
{
    [GeneratedRegex(@"^\s*(\d+)\s+((?:\[[a-fA-F0-9:]+\]|[\d\.]+)):(\d+)\s+(-?\d+)\s+([a-fA-F0-9]{32}|\-)(?:\((?:OK|\?|\w+)\))?\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlayerRowRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+([a-fA-F0-9]{32}|[\d\.]+)\s+(\w+|-?\d+)\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BanRowRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+((?:\[[a-fA-F0-9:]+\]|[\d\.]+)):(\d+)\s*$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AdminRowRegex();

    [GeneratedRegex(@"\((\d+)\s+players\s+in\s+total\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TotalPlayersFooterRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+\((.+?):(\d+)\)\s+connected", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    public static partial Regex PlayerConnectedStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+-\s+BE\s+GUID:\s+([a-fA-F0-9]{32})", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    public static partial Regex PlayerGuidStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+disconnected", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    public static partial Regex PlayerDisconnectedStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+\(([a-fA-F0-9]{32})\)\s+has\s+been\s+kicked\s+by\s+BattlEye:\s+Admin\s+Kick(?:\s*\((.*?)\))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    public static partial Regex PlayerKickedStreamRegex();

    [GeneratedRegex(@"^Player\s+#(\d+)\s+(.+?)\s+\(([a-fA-F0-9]{32})\)\s+has\s+been\s+kicked\s+by\s+BattlEye:\s+Admin\s+Ban(?:\s*\((.*?)\))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    public static partial Regex PlayerBannedStreamRegex();

    [GeneratedRegex(@"^RCon\s+admin\s+#(\d+)\s+\((.+?)\)\s+logged\s+in", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
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
            AppLogger.Debug($"[BattlEyeResponseParser:Players] Processing {lines.Length} line(s) for player records ({rawResponse.Length} chars)...");

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownPlayerHeaderLine(rawLine))
                {
                    AppLogger.Trace($"[BattlEyeResponseParser:Players] Skipped header line #{i + 1}: '{rawLine}'");
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
            if (totalMatch.Success && int.TryParse(totalMatch.Groups[1].Value, out int expectedCount))
            {
                if (players.Count != expectedCount)
                {
                    AppLogger.Warn($"[BattlEyeResponseParser:Players] Discrepancy: Server reported {expectedCount} players, parsed {players.Count} rows.");
                }
                else
                {
                    AppLogger.Trace($"[BattlEyeResponseParser:Players] Server count verified ({expectedCount} players).");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[BattlEyeResponseParser:Players] Parsed {players.Count} active player(s) from {lines.Length} line(s) in {elapsedMs:F2}ms.");
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
            AppLogger.Trace("[BattlEyeResponseParser:Admins] Empty response buffer.");
            return admins;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[BattlEyeResponseParser:Admins] Processing {lines.Length} line(s) for connected admins...");

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.StartsWith("Connected RCon admins:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("[#]", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("---", StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Trace($"[BattlEyeResponseParser:Admins] Skipped header line #{i + 1}: '{line}'");
                    continue;
                }

                var match = AdminRowRegex().Match(line);
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, out int id) &&
                    int.TryParse(match.Groups[3].Value, out int port))
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

                    AppLogger.Trace($"[BattlEyeResponseParser:Admins] Parsed admin #{id} ({ip}:{port}, Location: '{geo.NaturalLocation}').");
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

        try
        {
            var match = PlayerRowRegex().Match(line);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out int id) &&
                int.TryParse(match.Groups[3].Value, out int port) &&
                int.TryParse(match.Groups[4].Value, out int ping))
            {
                var ip = match.Groups[2].Value.Trim();
                var guid = match.Groups[5].Value.Trim();
                var rawName = match.Groups[6].Value;

                string cleanName = ReforgerResponseParser.SanitizePlayerName(rawName);

                if (cleanName.EndsWith(" (Lobby)", StringComparison.OrdinalIgnoreCase))
                {
                    cleanName = cleanName[..^8].TrimEnd();
                }

                var geo = GeoIpService.GetLocation(ip);

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
                    Country = new CountryInfo
                    {
                        Code = geo.CountryCode,
                        Name = geo.CountryName
                    },
                    LocationCity = geo.CityName,
                    LocationState = geo.SubdivisionName,
                    DisplayLocation = geo.NaturalLocation,
                    TimeZone = geo.TimeZone
                };

                AppLogger.Trace($"[BattlEyeResponseParser:Players] Line #{lineIndex + 1} -> #{id} '{cleanName}' ({ip}:{port}, Ping={ping}ms, GUID={guid}).");
                return true;
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[BattlEyeResponseParser:Players] Regex timeout on line #{lineIndex + 1}: '{line}': {regexEx.Message}");
        }

        return TryHeuristicPlayerLine(line, out player);
    }

    private static bool TryHeuristicPlayerLine(string line, out PlayerModel? player)
    {
        player = null;

        var tokens = line.Split([' ', '\t'], 5, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 5 &&
            int.TryParse(tokens[0], out int id) &&
            tokens[1].Contains(':') &&
            int.TryParse(tokens[2], out int ping))
        {
            var endpointParts = tokens[1].Split(':', 2);
            if (endpointParts.Length == 2 && int.TryParse(endpointParts[1], out int port))
            {
                var ip = endpointParts[0].Trim('[', ']');
                var guid = tokens[3].Trim();
                var rawName = tokens[4].Trim();

                string cleanName = ReforgerResponseParser.SanitizePlayerName(rawName);
                if (cleanName.EndsWith(" (Lobby)", StringComparison.OrdinalIgnoreCase))
                {
                    cleanName = cleanName[..^8].TrimEnd();
                }

                var geo = GeoIpService.GetLocation(ip);

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
                    Country = new CountryInfo
                    {
                        Code = geo.CountryCode,
                        Name = geo.CountryName
                    },
                    LocationCity = geo.CityName,
                    LocationState = geo.SubdivisionName,
                    DisplayLocation = geo.NaturalLocation,
                    TimeZone = geo.TimeZone
                };
                return true;
            }
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
            AppLogger.Trace("[BattlEyeResponseParser:Bans] Empty response buffer.");
            return bans;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[BattlEyeResponseParser:Bans] Processing {lines.Length} line(s) for bans...");

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (IsKnownBanHeaderLine(rawLine))
                {
                    AppLogger.Trace($"[BattlEyeResponseParser:Bans] Skipped header line #{i + 1}: '{rawLine}'");
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
        return line.StartsWith("GUID Bans:", StringComparison.OrdinalIgnoreCase) ||
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
            if (match.Success && int.TryParse(match.Groups[1].Value, out int banNumber))
            {
                var identity = match.Groups[2].Value.Trim();
                var durationStr = match.Groups[3].Value.Trim();
                var rawReason = match.Groups[4].Value;

                long durationSeconds = 0;
                if (durationStr.Equals("-", StringComparison.OrdinalIgnoreCase) || durationStr.Equals("expired", StringComparison.OrdinalIgnoreCase))
                {
                    durationSeconds = -1;
                }
                else if (!durationStr.Equals("perm", StringComparison.OrdinalIgnoreCase) &&
                         !durationStr.Equals("-1", StringComparison.OrdinalIgnoreCase) &&
                         long.TryParse(durationStr, out long minutes))
                {
                    durationSeconds = minutes * 60;
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

                AppLogger.Trace($"[BattlEyeResponseParser:Bans] Line #{lineIndex + 1} -> Ban #{banNumber}: Identity='{identity}', Duration={durationSeconds}s, Reason='{cleanReason}'.");
                return true;
            }
        }
        catch (RegexMatchTimeoutException regexEx)
        {
            AppLogger.Warn($"[BattlEyeResponseParser:Bans] Regex timeout on line #{lineIndex + 1}: '{line}': {regexEx.Message}");
        }

        var tokens = line.Split([' ', '\t'], 4, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 3 && int.TryParse(tokens[0], out int fallbackBanNo))
        {
            var identity = tokens[1].Trim();
            var durationStr = tokens[2].Trim();
            var rawReason = tokens.Length > 3 ? tokens[3].Trim() : "Server Ban";

            long durationSeconds = 0;
            if (long.TryParse(durationStr, out long minutes))
            {
                durationSeconds = minutes * 60;
            }

            var cleanReason = ReforgerResponseParser.SanitizeReason(rawReason);

            ban = new BanModel
            {
                BanNumber = fallbackBanNo,
                IdentityId = identity,
                BannedName = "Banned Target",
                Reason = cleanReason,
                DurationSeconds = durationSeconds,
                BannedAt = DateTime.UtcNow
            };
            return true;
        }

        return false;
    }
}