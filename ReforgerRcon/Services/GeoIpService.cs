using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using ReforgerRcon.Models;
using Sentry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
            AppLogger.Debug($"[LocationFormatter] TimeZoneConverter notice for '{trimmedZone}': {ex.Message}");
        }

        return trimmedZone;
    }
}

public static class GeoIpService
{
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string GeoIpDirectory = Path.Combine(StorageDirectory, "geoip");
    private static readonly string SettingsFile = Path.Combine(StorageDirectory, "settings.json");
    private static readonly string ConfFile = Path.Combine(GeoIpDirectory, "GeoIP.conf");

    private static readonly string CityDbPath = Path.Combine(GeoIpDirectory, "GeoLite2-City.mmdb");
    private static readonly string CountryDbPath = Path.Combine(GeoIpDirectory, "GeoLite2-Country.mmdb");

    private static DatabaseReader? _cityReader;
    private static DatabaseReader? _countryReader;
    private static readonly Lock ReaderLock = new();
    private static readonly ConcurrentDictionary<string, GeoLocationResult> LookupCache = new(StringComparer.OrdinalIgnoreCase);

    private static CancellationTokenSource? _periodicUpdateCts;

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

    public static DateTime? CityDbLastModified => File.Exists(CityDbPath) ? File.GetLastWriteTimeUtc(CityDbPath) : null;
    public static DateTime? CountryDbLastModified => File.Exists(CountryDbPath) ? File.GetLastWriteTimeUtc(CountryDbPath) : null;
    public static bool IsUpdating { get; private set; }

    public static event Action? DatabasesUpdated;

    static GeoIpService()
    {
        try
        {
            if (!Directory.Exists(GeoIpDirectory))
            {
                Directory.CreateDirectory(GeoIpDirectory);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[GeoIpService] Directory notice: {ex.Message}");
        }

        DeployBundledDatabasesIfMissing();
    }

    public static void Initialize()
    {
        _ = Task.Run(() =>
        {
            try
            {
                ReloadReaders();
                AppLogger.Info($"[GeoIpService] MaxMind GeoIP2 engine ready (City DB: {IsCityDbLoaded}, Country DB: {IsCountryDbLoaded}, Has Credentials: {HasCustomCredentials}).");

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
            catch (Exception ex)
            {
                AppLogger.Error("[GeoIpService] Fatal error during GeoIP engine initialization.", ex);
            }
        }, CancellationToken.None);
    }

    public static void Shutdown()
    {
        if (_periodicUpdateCts != null)
        {
            try
            {
                _periodicUpdateCts.Cancel();
                _periodicUpdateCts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Disposed cleanly
            }
            _periodicUpdateCts = null;
        }
    }

    private static async Task StartPeriodicUpdateLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SafeDelayAsync(TimeSpan.FromHours(12), cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await UpdateDatabasesAsync(force: false, progress: null, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Error("[GeoIpService] Exception in background GeoIP update loop.", ex);
            }
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state => ((TaskCompletionSource?)state)?.TrySetResult(), tcs).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.Delay(delay, CancellationToken.None), tcs.Task).ConfigureAwait(false);
        }
    }

    private static void DeployBundledDatabasesIfMissing()
    {
        TryDeployBundledFile("GeoLite2-City.mmdb", CityDbPath);
        TryDeployBundledFile("GeoLite2-Country.mmdb", CountryDbPath);
    }

    private static void TryDeployBundledFile(string fileName, string destinationPath)
    {
        if (File.Exists(destinationPath)) return;

        string[] potentialSourcePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "GeoIP", fileName),
            Path.Combine(AppContext.BaseDirectory, "geoip", fileName),
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            Path.Combine(AppContext.BaseDirectory, "assets", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        ];

        foreach (var sourcePath in potentialSourcePaths.Where(File.Exists))
        {
            try
            {
                File.Copy(sourcePath, destinationPath, overwrite: false);
                break;
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[GeoIpService] Deploy notice for '{fileName}': {ex.Message}");
            }
        }
    }

    public static (string AccountId, string LicenseKey) ResolveCredentials()
    {
        var envAccount = Environment.GetEnvironmentVariable("MAXMIND_ACCOUNT_ID");
        var envKey = Environment.GetEnvironmentVariable("MAXMIND_LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(envAccount) && !string.IsNullOrWhiteSpace(envKey))
        {
            return (envAccount.Trim(), envKey.Trim());
        }

        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null && !string.IsNullOrWhiteSpace(settings.MaxMindAccountId) && !string.IsNullOrWhiteSpace(settings.MaxMindLicenseKey))
                {
                    return (settings.MaxMindAccountId.Trim(), settings.MaxMindLicenseKey.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService] Resolve credentials notice: {ex.Message}");
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
                    return (parsedAccount.Trim(), parsedKey.Trim());
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService] Conf file notice: {ex.Message}");
        }

        return (string.Empty, string.Empty);
    }

    public static void ReloadReaders()
    {
        lock (ReaderLock)
        {
            try
            {
                _cityReader?.Dispose();
                _cityReader = null;

                if (File.Exists(CityDbPath))
                {
                    _cityReader = new DatabaseReader(CityDbPath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[GeoIpService] Error initializing GeoLite2-City reader: {ex.Message}", ex);
            }

            try
            {
                _countryReader?.Dispose();
                _countryReader = null;

                if (File.Exists(CountryDbPath))
                {
                    _countryReader = new DatabaseReader(CountryDbPath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[GeoIpService] Error initializing GeoLite2-Country reader: {ex.Message}", ex);
            }

            LookupCache.Clear();
        }

        try
        {
            DatabasesUpdated?.Invoke();
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService] DatabasesUpdated notification notice: {ex.Message}");
        }
    }

    public static GeoLocationResult GetLocation(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return new GeoLocationResult("xx", LocationFormatter.UnknownRegion, string.Empty, string.Empty, string.Empty, null, null, string.Empty, LocationFormatter.UnknownRegion);
        }

        if (LookupCache.TryGetValue(ip, out var cached))
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

        lock (ReaderLock)
        {
            if (_cityReader != null)
            {
                try
                {
                    if (_cityReader.TryCity(parsedIp, out var cityResponse) && cityResponse != null)
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
                        return result;
                    }
                }
                catch (AddressNotFoundException)
                {
                    AppLogger.Trace($"[GeoIpService] IP '{ip}' not found in GeoLite2-City database.");
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[GeoIpService] City lookup notice for {ip}: {ex.Message}");
                }
            }

            if (_countryReader != null)
            {
                try
                {
                    if (_countryReader.TryCountry(parsedIp, out var countryResponse) && countryResponse != null)
                    {
                        var countryCode = !string.IsNullOrEmpty(countryResponse.Country.IsoCode) ? countryResponse.Country.IsoCode.ToLowerInvariant() : "xx";
                        var countryName = !string.IsNullOrEmpty(countryResponse.Country.Name) ? countryResponse.Country.Name : LocationFormatter.UnknownRegion;

                        var result = new GeoLocationResult(countryCode, countryName, string.Empty, countryName, string.Empty, null, null, string.Empty, countryName);
                        LookupCache[ip] = result;
                        return result;
                    }
                }
                catch (AddressNotFoundException)
                {
                    AppLogger.Trace($"[GeoIpService] IP '{ip}' not found in GeoLite2-Country database.");
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[GeoIpService] Country lookup notice for {ip}: {ex.Message}");
                }
            }
        }

        var fallbackResult = new GeoLocationResult("xx", LocationFormatter.UnknownRegion, string.Empty, string.Empty, string.Empty, null, null, string.Empty, LocationFormatter.UnknownRegion);
        LookupCache[ip] = fallbackResult;
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
            return false;
        }

        var report = new GeoIpProgressReport();

        var (accountId, licenseKey) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(licenseKey))
        {
            report.CurrentOperation = "Authentication Credentials Missing";
            report.DetailLog = "MaxMind Account ID & License Key required to download live database updates.";
            report.CityStatus = GeoIpStepStatus.Failed;
            report.CountryStatus = GeoIpStepStatus.Failed;
            progress?.Report(report);

            if (force)
            {
                ToastNotificationService.Instance.ShowToast(
                    "MaxMind Credentials Required",
                    "Currently using offline pre-bundled databases. Enter Account ID & License Key in Settings to update.",
                    "GEOIP_AUTH_NOTICE"
                );
            }
            return false;
        }

        IsUpdating = true;
        try
        {
            report.CurrentOperation = "Connecting to MaxMind Update Servers...";
            report.OverallProgressPercentage = 5;
            report.DetailLog = $"Initiating authenticated handshake for Account ID: {accountId}...";
            progress?.Report(report);

            // 1. GeoLite2-City
            report.CurrentOperation = "Updating GeoLite2-City Database...";
            report.CityStatus = GeoIpStepStatus.InProgress;
            report.CityStatusMessage = "Checking version...";
            report.OverallProgressPercentage = 15;
            report.DetailLog = "Checking remote revision timestamp for GeoLite2-City.mmdb...";
            progress?.Report(report);

            var cityReq = new GeoIpDownloadRequest("GeoLite2-City", CityDbPath, accountId, licenseKey, force, 15, 55);
            bool cityUpdated = await CheckAndUpdateEditionAsync(cityReq, progress, report, cancellationToken).ConfigureAwait(false);
            report.CityStatus = cityUpdated ? GeoIpStepStatus.Completed : GeoIpStepStatus.Skipped;
            report.CityStatusMessage = cityUpdated ? "Updated successfully" : "Already up to date";
            report.OverallProgressPercentage = 55;
            progress?.Report(report);

            // 2. GeoLite2-Country
            report.CurrentOperation = "Updating GeoLite2-Country Database...";
            report.CountryStatus = GeoIpStepStatus.InProgress;
            report.CountryStatusMessage = "Checking version...";
            report.OverallProgressPercentage = 60;
            report.DetailLog = "Checking remote revision timestamp for GeoLite2-Country.mmdb...";
            progress?.Report(report);

            var countryReq = new GeoIpDownloadRequest("GeoLite2-Country", CountryDbPath, accountId, licenseKey, force, 60, 95);
            bool countryUpdated = await CheckAndUpdateEditionAsync(countryReq, progress, report, cancellationToken).ConfigureAwait(false);
            report.CountryStatus = countryUpdated ? GeoIpStepStatus.Completed : GeoIpStepStatus.Skipped;
            report.CountryStatusMessage = countryUpdated ? "Updated successfully" : "Already up to date";
            report.OverallProgressPercentage = 95;
            progress?.Report(report);

            report.CurrentOperation = "Reloading Reader Instances...";
            report.DetailLog = "Re-initializing binary MaxMind MMDB reader engines and warming cache...";
            progress?.Report(report);

            ReloadReaders();

            report.OverallProgressPercentage = 100;
            report.CurrentOperation = "All Databases Synchronized";
            report.DetailLog = "MaxMind binary database synchronization completed successfully.";
            progress?.Report(report);

            if (cityUpdated || countryUpdated || force)
            {
                ToastNotificationService.Instance.ShowToast(
                    "GeoIP Databases Updated",
                    "MaxMind GeoLite2 binary databases refreshed and active.",
                    "GEOIP_UPDATE"
                );
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            report.CurrentOperation = "Update Canceled";
            report.DetailLog = "Database update operation was canceled by the operator.";
            progress?.Report(report);
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            AppLogger.Error("[GeoIpService] Network error during MaxMind update.", httpEx);
            report.CurrentOperation = "Network Failure";
            report.DetailLog = $"HTTP communication failure: {httpEx.Message}";
            progress?.Report(report);
            ToastNotificationService.Instance.ShowError("GeoIP Update Failed", $"HTTP error: {httpEx.Message}");
            return false;
        }
        catch (IOException ioEx)
        {
            AppLogger.Error("[GeoIpService] Disk I/O error during database extraction.", ioEx);
            report.CurrentOperation = "Disk Extraction Failure";
            report.DetailLog = $"File write failure: {ioEx.Message}";
            progress?.Report(report);
            ToastNotificationService.Instance.ShowError("GeoIP Disk Error", $"I/O failure: {ioEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[GeoIpService] Unexpected error during MaxMind GeoIP update.", ex);
            report.CurrentOperation = "Unexpected Error";
            report.DetailLog = $"Failed updating databases: {ex.Message}";
            progress?.Report(report);
            ToastNotificationService.Instance.ShowToast("GeoIP Update Error", $"Failed updating GeoIP databases: {ex.Message}", "GEOIP_UPDATE_ERR");
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
        var downloadUrl = $"https://download.maxmind.com/geoip/databases/{request.EditionId}/download?suffix=tar.gz";
        var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{request.AccountId}:{request.LicenseKey}"));

        DateTimeOffset? remoteLastModified = null;

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
            headRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            using var headResponse = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (headResponse.IsSuccessStatusCode)
            {
                remoteLastModified = headResponse.Content.Headers.LastModified ?? headResponse.Headers.Date;
                report.DetailLog = $"[{request.EditionId}] Remote version timestamp: {remoteLastModified:yyyy-MM-dd HH:mm:ss UTC}";
                progress?.Report(report);
            }
            else if (headResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                report.DetailLog = $"[{request.EditionId}] Authentication rejected: MaxMind Account ID or License Key is invalid.";
                progress?.Report(report);
                return false;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[GeoIpService] Head check notice for '{request.EditionId}': {ex.Message}");
        }

        if (!request.Force && File.Exists(request.TargetMmdbPath) && remoteLastModified.HasValue)
        {
            var localLastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(request.TargetMmdbPath), TimeSpan.Zero);
            if (remoteLastModified.Value <= localLastWriteTime)
            {
                report.DetailLog = $"[{request.EditionId}] Local file is already current ({localLastWriteTime:yyyy-MM-dd}). Download skipped.";
                progress?.Report(report);
                return false;
            }
        }

        report.DetailLog = $"[{request.EditionId}] Downloading archive payload (.tar.gz)...";
        report.OverallProgressPercentage = (request.StartPercentage + request.EndPercentage) / 2.0;
        progress?.Report(report);

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

        using var getResponse = await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!getResponse.IsSuccessStatusCode)
        {
            report.DetailLog = $"[{request.EditionId}] Download failed with status {getResponse.StatusCode}.";
            progress?.Report(report);
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
                report.DetailLog = $"[{request.EditionId}] Extracted {fileSizeMb:F2} MB binary database to '{Path.GetFileName(request.TargetMmdbPath)}'.";
                progress?.Report(report);
                return true;
            }
        }

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