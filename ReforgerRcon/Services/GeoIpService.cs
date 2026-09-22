using MaxMind.Db;
using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using ReforgerRcon.Models;
using Sentry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TimeZoneConverter;

namespace ReforgerRcon.Services;

public record GeoLocationResult(
    string CountryCode,
    string CountryName,
    string CityName,
    string SubdivisionName,
    string PostalCode,
    double? Latitude,
    double? Longitude,
    string TimeZone,
    string NaturalLocation);

public enum GeoIpStepStatus
{
    Pending,
    InProgress,
    Completed,
    Skipped,
    Failed
}

public class GeoIpProgressReport
{
    public string CurrentOperation { get; set; } = string.Empty;
    public double OverallProgressPercentage { get; set; }
    public string DetailLog { get; set; } = string.Empty;
    public GeoIpStepStatus CityStatus { get; set; } = GeoIpStepStatus.Pending;
    public GeoIpStepStatus CountryStatus { get; set; } = GeoIpStepStatus.Pending;
    public string CityStatusMessage { get; set; } = "Waiting...";
    public string CountryStatusMessage { get; set; } = "Waiting...";
    public bool IsIndeterminate { get; set; }
}

public record GeoIpDownloadRequest(
    string EditionId,
    string TargetMmdbPath,
    string AccountId,
    string LicenseKey,
    bool Force,
    double StartPercentage,
    double EndPercentage);

public static class LocationFormatter
{
    public const string UnknownRegion = "Unknown Region";

    public static string FormatNatural(
        string countryCode,
        string countryName,
        string? cityName,
        IReadOnlyList<(string Name, string IsoCode)> subdivisions)
    {
        if (string.IsNullOrWhiteSpace(countryName) ||
            countryCode.Equals("xx", StringComparison.OrdinalIgnoreCase) ||
            countryName.Equals("Direct Reforger Server", StringComparison.OrdinalIgnoreCase) ||
            (countryCode.Equals("un", StringComparison.OrdinalIgnoreCase) && !string.Equals(countryName.Trim(), "United Nations", StringComparison.OrdinalIgnoreCase)))
        {
            return UnknownRegion;
        }

        var cleanCity = string.IsNullOrWhiteSpace(cityName) ||
                        cityName.Equals("Connected Region", StringComparison.OrdinalIgnoreCase) ||
                        cityName.Equals("Unknown City", StringComparison.OrdinalIgnoreCase)
            ? null
            : cityName.Trim();

        var code = countryCode.Trim().ToUpperInvariant();

        if (code == "US")
        {
            if (subdivisions.Count > 0)
            {
                var (stateName, stateIso) = subdivisions[^1];
                var stateStr = !string.IsNullOrWhiteSpace(stateIso) && !string.Equals(stateName, stateIso, StringComparison.OrdinalIgnoreCase)
                    ? $"{stateName} ({stateIso})"
                    : stateName;

                return !string.IsNullOrEmpty(cleanCity)
                    ? $"{cleanCity}, {stateStr}, {countryName}"
                    : $"{stateStr}, {countryName}";
            }

            return !string.IsNullOrEmpty(cleanCity) ? $"{cleanCity}, {countryName}" : countryName;
        }

        if (code == "GB")
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(cleanCity))
            {
                parts.Add(cleanCity);
            }

            if (subdivisions.Count >= 2)
            {
                var constituent = subdivisions[0].Name;
                var county = subdivisions[1].Name;

                if (!string.Equals(county, cleanCity, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(county);
                }
                if (!string.Equals(constituent, county, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(constituent);
                }
            }
            else if (subdivisions.Count == 1)
            {
                var sub = subdivisions[0].Name;
                if (!string.Equals(sub, cleanCity, StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(sub);
                }
            }

            parts.Add(countryName);
            return string.Join(", ", parts);
        }

        if (code is "CA" or "AU" && subdivisions.Count > 0)
        {
            var (subName, subIso) = subdivisions[^1];
            var subStr = !string.IsNullOrWhiteSpace(subIso) && !string.Equals(subName, subIso, StringComparison.OrdinalIgnoreCase)
                ? $"{subName} ({subIso})"
                : subName;

            return !string.IsNullOrEmpty(cleanCity)
                ? $"{cleanCity}, {subStr}, {countryName}"
                : $"{subStr}, {countryName}";
        }

        var generalParts = new List<string>();
        if (!string.IsNullOrEmpty(cleanCity))
        {
            generalParts.Add(cleanCity);
        }

        if (subdivisions.Count > 0)
        {
            var mostSpecific = subdivisions[^1].Name;
            if (!string.Equals(mostSpecific, cleanCity, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(mostSpecific, countryName, StringComparison.OrdinalIgnoreCase))
            {
                generalParts.Add(mostSpecific);
            }
        }

        generalParts.Add(countryName);
        return string.Join(", ", generalParts);
    }

    public static string FormatLocalTime(string? ianaTimeZone)
    {
        if (string.IsNullOrWhiteSpace(ianaTimeZone))
        {
            return "Unknown Timezone";
        }

        var trimmedZone = ianaTimeZone.Trim();

        try
        {
            if (TZConvert.TryGetTimeZoneInfo(trimmedZone, out var tzInfo))
            {
                var nowUtc = DateTime.UtcNow;
                var localTime = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tzInfo);
                var offset = tzInfo.GetUtcOffset(nowUtc);
                var offsetSign = offset >= TimeSpan.Zero ? "+" : "-";
                var offsetHours = Math.Abs(offset.Hours);
                var offsetMinutes = Math.Abs(offset.Minutes);
                var offsetStr = offsetMinutes == 0
                    ? $"UTC{offsetSign}{offsetHours}"
                    : $"UTC{offsetSign}{offsetHours}:{offsetMinutes:D2}";

                return $"{localTime:hh:mm tt} ({trimmedZone}, {offsetStr})";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[LocationFormatter:TimeZone] TimeZone notice for '{trimmedZone}': {ex.Message}");
        }

        return trimmedZone;
    }
}

public static class GeoIpService
{
    private const string GeoIpUpdateCycleCompletedEvent = "geoip_update_cycle_completed";
    private const string IsForcedKey = "is_forced";
    private const string CityStatusKey = "city_status";
    private const string CountryStatusKey = "country_status";
    private const string HasCustomCredentialsKey = "has_custom_credentials";
    private const string DurationMsKey = "duration_ms";
    private const string SuccessKey = "success";
    private const string StatusFailed = "Failed";

    private static readonly string GeoIpDirectory = AppPaths.GeoIpDirectory;
    private static readonly string ConfFile = Path.Combine(GeoIpDirectory, "GeoIP.conf");

    private static readonly string CityDbPath = Path.Combine(GeoIpDirectory, "GeoLite2-City.mmdb");
    private static readonly string CountryDbPath = Path.Combine(GeoIpDirectory, "GeoLite2-Country.mmdb");

    [SuppressMessage("Security", "S1313:Do not hardcode IP addresses", Justification = "Standard public DNS probe address used solely to prime memory-mapped database index structures")]
    private static readonly IPAddress PrewarmProbeAddress = new([8, 8, 8, 8]);

    private static volatile DatabaseReader? _cityReader;
    private static volatile DatabaseReader? _countryReader;
    private static volatile bool _isInitialized;
    private static readonly Lock ReaderLock = new();
    private static readonly ConcurrentDictionary<string, GeoLocationResult> LookupCache = new(StringComparer.OrdinalIgnoreCase);

    private static CancellationTokenSource? _periodicUpdateCts;
    private static (string AccountId, string LicenseKey)? _cachedCredentials;

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10
    })
    {
        Timeout = TimeSpan.FromMinutes(4)
    };

    public static bool IsCityDbLoaded => _cityReader != null;
    public static bool IsCountryDbLoaded => _countryReader != null;
    public static bool HasCustomCredentials
    {
        get
        {
            var (acc, key) = ResolveCredentials();
            return !string.IsNullOrWhiteSpace(acc) && !string.IsNullOrWhiteSpace(key);
        }
    }

    public static DateTime? CityDbLastModified
    {
        get
        {
            var resolved = ResolveEffectiveDbPath("GeoLite2-City.mmdb", CityDbPath);
            return File.Exists(resolved) ? File.GetLastWriteTimeUtc(resolved) : null;
        }
    }

    public static DateTime? CountryDbLastModified
    {
        get
        {
            var resolved = ResolveEffectiveDbPath("GeoLite2-Country.mmdb", CountryDbPath);
            return File.Exists(resolved) ? File.GetLastWriteTimeUtc(resolved) : null;
        }
    }

    public static bool IsUpdating { get; private set; }

    public static event Action? DatabasesUpdated;

    public static void PrewarmReaders()
    {
        if (_isInitialized) return;

        lock (ReaderLock)
        {
            if (_isInitialized) return;

            var start = Stopwatch.GetTimestamp();
            try
            {
                if (!Directory.Exists(GeoIpDirectory))
                {
                    Directory.CreateDirectory(GeoIpDirectory);
                }

                ReloadReaders();
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Info($"[GeoIpService:Prewarm] GeoIP MMDB readers memory-mapped in {elapsedMs:F2}ms.");
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[GeoIpService:Prewarm] Notice during pre-warm: {ex.Message}");
            }
        }
    }

    public static void Initialize()
    {
        PrewarmReaders();

        if (HasCustomCredentials)
        {
            _periodicUpdateCts?.Cancel();
            _periodicUpdateCts?.Dispose();
            _periodicUpdateCts = new CancellationTokenSource();

            var token = _periodicUpdateCts.Token;
            _ = Task.Run(() => UpdateDatabasesAsync(force: false, progress: null, token), token);
            _ = StartPeriodicUpdateLoopAsync(token);
        }
    }

    public static void EnsureReadersLoaded()
    {
        if (_isInitialized) return;

        lock (ReaderLock)
        {
            if (_isInitialized) return;
            EnsureInitialized();
        }
    }

    private static void EnsureInitialized()
    {
        if (_isInitialized) return;

        lock (ReaderLock)
        {
            if (_isInitialized) return;

            if (!Directory.Exists(GeoIpDirectory))
            {
                Directory.CreateDirectory(GeoIpDirectory);
            }

            ReloadReaders();
            _isInitialized = true;
        }
    }

    public static void Shutdown()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        AppLogger.Info("[GeoIpService:Shutdown] Shutting down GeoIP workers...");
        if (_periodicUpdateCts != null)
        {
            try
            {
                _periodicUpdateCts.Cancel();
                _periodicUpdateCts.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                AppLogger.Trace($"[GeoIpService:Shutdown] CTS already disposed: {ex.Message}");
            }
            _periodicUpdateCts = null;
        }

        lock (ReaderLock)
        {
            _cityReader?.Dispose();
            _cityReader = null;
            _countryReader?.Dispose();
            _countryReader = null;
            LookupCache.Clear();
            _isInitialized = false;
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Debug($"[GeoIpService:Shutdown] GeoIP shutdown complete in {elapsedMs:F2}ms.");
    }

    [SuppressMessage("AsyncUsage", "PH_P008:ThrowOperationCanceledException", Justification = "Background update loop terminates cleanly without throwing")]
    private static async Task StartPeriodicUpdateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SafeDelayAsync(TimeSpan.FromHours(12), cancellationToken).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested) break;

                AppLogger.Info("[GeoIpService:Periodic] 12-hour timer tick: checking for updated databases...");
                await UpdateDatabasesAsync(force: false, progress: null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[GeoIpService:Periodic] Exception in update loop: {ex.Message}", ex);
            }
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || delay <= TimeSpan.Zero) return;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state => ((TaskCompletionSource?)state)?.TrySetResult(), tcs).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.Delay(delay, CancellationToken.None), tcs.Task).ConfigureAwait(false);
        }
    }

    private static string ResolveEffectiveDbPath(string fileName, string customPath)
    {
        if (File.Exists(customPath)) return customPath;

        string[] potentialPaths =
        [
            Path.Combine(AppContext.BaseDirectory, "GeoIP", fileName),
            Path.Combine(AppContext.BaseDirectory, "geoip", fileName),
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            Path.Combine(AppContext.BaseDirectory, "assets", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        ];

        return potentialPaths.FirstOrDefault(File.Exists) ?? customPath;
    }

    public static (string AccountId, string LicenseKey) ResolveCredentials()
    {
        if (_cachedCredentials.HasValue)
        {
            return _cachedCredentials.Value;
        }

        var envAccount = Environment.GetEnvironmentVariable("MAXMIND_ACCOUNT_ID");
        var envKey = Environment.GetEnvironmentVariable("MAXMIND_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(envAccount) && !string.IsNullOrWhiteSpace(envKey))
        {
            _cachedCredentials = (envAccount.Trim(), envKey.Trim());
            return _cachedCredentials.Value;
        }

        try
        {
            var settings = AppSettings.LoadFromDisk();
            if (!string.IsNullOrWhiteSpace(settings.MaxMindAccountId) && !string.IsNullOrWhiteSpace(settings.MaxMindLicenseKey))
            {
                _cachedCredentials = (settings.MaxMindAccountId.Trim(), settings.MaxMindLicenseKey.Trim());
                return _cachedCredentials.Value;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService:Credentials] Settings parsing notice: {ex.Message}");
        }

        try
        {
            if (File.Exists(ConfFile))
            {
                string parsedAccount = string.Empty;
                string parsedKey = string.Empty;

                foreach (var line in File.ReadAllLines(ConfFile))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith('#') || string.IsNullOrEmpty(trimmed)) continue;

                    var parts = trimmed.Split([' ', '\t', '='], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        if (parts[0].Equals("AccountID", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("UserId", StringComparison.OrdinalIgnoreCase))
                        {
                            parsedAccount = parts[1];
                        }
                        else if (parts[0].Equals("LicenseKey", StringComparison.OrdinalIgnoreCase))
                        {
                            parsedKey = parts[1];
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(parsedAccount) && !string.IsNullOrWhiteSpace(parsedKey))
                {
                    _cachedCredentials = (parsedAccount.Trim(), parsedKey.Trim());
                    return _cachedCredentials.Value;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService:Credentials] Conf file parsing notice: {ex.Message}");
        }

        _cachedCredentials = (string.Empty, string.Empty);
        return _cachedCredentials.Value;
    }

    public static void InvalidateCredentialsCache()
    {
        _cachedCredentials = null;
    }

    public static void ReloadReaders()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        lock (ReaderLock)
        {
            try
            {
                _cityReader?.Dispose();
                _cityReader = null;

                var cityPath = ResolveEffectiveDbPath("GeoLite2-City.mmdb", CityDbPath);
                if (File.Exists(cityPath))
                {
                    _cityReader = new DatabaseReader(cityPath, FileAccessMode.MemoryMapped);
                    AppLogger.Info($"[GeoIpService:Reader] Loaded DatabaseReader for GeoLite2-City in MemoryMapped mode ({new FileInfo(cityPath).Length / 1024} KB).");

                    try
                    {
                        _cityReader.TryCity(PrewarmProbeAddress, out _);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Trace($"[GeoIpService:Reader] City prewarm probe notice: {ex.Message}");
                    }
                }
                else
                {
                    AppLogger.Warn($"[GeoIpService:Reader] GeoLite2-City missing at '{cityPath}'.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[GeoIpService:Reader] Error initializing City reader in MemoryMapped mode: {ex.Message}", ex);
            }

            try
            {
                _countryReader?.Dispose();
                _countryReader = null;

                var countryPath = ResolveEffectiveDbPath("GeoLite2-Country.mmdb", CountryDbPath);
                if (File.Exists(countryPath))
                {
                    _countryReader = new DatabaseReader(countryPath, FileAccessMode.MemoryMapped);
                    AppLogger.Info($"[GeoIpService:Reader] Loaded DatabaseReader for GeoLite2-Country in MemoryMapped mode ({new FileInfo(countryPath).Length / 1024} KB).");

                    try
                    {
                        _countryReader.TryCountry(PrewarmProbeAddress, out _);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Trace($"[GeoIpService:Reader] Country prewarm probe notice: {ex.Message}");
                    }
                }
                else
                {
                    AppLogger.Warn($"[GeoIpService:Reader] GeoLite2-Country missing at '{countryPath}'.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[GeoIpService:Reader] Error initializing Country reader in MemoryMapped mode: {ex.Message}", ex);
            }

            LookupCache.Clear();
            _isInitialized = true;
        }

        try
        {
            DatabasesUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService:Reader] DatabasesUpdated subscriber notice: {ex.Message}");
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AppLogger.Info($"[GeoIpService:Reader] ReloadReaders complete in {elapsedMs:F2}ms.");
    }

    public static bool TryGetCachedLocation(string? ip, out GeoLocationResult result)
    {
        if (!string.IsNullOrWhiteSpace(ip) && LookupCache.TryGetValue(ip, out var cached) && cached.CountryCode != "xx")
        {
            result = cached;
            return true;
        }

        result = new GeoLocationResult("xx", LocationFormatter.UnknownRegion, string.Empty, string.Empty, string.Empty, null, null, string.Empty, LocationFormatter.UnknownRegion);
        return false;
    }

    public static GeoLocationResult GetLocation(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return new GeoLocationResult("xx", LocationFormatter.UnknownRegion, string.Empty, string.Empty, string.Empty, null, null, string.Empty, LocationFormatter.UnknownRegion);
        }

        if (LookupCache.TryGetValue(ip, out var cached) && cached.CountryCode != "xx")
        {
            return cached;
        }

        if (!IPAddress.TryParse(ip, out var parsedIp))
        {
            return new GeoLocationResult("xx", LocationFormatter.UnknownRegion, string.Empty, string.Empty, string.Empty, null, null, string.Empty, LocationFormatter.UnknownRegion);
        }

        if (IsPrivateOrLoopbackIp(parsedIp))
        {
            var localResult = new GeoLocationResult("xx", LocationFormatter.UnknownRegion, "Local Subnet", "LAN", string.Empty, 0.0, 0.0, TimeZoneInfo.Local.Id, LocationFormatter.UnknownRegion);
            LookupCache[ip] = localResult;
            return localResult;
        }

        EnsureReadersLoaded();

        DatabaseReader? cityReader;
        DatabaseReader? countryReader;
        lock (ReaderLock)
        {
            cityReader = _cityReader;
            countryReader = _countryReader;
        }

        if (cityReader != null)
        {
            try
            {
                if (cityReader.TryCity(parsedIp, out var cityResponse) && cityResponse != null)
                {
                    var countryCode = !string.IsNullOrEmpty(cityResponse.Country.IsoCode) ? cityResponse.Country.IsoCode.ToLowerInvariant() : "xx";
                    var countryName = !string.IsNullOrEmpty(cityResponse.Country.Name) ? cityResponse.Country.Name : LocationFormatter.UnknownRegion;
                    var cityName = !string.IsNullOrEmpty(cityResponse.City.Name) ? cityResponse.City.Name : string.Empty;
                    var stateName = !string.IsNullOrEmpty(cityResponse.MostSpecificSubdivision.Name) ? cityResponse.MostSpecificSubdivision.Name : countryName;
                    var postal = cityResponse.Postal.Code ?? string.Empty;
                    var lat = cityResponse.Location.Latitude;
                    var lon = cityResponse.Location.Longitude;
                    var timeZone = cityResponse.Location.TimeZone ?? string.Empty;

                    var subList = cityResponse.Subdivisions
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                        .Select(s => (s.Name!, s.IsoCode ?? string.Empty))
                        .ToList();

                    var naturalLoc = LocationFormatter.FormatNatural(countryCode, countryName, cityName, subList);
                    var result = new GeoLocationResult(countryCode, countryName, cityName, stateName, postal, lat, lon, timeZone, naturalLoc);
                    LookupCache[ip] = result;

                    FlagAssetService.PrewarmFlag(countryCode);
                    return result;
                }
            }
            catch (AddressNotFoundException)
            {
                AppLogger.Trace($"[GeoIpService:Lookup] Address not found in city database: {ip}");
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[GeoIpService:Lookup] City lookup notice for {ip}: {ex.Message}");
            }
        }

        if (countryReader != null)
        {
            try
            {
                if (countryReader.TryCountry(parsedIp, out var countryResponse) && countryResponse != null)
                {
                    var countryCode = !string.IsNullOrEmpty(countryResponse.Country.IsoCode) ? countryResponse.Country.IsoCode.ToLowerInvariant() : "xx";
                    var countryName = !string.IsNullOrEmpty(countryResponse.Country.Name) ? countryResponse.Country.Name : LocationFormatter.UnknownRegion;

                    var result = new GeoLocationResult(countryCode, countryName, string.Empty, countryName, string.Empty, null, null, string.Empty, countryName);
                    LookupCache[ip] = result;

                    FlagAssetService.PrewarmFlag(countryCode);
                    return result;
                }
            }
            catch (AddressNotFoundException)
            {
                AppLogger.Trace($"[GeoIpService:Lookup] Address not found in country database: {ip}");
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[GeoIpService:Lookup] Country lookup notice for {ip}: {ex.Message}");
            }
        }

        var fallbackResult = new GeoLocationResult("xx", LocationFormatter.UnknownRegion, string.Empty, string.Empty, string.Empty, null, null, string.Empty, LocationFormatter.UnknownRegion);
        if (cityReader != null || countryReader != null)
        {
            LookupCache[ip] = fallbackResult;
        }
        return fallbackResult;
    }

    public static CountryInfo GetCountryForIp(string ip)
    {
        var loc = GetLocation(ip);
        return new CountryInfo
        {
            Code = loc.CountryCode,
            Name = loc.CountryName
        };
    }

    public static async Task<bool> UpdateDatabasesAsync(
        bool force = false,
        IProgress<GeoIpProgressReport>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsUpdating)
        {
            AppLogger.Warn("[GeoIpService:Update] Skipped: update already in progress.");
            return false;
        }

        var updateStartTimestamp = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"GeoIpService.UpdateDatabasesAsync(Force: {force})");
        var report = new GeoIpProgressReport();

        var (accountId, licenseKey) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(licenseKey))
        {
            report.CurrentOperation = "Authentication Credentials Missing";
            report.DetailLog = "MaxMind Account ID and License Key required for live updates.";
            report.CityStatus = GeoIpStepStatus.Failed;
            report.CountryStatus = GeoIpStepStatus.Failed;
            progress?.Report(report);

            AppLogger.Warn("[GeoIpService:Update] Update aborted: MaxMind credentials missing.");

            AppLogger.TrackEvent(GeoIpUpdateCycleCompletedEvent, new Dictionary<string, object>
            {
                [IsForcedKey] = force,
                [CityStatusKey] = StatusFailed,
                [CountryStatusKey] = StatusFailed,
                [HasCustomCredentialsKey] = false,
                [DurationMsKey] = Stopwatch.GetElapsedTime(updateStartTimestamp).TotalMilliseconds,
                [SuccessKey] = false
            });

            if (force)
            {
                ToastNotificationService.Instance.ShowToast(
                    "MaxMind Credentials Required",
                    "Using offline pre-bundled databases. Enter credentials in Settings to update.",
                    "GEOIP_AUTH_NOTICE"
                );
            }
            return false;
        }

        IsUpdating = true;
        try
        {
            report.CurrentOperation = "Connecting to MaxMind servers...";
            report.OverallProgressPercentage = 5;
            report.DetailLog = $"Connecting for Account ID: {accountId}...";
            progress?.Report(report);

            AppLogger.Info($"[GeoIpService:Update] Starting MaxMind update (Account: {accountId}, Force: {force})...");

            report.CurrentOperation = "Updating GeoLite2-City Database...";
            report.CityStatus = GeoIpStepStatus.InProgress;
            report.CityStatusMessage = "Checking version...";
            report.OverallProgressPercentage = 15;
            report.DetailLog = "Checking remote timestamp for GeoLite2-City.mmdb...";
            progress?.Report(report);

            var cityReq = new GeoIpDownloadRequest("GeoLite2-City", CityDbPath, accountId, licenseKey, force, 15, 55);
            bool cityUpdated = await CheckAndUpdateEditionAsync(cityReq, progress, report, cancellationToken).ConfigureAwait(false);
            report.CityStatus = cityUpdated ? GeoIpStepStatus.Completed : GeoIpStepStatus.Skipped;
            report.CityStatusMessage = cityUpdated ? "Updated successfully" : "Already up to date";
            report.OverallProgressPercentage = 55;
            progress?.Report(report);

            report.CurrentOperation = "Updating GeoLite2-Country Database...";
            report.CountryStatus = GeoIpStepStatus.InProgress;
            report.CountryStatusMessage = "Checking version...";
            report.OverallProgressPercentage = 60;
            report.DetailLog = "Checking remote timestamp for GeoLite2-Country.mmdb...";
            progress?.Report(report);

            var countryReq = new GeoIpDownloadRequest("GeoLite2-Country", CountryDbPath, accountId, licenseKey, force, 60, 95);
            bool countryUpdated = await CheckAndUpdateEditionAsync(countryReq, progress, report, cancellationToken).ConfigureAwait(false);
            report.CountryStatus = countryUpdated ? GeoIpStepStatus.Completed : GeoIpStepStatus.Skipped;
            report.CountryStatusMessage = countryUpdated ? "Updated successfully" : "Already up to date";
            report.OverallProgressPercentage = 95;
            progress?.Report(report);

            report.CurrentOperation = "Reloading Readers...";
            report.DetailLog = "Re-initializing binary MMDB readers in MemoryMapped mode...";
            progress?.Report(report);

            ReloadReaders();

            report.OverallProgressPercentage = 100;
            report.CurrentOperation = "Databases Synchronized";
            report.DetailLog = "MaxMind database synchronization completed successfully.";
            progress?.Report(report);

            var totalElapsedMs = Stopwatch.GetElapsedTime(updateStartTimestamp).TotalMilliseconds;

            AppLogger.TrackEvent(GeoIpUpdateCycleCompletedEvent, new Dictionary<string, object>
            {
                [IsForcedKey] = force,
                [CityStatusKey] = report.CityStatus.ToString(),
                [CountryStatusKey] = report.CountryStatus.ToString(),
                [HasCustomCredentialsKey] = true,
                [DurationMsKey] = totalElapsedMs,
                [SuccessKey] = cityUpdated || countryUpdated
            });

            AppLogger.Info($"[GeoIpService:Update] Sync finished in {totalElapsedMs:F2}ms (CityUpdated={cityUpdated}, CountryUpdated={countryUpdated}).");

            if (cityUpdated || countryUpdated || force)
            {
                ToastNotificationService.Instance.ShowToast(
                    "GeoIP Databases Updated",
                    "MaxMind GeoLite2 databases refreshed and active in memory.",
                    "GEOIP_UPDATE"
                );
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            report.CurrentOperation = "Update Canceled";
            report.DetailLog = "Update canceled by operator.";
            progress?.Report(report);

            AppLogger.TrackEvent(GeoIpUpdateCycleCompletedEvent, new Dictionary<string, object>
            {
                [IsForcedKey] = force,
                [CityStatusKey] = "Canceled",
                [CountryStatusKey] = "Canceled",
                [HasCustomCredentialsKey] = true,
                [DurationMsKey] = Stopwatch.GetElapsedTime(updateStartTimestamp).TotalMilliseconds,
                [SuccessKey] = false
            });

            AppLogger.Info("[GeoIpService:Update] Update canceled by operator.");
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            AppLogger.Error($"[GeoIpService:Update] HTTP error during update: {httpEx.Message}", httpEx);
            report.CurrentOperation = "Network Failure";
            report.DetailLog = $"HTTP failure: {httpEx.Message}";
            progress?.Report(report);

            AppLogger.TrackEvent(GeoIpUpdateCycleCompletedEvent, new Dictionary<string, object>
            {
                [IsForcedKey] = force,
                [CityStatusKey] = StatusFailed,
                [CountryStatusKey] = StatusFailed,
                [HasCustomCredentialsKey] = true,
                [DurationMsKey] = Stopwatch.GetElapsedTime(updateStartTimestamp).TotalMilliseconds,
                [SuccessKey] = false
            });

            ToastNotificationService.Instance.ShowError("GeoIP Update Failed", $"HTTP error: {httpEx.Message}");
            return false;
        }
        catch (IOException ioEx)
        {
            AppLogger.Error($"[GeoIpService:Update] I/O error extracting databases: {ioEx.Message}", ioEx);
            report.CurrentOperation = "Disk Extraction Failure";
            report.DetailLog = $"File write failure: {ioEx.Message}";
            progress?.Report(report);

            AppLogger.TrackEvent(GeoIpUpdateCycleCompletedEvent, new Dictionary<string, object>
            {
                [IsForcedKey] = force,
                [CityStatusKey] = StatusFailed,
                [CountryStatusKey] = StatusFailed,
                [HasCustomCredentialsKey] = true,
                [DurationMsKey] = Stopwatch.GetElapsedTime(updateStartTimestamp).TotalMilliseconds,
                [SuccessKey] = false
            });

            ToastNotificationService.Instance.ShowError("GeoIP Disk Error", $"I/O failure: {ioEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[GeoIpService:Update] Error updating GeoIP databases: {ex.Message}", ex);
            report.CurrentOperation = "Unexpected Error";
            report.DetailLog = $"Failed updating databases: {ex.Message}";
            progress?.Report(report);

            AppLogger.TrackEvent(GeoIpUpdateCycleCompletedEvent, new Dictionary<string, object>
            {
                [IsForcedKey] = force,
                [CityStatusKey] = StatusFailed,
                [CountryStatusKey] = StatusFailed,
                [HasCustomCredentialsKey] = true,
                [DurationMsKey] = Stopwatch.GetElapsedTime(updateStartTimestamp).TotalMilliseconds,
                [SuccessKey] = false
            });

            ToastNotificationService.Instance.ShowToast("GeoIP Update Error", $"Failed updating databases: {ex.Message}", "GEOIP_UPDATE_ERR");
            return false;
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private static async Task<bool> CheckAndUpdateEditionAsync(
        GeoIpDownloadRequest request,
        IProgress<GeoIpProgressReport>? progress,
        GeoIpProgressReport report,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return false;
        var stepStart = Stopwatch.GetTimestamp();
        var downloadUrl = $"https://download.maxmind.com/geoip/databases/{request.EditionId}/download?suffix=tar.gz";
        var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{request.AccountId}:{request.LicenseKey}"));

        DateTimeOffset? remoteLastModified = null;
        AppLogger.Debug($"[GeoIpService:Download] HEAD request to {downloadUrl} for {request.EditionId}...");

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
            headRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            using var headResponse = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (headResponse.IsSuccessStatusCode)
            {
                remoteLastModified = headResponse.Content.Headers.LastModified ?? headResponse.Headers.Date;
                report.DetailLog = $"[{request.EditionId}] Remote timestamp: {remoteLastModified:yyyy-MM-dd HH:mm:ss UTC}";
                progress?.Report(report);
                AppLogger.Info($"[{request.EditionId}] Remote Last-Modified: {remoteLastModified}");
            }
            else if (headResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                report.DetailLog = $"[{request.EditionId}] Authentication rejected: Account ID or License Key is invalid.";
                progress?.Report(report);
                AppLogger.Warn($"[{request.EditionId}] HTTP 401 Unauthorized from MaxMind.");
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService:Download] Head check notice for '{request.EditionId}': {ex.Message}");
        }

        if (!request.Force && File.Exists(request.TargetMmdbPath) && remoteLastModified.HasValue)
        {
            var localLastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(request.TargetMmdbPath), TimeSpan.Zero);
            if (remoteLastModified.Value <= localLastWriteTime)
            {
                report.DetailLog = $"[{request.EditionId}] Local file is current ({localLastWriteTime:yyyy-MM-dd}). Skipping download.";
                progress?.Report(report);
                AppLogger.Info($"[{request.EditionId}] Local database is current. Skipping download.");
                return false;
            }
        }

        report.DetailLog = $"[{request.EditionId}] Downloading archive payload (.tar.gz)...";
        report.OverallProgressPercentage = (request.StartPercentage + request.EndPercentage) / 2.0;
        progress?.Report(report);

        AppLogger.Info($"[GeoIpService:Download] Downloading archive for {request.EditionId}...");

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

        using var getResponse = await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!getResponse.IsSuccessStatusCode)
        {
            report.DetailLog = $"[{request.EditionId}] Download failed: {getResponse.StatusCode}.";
            progress?.Report(report);
            AppLogger.Error($"[{request.EditionId}] Download failed: {getResponse.StatusCode}");
            return false;
        }

        report.DetailLog = $"[{request.EditionId}] Decompressing GZip stream & extracting MMDB...";
        progress?.Report(report);

        await using var compressedStream = await getResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
        await using var tarReader = new TarReader(gzipStream);

        string tempExtractFile = $"{request.TargetMmdbPath}.tmp";

        while (await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false) is { } entry)
        {
            if (entry.Name.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Debug($"[{request.EditionId}] Found target entry '{entry.Name}'. Extracting to '{tempExtractFile}'...");
                await entry.ExtractToFileAsync(tempExtractFile, overwrite: true, cancellationToken: cancellationToken).ConfigureAwait(false);

                if (File.Exists(request.TargetMmdbPath))
                {
                    File.Delete(request.TargetMmdbPath);
                }
                File.Move(tempExtractFile, request.TargetMmdbPath, overwrite: true);

                if (remoteLastModified.HasValue)
                {
                    File.SetLastWriteTimeUtc(request.TargetMmdbPath, remoteLastModified.Value.UtcDateTime);
                }

                var fileSizeMb = new FileInfo(request.TargetMmdbPath).Length / (1024.0 * 1024.0);
                var stepElapsedMs = Stopwatch.GetElapsedTime(stepStart).TotalMilliseconds;
                report.DetailLog = $"[{request.EditionId}] Extracted {fileSizeMb:F2} MB binary database in {stepElapsedMs:F2}ms.";
                progress?.Report(report);
                AppLogger.Info($"[{request.EditionId}] Deployed database ({fileSizeMb:F2} MB) in {stepElapsedMs:F2}ms.");
                return true;
            }
        }

        AppLogger.Warn($"[{request.EditionId}] Extraction complete but no .mmdb file found in archive.");
        return false;
    }

    private static bool IsPrivateOrLoopbackIp(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10 ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 169 && bytes[1] == 254);
        }

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal;
        }

        return false;
    }
}