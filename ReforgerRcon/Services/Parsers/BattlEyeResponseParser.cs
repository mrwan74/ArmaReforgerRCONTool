using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services.Parsers;

public static partial class BattlEyeResponseParser
{
    [GeneratedRegex(@"^\s*(\d+)\s+((?:\[[a-fA-F0-9:]+\]|[\d\.]+)):(\d+)\s+(-?\d+)\s+([a-fA-F0-9]{32}|\-)(?:\((?:OK|\?|\w+)\))?\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PlayerRowRegex();

    [GeneratedRegex(@"^\s*(\d+)\s+([a-fA-F0-9]{32}|[\d\.]+)\s+(\w+|-?\d+)\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BanRowRegex();

    public static List<PlayerModel> ParsePlayers(string rawResponse)
    {
        var players = new List<PlayerModel>();
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            AppLogger.Trace("[BattlEyeResponseParser] Empty BattlEye player response buffer.");
            return players;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[BattlEyeResponseParser] Processing {lines.Length} lines for BattlEye player records...");

            foreach (var rawLine in lines)
            {
                if (TryParsePlayerLine(rawLine, out var player) && player != null)
                {
                    players.Add(player);
                }
            }

            AppLogger.Info($"[BattlEyeResponseParser] Successfully parsed {players.Count} active BattlEye player(s).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeResponseParser] Critical failure while parsing BattlEye player response: {ex.Message}", ex);
        }

        return players;
    }

    private static bool TryParsePlayerLine(string line, out PlayerModel? player)
    {
        player = null;

        if (line.StartsWith("Players on server:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("[#]", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("---", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("(", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("Connected RCon", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
                LocationState = geo.SubdivisionName
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
            AppLogger.Trace("[BattlEyeResponseParser] Empty BattlEye ban response buffer.");
            return bans;
        }

        try
        {
            var lines = rawResponse.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            AppLogger.Debug($"[BattlEyeResponseParser] Processing {lines.Length} lines for BattlEye ban records...");

            foreach (var rawLine in lines)
            {
                if (TryParseBanLine(rawLine, out var ban) && ban != null)
                {
                    bans.Add(ban);
                }
            }

            AppLogger.Info($"[BattlEyeResponseParser] Successfully parsed {bans.Count} BattlEye ban(s).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BattlEyeResponseParser] Critical failure while parsing BattlEye ban response: {ex.Message}", ex);
        }

        return bans;
    }

    private static bool TryParseBanLine(string line, out BanModel? ban)
    {
        ban = null;

        if (line.StartsWith("GUID Bans:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("IP Bans:", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("[#]", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("---", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
            return true;
        }

        return false;
    }
}