using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public class ParsedImportBanItem
{
    public string Identity { get; set; } = string.Empty;
    public string BannedName { get; set; } = BanImportExportService.DefaultBannedTargetName;
    public long DurationSeconds { get; set; }
    public string Reason { get; set; } = "Imported Ban";
    public bool IsDuplicate { get; set; }
    public bool IsSelected { get; set; } = true;
    public bool IsIpAddress { get; set; }

    public string DurationFormatted
    {
        get
        {
            if (DurationSeconds == 0)
            {
                return "Permanent";
            }

            if (DurationSeconds < 0)
            {
                return "Expired";
            }

            if (DurationSeconds < 60)
            {
                return "< 1 min";
            }

            return string.Create(CultureInfo.InvariantCulture, $"{Math.Max(1, (int)(DurationSeconds / 60))} min");
        }
    }

    public string StatusBadge => IsDuplicate ? "DUPLICATE" : "NEW";
}

public static partial class BanImportExportService
{
    public const string DefaultBannedTargetName = "Banned Target";

    [GeneratedRegex(@"^\s*(\d+)\s+([a-fA-F0-9]{32}|(?:\[[a-fA-F0-9:]+\]|[\d\.]+))\s+(\w+|-?\d+|-)\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex BattlEyeBanRowRegex();

    [GeneratedRegex(@"^\s*(?:-\s*)?([a-fA-F0-9\-]{36}|[a-fA-F0-9]{32}|(?:\[[a-fA-F0-9:]+\]|[\d\.]+))\s*\|\s*(.*)$", RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex ReforgerPipeBanRowRegex();

    private static string FormatBattlEyeMinutes(long durationSeconds)
    {
        if (durationSeconds == 0)
        {
            return "perm";
        }

        if (durationSeconds < 0)
        {
            return "-";
        }

        return Math.Max(0, (long)(durationSeconds / 60.0)).ToString(CultureInfo.InvariantCulture);
    }

    public static string ExportBansToText(IEnumerable<BanModel> bans, RconProtocol protocol = RconProtocol.ReforgerBuiltIn)
    {
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"BanImportExportService.ExportBansToText({protocol})");

        var context = new Dictionary<string, object?>
        {
            ["protocol"] = protocol.ToString(),
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        try
        {
            var banList = bans.ToList();
            context["total_records"] = banList.Count;
            AppLogger.Info($"[BanImportExport:Export] Starting export serialization of {banList.Count} ban records in {protocol} format...", context);

            var sb = new StringBuilder();

            if (protocol == RconProtocol.ReforgerBuiltIn)
            {
                sb.AppendLine("#ban list");
                sb.AppendLine("Processing Command: #ban list");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Total bans: {banList.Count} | Page: 1/1");
                sb.AppendLine("- Identity Id | Banned name");

                for (int i = 0; i < banList.Count; i++)
                {
                    var b = banList[i];
                    var cleanName = string.IsNullOrWhiteSpace(b.BannedName) || b.BannedName.Equals(DefaultBannedTargetName, StringComparison.OrdinalIgnoreCase)
                        ? "Banned Player"
                        : b.BannedName.Trim();

                    sb.AppendLine(CultureInfo.InvariantCulture, $"- {b.IdentityId} | {cleanName}");

                    if (i < 3 || i == banList.Count - 1)
                    {
                        AppLogger.Trace($"[BanImportExport:ExportRow] Reforger row #{i + 1}: Identity='{b.IdentityId}', Name='{cleanName}'");
                    }
                }
            }
            else
            {
                var guidBans = new List<BanModel>();
                var ipBans = new List<BanModel>();

                foreach (var b in banList)
                {
                    if (IPAddress.TryParse(b.IdentityId.Trim('[', ']'), out _))
                    {
                        ipBans.Add(b);
                    }
                    else
                    {
                        guidBans.Add(b);
                    }
                }

                context["guid_bans_count"] = guidBans.Count;
                context["ip_bans_count"] = ipBans.Count;

                AppLogger.Debug($"[BanImportExport:Export] Categorized BattlEye bans: {guidBans.Count} GUID bans, {ipBans.Count} IP bans.", context);

                sb.AppendLine("bans");
                sb.AppendLine("GUID Bans:");
                sb.AppendLine("[#] [GUID] [Minutes left] [Reason]");
                sb.AppendLine("----------------------------------------");

                int guidIndex = 0;
                foreach (var b in guidBans)
                {
                    string minutesStr = FormatBattlEyeMinutes(b.DurationSeconds);
                    var cleanReason = string.IsNullOrWhiteSpace(b.Reason) ? "testing" : b.Reason.Trim();
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0,-2} {1,-32} {2,-5} {3}", guidIndex++, b.IdentityId, minutesStr, cleanReason).AppendLine();
                }

                sb.AppendLine();
                sb.AppendLine("IP Bans:");
                sb.AppendLine("[#] [IP Address] [Minutes left] [Reason]");
                sb.AppendLine("----------------------------------------------");

                int ipIndex = guidIndex;
                foreach (var b in ipBans)
                {
                    string minutesStr = FormatBattlEyeMinutes(b.DurationSeconds);
                    var cleanReason = string.IsNullOrWhiteSpace(b.Reason) ? "Rule violation" : b.Reason.Trim();
                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0,-2} {1,-15} {2,-5} {3}", ipIndex++, b.IdentityId, minutesStr, cleanReason).AppendLine();
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["generated_bytes"] = Encoding.UTF8.GetByteCount(sb.ToString());
            context["generated_chars"] = sb.Length;
            context["elapsed_ms"] = elapsedMs;

            AppLogger.Info($"[BanImportExport:Export] Generated {banList.Count} ban rows in {elapsedMs:F2}ms (Chars={sb.Length}, Bytes={context["generated_bytes"]}).", context);
            return sb.ToString();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BanImportExport:Export] Critical error during ban export: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Export Generation Failed", $"Failed generating ban export: {ex.Message}");
            throw;
        }
    }

    public static List<ParsedImportBanItem> ParseImportPayload(string rawText, IEnumerable<BanModel> existingBans)
    {
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BanImportExportService.ParseImportPayload");

        var context = new Dictionary<string, object?>
        {
            ["raw_chars"] = rawText?.Length ?? 0,
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        if (string.IsNullOrWhiteSpace(rawText))
        {
            AppLogger.Warn("[BanImportExport:Parse] Payload is empty or whitespace. Returning 0 items.", null, context);
            return [];
        }

        try
        {
            var existingIdentities = new HashSet<string>(
                existingBans.Select(b => b.IdentityId.Trim()),
                StringComparer.OrdinalIgnoreCase
            );
            context["server_bans_for_dedupe"] = existingIdentities.Count;

            var lines = rawText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            context["lines_to_process"] = lines.Length;
            AppLogger.Debug($"[BanImportExport:Parse] Processing {lines.Length} text lines against {existingIdentities.Count} existing server bans...", context);

            var results = new List<ParsedImportBanItem>(lines.Length);
            int skippedHeaders = 0;
            int reforgerPipeCount = 0;
            int battlEyeRowCount = 0;
            int tokenRowCount = 0;
            int malformedCount = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var lineNum = i + 1;

                try
                {
                    if (IsCommentOrHeaderLine(line))
                    {
                        skippedHeaders++;
                        AppLogger.Trace($"[BanImportExport:ParseLine] Line #{lineNum} skipped (Header/Comment): '{line}'");
                        continue;
                    }

                    if (TryParseReforgerPipeFormat(line, out var reforgerItem) && reforgerItem != null)
                    {
                        results.Add(reforgerItem);
                        reforgerPipeCount++;
                        AppLogger.Trace($"[BanImportExport:ParseLine] Line #{lineNum} parsed as ReforgerPipe: Identity='{reforgerItem.Identity}', Name='{reforgerItem.BannedName}', Dur={reforgerItem.DurationSeconds}s");
                        continue;
                    }

                    if (TryParseBattlEyeBansOutput(line, out var beItem) && beItem != null)
                    {
                        results.Add(beItem);
                        battlEyeRowCount++;
                        AppLogger.Trace($"[BanImportExport:ParseLine] Line #{lineNum} parsed as BattlEyeRow: Identity='{beItem.Identity}', Dur={beItem.DurationSeconds}s, Reason='{beItem.Reason}'");
                        continue;
                    }

                    if (TryParseStandardTokenFormat(line, out var tokenItem) && tokenItem != null)
                    {
                        results.Add(tokenItem);
                        tokenRowCount++;
                        AppLogger.Trace($"[BanImportExport:ParseLine] Line #{lineNum} parsed as StandardToken: Identity='{tokenItem.Identity}', Dur={tokenItem.DurationSeconds}s, Reason='{tokenItem.Reason}'");
                        continue;
                    }

                    malformedCount++;
                    AppLogger.Warn($"[BanImportExport:ParseLine] Line #{lineNum} rejected (Unrecognized ban format): '{line}'");
                }
                catch (Exception lineEx)
                {
                    malformedCount++;
                    AppLogger.Error($"[BanImportExport:ParseLine] Exception parsing line #{lineNum} ('{line}'): {lineEx.Message}", lineEx);
                }
            }

            int duplicateCount = 0;
            int ipCount = 0;
            int guidCount = 0;

            foreach (var item in results)
            {
                item.IsIpAddress = IPAddress.TryParse(item.Identity.Trim('[', ']'), out _);
                if (item.IsIpAddress)
                {
                    ipCount++;
                }
                else
                {
                    guidCount++;
                }

                if (existingIdentities.Contains(item.Identity))
                {
                    item.IsDuplicate = true;
                    item.IsSelected = false;
                    duplicateCount++;
                    AppLogger.Trace($"[BanImportExport:Dedupe] Identified duplicate ban: '{item.Identity}' (Already banned on server; deselected by default).");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["total_parsed"] = results.Count;
            context["new_bans"] = results.Count - duplicateCount;
            context["duplicates"] = duplicateCount;
            context["ip_bans"] = ipCount;
            context["guid_bans"] = guidCount;
            context["reforger_matches"] = reforgerPipeCount;
            context["battleye_matches"] = battlEyeRowCount;
            context["token_matches"] = tokenRowCount;
            context["headers_skipped"] = skippedHeaders;
            context["malformed_lines"] = malformedCount;
            context["elapsed_ms"] = elapsedMs;

            AppLogger.Info($"[BanImportExport:Parse] Parse finished in {elapsedMs:F2}ms: Parsed={results.Count} (New={results.Count - duplicateCount}, Dupes={duplicateCount}, IPs={ipCount}, GUIDs={guidCount}, Errors={malformedCount}).", context);
            return results;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BanImportExport:Parse] Critical parse failure: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Import Parse Failed", $"Error processing ban file: {ex.Message}");
            return [];
        }
    }

    private static bool IsCommentOrHeaderLine(string line)
    {
        return line.StartsWith("//", StringComparison.Ordinal) ||
               line.StartsWith("/*", StringComparison.Ordinal) ||
               line.StartsWith("---", StringComparison.Ordinal) ||
               line.StartsWith("Processing Command", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Total bans:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("- Identity Id", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("Identity Id", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("bans", StringComparison.OrdinalIgnoreCase) ||
               line.Equals("#ban list", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("GUID Bans:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("IP Bans:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("[#]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseReforgerPipeFormat(string line, out ParsedImportBanItem? item)
    {
        item = null;
        try
        {
            var match = ReforgerPipeBanRowRegex().Match(line);
            if (match.Success)
            {
                var identity = match.Groups[1].Value.Trim();
                var remainder = match.Groups[2].Value.Trim();

                if (identity.Equals("Identity Id", StringComparison.OrdinalIgnoreCase) ||
                    remainder.Equals("Banned name", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string bannedName = remainder;
                long duration = 0;
                string reason = "Imported Server Ban";

                if (remainder.Contains('|'))
                {
                    var parts = remainder.Split('|', StringSplitOptions.TrimEntries);
                    if (!string.IsNullOrWhiteSpace(parts[0]))
                    {
                        bannedName = parts[0];
                    }

                    if (parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sec))
                    {
                        duration = sec;
                    }

                    if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
                    {
                        reason = parts[2];
                    }
                }

                item = new ParsedImportBanItem
                {
                    Identity = identity,
                    BannedName = string.IsNullOrWhiteSpace(bannedName) ? DefaultBannedTargetName : bannedName,
                    DurationSeconds = duration,
                    Reason = reason
                };
                return true;
            }
        }
        catch (RegexMatchTimeoutException timeoutEx)
        {
            AppLogger.Warn($"[BanImportExport:RegexTimeout] Timeout parsing Reforger pipe format for line '{line}': {timeoutEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[BanImportExport:ReforgerParse] Unexpected failure parsing Reforger pipe line '{line}': {ex.Message}");
        }

        return false;
    }

    private static bool TryParseBattlEyeBansOutput(string line, out ParsedImportBanItem? item)
    {
        item = null;
        try
        {
            var match = BattlEyeBanRowRegex().Match(line);
            if (match.Success)
            {
                var identity = match.Groups[2].Value.Trim();
                var durationToken = match.Groups[3].Value.Trim();
                var rawReason = match.Groups[4].Value.Trim();

                long durationSeconds;
                if (durationToken.Equals("-", StringComparison.OrdinalIgnoreCase) ||
                    durationToken.Equals("expired", StringComparison.OrdinalIgnoreCase))
                {
                    durationSeconds = -1;
                }
                else if (durationToken.Equals("perm", StringComparison.OrdinalIgnoreCase))
                {
                    durationSeconds = 0;
                }
                else if (long.TryParse(durationToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                {
                    durationSeconds = minutes <= 0 ? 30 : minutes * 60;
                }
                else
                {
                    durationSeconds = -1;
                }

                item = new ParsedImportBanItem
                {
                    Identity = identity,
                    BannedName = DefaultBannedTargetName,
                    DurationSeconds = durationSeconds,
                    Reason = string.IsNullOrWhiteSpace(rawReason) ? "Rule violation" : rawReason
                };
                return true;
            }
        }
        catch (RegexMatchTimeoutException timeoutEx)
        {
            AppLogger.Warn($"[BanImportExport:RegexTimeout] Timeout parsing BattlEye format for line '{line}': {timeoutEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[BanImportExport:BEParse] Unexpected failure parsing BattlEye line '{line}': {ex.Message}");
        }

        return false;
    }

    private static bool TryParseStandardTokenFormat(string line, out ParsedImportBanItem? item)
    {
        item = null;
        try
        {
            if (line.Contains(';'))
            {
                var semiParts = line.Split(';', StringSplitOptions.TrimEntries);
                if (semiParts.Length >= 2)
                {
                    var id = semiParts[0];
                    long dur = 0;
                    string reason = semiParts[1];

                    if (semiParts.Length >= 3)
                    {
                        if (semiParts[1].Equals("perm", StringComparison.OrdinalIgnoreCase))
                        {
                            dur = 0;
                        }
                        else if (semiParts[1].Equals("-", StringComparison.OrdinalIgnoreCase) ||
                                 semiParts[1].Equals("expired", StringComparison.OrdinalIgnoreCase))
                        {
                            dur = -1;
                        }
                        else if (long.TryParse(semiParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedDur))
                        {
                            if (parsedDur > 10000)
                            {
                                dur = parsedDur;
                            }
                            else if (parsedDur <= 0)
                            {
                                dur = 30;
                            }
                            else
                            {
                                dur = parsedDur * 60;
                            }
                        }
                        else
                        {
                            dur = -1;
                        }
                        reason = semiParts[2];
                    }

                    if (id.Length >= 7)
                    {
                        item = new ParsedImportBanItem
                        {
                            Identity = id,
                            BannedName = DefaultBannedTargetName,
                            DurationSeconds = dur,
                            Reason = reason
                        };
                        return true;
                    }
                }
            }

            var tokens = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2)
            {
                int startIdx = 0;
                if (int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
                    tokens.Length >= 3 &&
                    (tokens[1].Length >= 8 || tokens[1].Contains('.')))
                {
                    startIdx = 1;
                }

                var identity = tokens[startIdx].Trim();
                long durationSeconds = 0;
                string reason = "Imported Ban";

                if (startIdx + 1 < tokens.Length)
                {
                    var durToken = tokens[startIdx + 1].Trim();
                    if (durToken.Equals("-", StringComparison.OrdinalIgnoreCase) ||
                        durToken.Equals("expired", StringComparison.OrdinalIgnoreCase))
                    {
                        durationSeconds = -1;
                        if (startIdx + 2 < tokens.Length)
                        {
                            reason = string.Join(' ', tokens.Skip(startIdx + 2)).Trim();
                        }
                    }
                    else if (durToken.Equals("perm", StringComparison.OrdinalIgnoreCase))
                    {
                        durationSeconds = 0;
                        if (startIdx + 2 < tokens.Length)
                        {
                            reason = string.Join(' ', tokens.Skip(startIdx + 2)).Trim();
                        }
                    }
                    else if (long.TryParse(durToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
                    {
                        durationSeconds = minutes <= 0 ? 30 : minutes * 60;
                        if (startIdx + 2 < tokens.Length)
                        {
                            reason = string.Join(' ', tokens.Skip(startIdx + 2)).Trim();
                        }
                    }
                    else
                    {
                        reason = string.Join(' ', tokens.Skip(startIdx + 1)).Trim();
                    }
                }

                if (identity.Length >= 7)
                {
                    item = new ParsedImportBanItem
                    {
                        Identity = identity,
                        BannedName = DefaultBannedTargetName,
                        DurationSeconds = durationSeconds,
                        Reason = reason
                    };
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[BanImportExport:TokenParse] Token parse exception for line '{line}': {ex.Message}");
        }

        return false;
    }
}