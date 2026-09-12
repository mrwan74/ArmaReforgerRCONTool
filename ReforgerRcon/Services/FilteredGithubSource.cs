using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using Velopack.Sources;

namespace ReforgerRcon.Services;

/// <summary>
/// Specialized, production-hardened GitHub update source that filters out legacy pre-Velopack releases
/// (< v0.9.0-alpha.1) before Velopack inspects release assets, eliminating ArgumentException
/// first-chance noise, HTTP rate-limiting latency, and release feed scanning delays.
/// Features full TRACE through FATAL logging, timing telemetry, and non-swallowing error safety nets.
/// </summary>
public class FilteredGithubSource : GithubSource
{
    private const string ContextErrorMessage = "error_message";
    private const string ContextElapsedMs = "elapsed_ms";

    private readonly string _repoUrl;
    private readonly bool _hasToken;

    public FilteredGithubSource(string repoUrl, string? accessToken = null, bool prerelease = true)
        : base(repoUrl, accessToken, prerelease)
    {
        _repoUrl = repoUrl ?? string.Empty;
        _hasToken = !string.IsNullOrWhiteSpace(accessToken);

        var context = new Dictionary<string, object?>
        {
            ["repo_url"] = AppLogger.SanitizeSensitiveData(_repoUrl),
            ["has_access_token"] = _hasToken,
            ["token_length"] = accessToken?.Length ?? 0,
            ["prerelease"] = prerelease,
            ["thread_id"] = Environment.CurrentManagedThreadId,
            ["process_id"] = Environment.ProcessId
        };

        AppLogger.Debug($"[FilteredGithubSource:Init] Initialized FilteredGithubSource for '{AppLogger.SanitizeSensitiveData(_repoUrl)}' (HasToken={_hasToken}, Prerelease={prerelease}).", context);
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        var taskId = Task.CurrentId?.ToString(CultureInfo.InvariantCulture) ?? "-";

        var context = new Dictionary<string, object?>
        {
            ["repo_url"] = AppLogger.SanitizeSensitiveData(_repoUrl),
            ["include_prereleases"] = includePrereleases,
            ["has_access_token"] = _hasToken,
            ["thread_id"] = threadId,
            ["task_id"] = taskId,
            ["ram_working_set_mb"] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
        };

        AppLogger.Info($"[FilteredGithubSource:GetReleases] Commencing GitHub release query for '{AppLogger.SanitizeSensitiveData(_repoUrl)}' (IncludePrereleases={includePrereleases}, Thread=T{threadId:D2}|Task{taskId})...", context);

        GithubRelease[] allReleases;
        double networkElapsedMs;

        try
        {
            var netStart = Stopwatch.GetTimestamp();
            allReleases = await base.GetReleases(includePrereleases).ConfigureAwait(false);
            networkElapsedMs = Stopwatch.GetElapsedTime(netStart).TotalMilliseconds;
            context["network_latency_ms"] = networkElapsedMs;

            AppLogger.Debug($"[FilteredGithubSource:GetReleases] GitHub REST API responded in {networkElapsedMs:F2}ms with {allReleases?.Length ?? 0} raw release(s).", context);
        }
        catch (OperationCanceledException opEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            AppLogger.Debug($"[FilteredGithubSource:GetReleases] Release query operation was canceled after {elapsedMs:F2}ms: {opEx.Message}", context);
            throw;
        }
        catch (HttpRequestException httpEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context["http_status_code"] = httpEx.StatusCode?.ToString() ?? "None";
            context["http_request_error"] = httpEx.HttpRequestError.ToString();
            context[ContextErrorMessage] = httpEx.Message;

            AppLogger.Error($"[FilteredGithubSource:GetReleases] HTTP network error querying GitHub releases from '{AppLogger.SanitizeSensitiveData(_repoUrl)}' after {elapsedMs:F2}ms (Status: {httpEx.StatusCode}): {httpEx.Message}", httpEx, context);
            ToastNotificationService.Instance.ShowWarning("Update Server Notice", $"Network error contacting update server ({httpEx.StatusCode?.ToString() ?? "No Response"}).");
            throw;
        }
        catch (SocketException sockEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context["socket_error_code"] = sockEx.SocketErrorCode.ToString();
            context["native_error_code"] = sockEx.NativeErrorCode;
            context[ContextErrorMessage] = sockEx.Message;

            AppLogger.Error($"[FilteredGithubSource:GetReleases] Socket/DNS failure contacting GitHub for '{AppLogger.SanitizeSensitiveData(_repoUrl)}' after {elapsedMs:F2}ms: {sockEx.SocketErrorCode} ({sockEx.NativeErrorCode})", sockEx, context);
            ToastNotificationService.Instance.ShowWarning("Network Error", $"Cannot reach GitHub update server: {sockEx.SocketErrorCode}");
            throw;
        }
        catch (JsonException jsonEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context["json_path"] = jsonEx.Path;
            context["json_line"] = jsonEx.LineNumber;
            context[ContextErrorMessage] = jsonEx.Message;

            AppLogger.Error($"[FilteredGithubSource:GetReleases] Failed parsing JSON payload from GitHub releases API after {elapsedMs:F2}ms (Path='{jsonEx.Path}', Line={jsonEx.LineNumber}): {jsonEx.Message}", jsonEx, context);
            ToastNotificationService.Instance.ShowError("Update Feed Error", "Received malformed release feed format from GitHub.");
            throw;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context["exception_type"] = ex.GetType().FullName;
            context[ContextErrorMessage] = ex.Message;
            context["stack_trace"] = ex.StackTrace;

            AppLogger.Fatal($"[FilteredGithubSource:GetReleases] Fatal unhandled exception in base.GetReleases after {elapsedMs:F2}ms: {ex.Message}", ex, context);
            CrashReportService.HandleFatalException("FilteredGithubSource.GetReleases", ex, isTerminating: false);
            ToastNotificationService.Instance.ShowError("Update Check Failure", $"Fatal error checking for updates: {ex.Message}");
            throw;
        }

        if (allReleases == null || allReleases.Length == 0)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            AppLogger.Warn($"[FilteredGithubSource:GetReleases] GitHub returned an empty or null release array for '{AppLogger.SanitizeSensitiveData(_repoUrl)}' after {elapsedMs:F2}ms.", null, context);
            return [];
        }

        var filterStart = Stopwatch.GetTimestamp();
        var eligibleReleases = new List<GithubRelease>(allReleases.Length);
        int skippedEmptyAssets = 0;
        int skippedMissingManifest = 0;
        int skippedLegacyVersion = 0;
        int skippedUnversionedAlpha = 0;

        foreach (var release in allReleases)
        {
            var releaseName = release.Name?.Trim() ?? "(Unnamed Release)";
            var assetCount = release.Assets?.Length ?? 0;
            var assetPreview = release.Assets?.Length > 0
                ? string.Join(", ", release.Assets.Take(5).Select(static a => a.Name ?? "(null)"))
                : "No assets";

            var evalContext = new Dictionary<string, object?>
            {
                ["release_name"] = releaseName,
                ["is_prerelease"] = release.Prerelease,
                ["published_at"] = release.PublishedAt?.ToString("o", CultureInfo.InvariantCulture) ?? "null",
                ["asset_count"] = assetCount,
                ["assets_preview"] = assetPreview
            };

            if (release.Assets == null || release.Assets.Length == 0)
            {
                skippedEmptyAssets++;
                AppLogger.Trace($"[FilteredGithubSource:Filter] Release '{releaseName}' EXCLUDED: Release has zero attached assets.", evalContext);
                continue;
            }

            bool hasManifest = release.Assets.Any(static a => a.Name?.StartsWith("releases.", StringComparison.OrdinalIgnoreCase) is true);
            if (!hasManifest)
            {
                skippedMissingManifest++;
                AppLogger.Trace($"[FilteredGithubSource:Filter] Release '{releaseName}' EXCLUDED: Missing Velopack feed manifest (releases.*.json).", evalContext);
                continue;
            }

            var cleanName = releaseName.TrimStart('v', 'V');
            if (cleanName.StartsWith("0.8.", StringComparison.OrdinalIgnoreCase))
            {
                skippedLegacyVersion++;
                AppLogger.Trace($"[FilteredGithubSource:Filter] Release '{releaseName}' EXCLUDED: Legacy pre-Velopack version (v0.8.x).", evalContext);
                continue;
            }

            if (string.Equals(cleanName, "0.9.0-alpha", StringComparison.OrdinalIgnoreCase))
            {
                skippedUnversionedAlpha++;
                AppLogger.Trace($"[FilteredGithubSource:Filter] Release '{releaseName}' EXCLUDED: Legacy unversioned alpha (v0.9.0-alpha).", evalContext);
                continue;
            }

            eligibleReleases.Add(release);
            AppLogger.Debug($"[FilteredGithubSource:Filter] Release '{releaseName}' ACCEPTED: Valid Velopack release (Assets={assetCount}, Published={release.PublishedAt:yyyy-MM-dd}).", evalContext);
        }

        var filterElapsedMs = Stopwatch.GetElapsedTime(filterStart).TotalMilliseconds;
        var totalElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        context["total_raw_releases"] = allReleases.Length;
        context["eligible_releases_count"] = eligibleReleases.Count;
        context["skipped_empty_assets"] = skippedEmptyAssets;
        context["skipped_missing_manifest"] = skippedMissingManifest;
        context["skipped_legacy_version"] = skippedLegacyVersion;
        context["skipped_unversioned_alpha"] = skippedUnversionedAlpha;
        context["filter_duration_ms"] = filterElapsedMs;
        context["total_duration_ms"] = totalElapsedMs;

        AppLogger.Info($"[FilteredGithubSource:GetReleases] Release filtering finalized in {totalElapsedMs:F2}ms (Network={networkElapsedMs:F2}ms, Filter={filterElapsedMs:F2}ms): {eligibleReleases.Count}/{allReleases.Length} release(s) eligible.", context);

        if (eligibleReleases.Count == 0)
        {
            AppLogger.Warn($"[FilteredGithubSource:GetReleases] All {allReleases.Length} releases were filtered out. No valid Velopack release manifests found on '{AppLogger.SanitizeSensitiveData(_repoUrl)}'.", null, context);
        }

        return [.. eligibleReleases];
    }
}