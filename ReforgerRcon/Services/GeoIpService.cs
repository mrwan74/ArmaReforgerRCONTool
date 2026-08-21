using MaxMind.GeoIP2;
using MaxMind.GeoIP2.Exceptions;
using ReforgerRcon.Models;
using Sentry;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Formats.Tar;
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

namespace ReforgerRcon.Services;

public record GeoLocationResult(
    string CountryCode,
    string CountryName,
    string CityName,
    string SubdivisionName,
    string PostalCode,
    double? Latitude,
    double? Longitude);

public static class GeoIpService
{
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string GeoIpDirectory = Path.Combine(StorageDirectory, "geoip");
    private static readonly string SettingsFile = Path.Combine(StorageDirectory, "settings.json");
    private static readonly string ConfFile = Path.Combine(GeoIpDirectory, "GeoIP.conf");

    private static readonly string CityDbPath = Path.Combine(GeoIpDirectory, "GeoLite2-City.mmdb");
    private static readonly string CountryDbPath = Path.Combine(GeoIpDirectory, "GeoLite2-Country.mmdb");
    private static readonly string AsnDbPath = Path.Combine(GeoIpDirectory, "GeoLite2-ASN.mmdb");

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
        catch (IOException ex)
        {
            AppLogger.Error("[GeoIpService] Failed to create geoip storage directory.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            AppLogger.Error("[GeoIpService] Access denied creating geoip storage directory.", ex);
        }

        DeployBundledDatabasesIfMissing();
        ReloadReaders();
    }

    public static void Initialize()
    {
        AppLogger.Info($"[GeoIpService] Initializing MaxMind GeoIP2 engine (City DB: {IsCityDbLoaded}, Country DB: {IsCountryDbLoaded}, Has Credentials: {HasCustomCredentials})...");

        if (HasCustomCredentials)
        {
            AppLogger.Info("[GeoIpService] MaxMind credentials detected. Scheduling initial HEAD check and background update loop...");

            _periodicUpdateCts?.Cancel();
            _periodicUpdateCts?.Dispose();
            _periodicUpdateCts = new CancellationTokenSource();

            var token = _periodicUpdateCts.Token;
            _ = Task.Run(() => UpdateDatabasesAsync(force: false, token), token);
            _ = StartPeriodicUpdateLoopAsync(token);
        }
        else if (IsCityDbLoaded || IsCountryDbLoaded)
        {
            AppLogger.Info("[GeoIpService] Running in offline pre-bundled mode. Geolocation lookups active without credential requirements.");
        }
        else
        {
            AppLogger.Info("[GeoIpService] Running in standalone mode. To enable automatic GeoIP database updates, enter your MaxMind credentials in Settings.");
        }
    }

    private static async Task StartPeriodicUpdateLoopAsync(CancellationToken cancellationToken)
    {
        using var periodicTimer = new PeriodicTimer(TimeSpan.FromHours(12));
        try
        {
            while (!cancellationToken.IsCancellationRequested && await periodicTimer.WaitForNextTickAsync(cancellationToken))
            {
                await UpdateDatabasesAsync(force: false, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Debug("[GeoIpService] Periodic background GeoIP update loop terminated cleanly via cancellation token.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[GeoIpService] Exception in periodic background GeoIP update loop.", ex);
        }
    }

    private static void DeployBundledDatabasesIfMissing()
    {
        TryDeployBundledFile("GeoLite2-City.mmdb", CityDbPath);
        TryDeployBundledFile("GeoLite2-Country.mmdb", CountryDbPath);
        TryDeployBundledFile("GeoLite2-ASN.mmdb", AsnDbPath);
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
                AppLogger.Info($"[GeoIpService] Deployed pre-bundled '{fileName}' from '{sourcePath}' to '{destinationPath}'.");
                break;
            }
            catch (IOException ex)
            {
                AppLogger.Warn($"[GeoIpService] I/O warning deploying pre-bundled '{fileName}': {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                AppLogger.Warn($"[GeoIpService] Permission warning deploying pre-bundled '{fileName}': {ex.Message}");
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
        catch (JsonException ex)
        {
            AppLogger.Warn($"[GeoIpService] JSON format error reading MaxMind credentials: {ex.Message}");
        }
        catch (IOException ex)
        {
            AppLogger.Warn($"[GeoIpService] I/O error reading settings file: {ex.Message}");
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
        catch (IOException ex)
        {
            AppLogger.Warn($"[GeoIpService] I/O error parsing GeoIP.conf: {ex.Message}");
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
                    AppLogger.Info($"[GeoIpService] Loaded GeoLite2-City database from '{CityDbPath}' ({new FileInfo(CityDbPath).Length / (1024 * 1024):F1} MB).");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[GeoIpService] Error initializing GeoLite2-City reader from '{CityDbPath}'", ex);
            }

            try
            {
                _countryReader?.Dispose();
                _countryReader = null;

                if (File.Exists(CountryDbPath))
                {
                    _countryReader = new DatabaseReader(CountryDbPath);
                    AppLogger.Info($"[GeoIpService] Loaded GeoLite2-Country database from '{CountryDbPath}' ({new FileInfo(CountryDbPath).Length / (1024 * 1024):F1} MB).");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[GeoIpService] Error initializing GeoLite2-Country reader from '{CountryDbPath}'", ex);
            }

            LookupCache.Clear();
        }

        try
        {
            DatabasesUpdated?.Invoke();
        }
        catch (Exception hookEx)
        {
            AppLogger.Error("[GeoIpService] Exception in DatabasesUpdated event handler.", hookEx);
        }
    }

    public static GeoLocationResult GetLocation(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return new GeoLocationResult("un", "Unknown Region", "Unknown City", "Unknown State", string.Empty, null, null);
        }

        if (LookupCache.TryGetValue(ip, out var cached))
        {
            return cached;
        }

        if (!IPAddress.TryParse(ip, out var parsedIp))
        {
            return new GeoLocationResult("un", "Unknown Region", "Invalid IP Format", "Unknown State", string.Empty, null, null);
        }

        if (IsPrivateOrLoopbackIp(parsedIp))
        {
            var localResult = new GeoLocationResult("un", "Local Network", "Internal LAN / Dedicated Host", "Local Subnet", string.Empty, 0.0, 0.0);
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
                        var countryCode = !string.IsNullOrEmpty(cityResponse.Country.IsoCode) ? cityResponse.Country.IsoCode.ToLowerInvariant() : "un";
                        var countryName = !string.IsNullOrEmpty(cityResponse.Country.Name) ? cityResponse.Country.Name : "Unknown Country";
                        var cityName = !string.IsNullOrEmpty(cityResponse.City.Name) ? cityResponse.City.Name : "Connected Region";
                        var stateName = !string.IsNullOrEmpty(cityResponse.MostSpecificSubdivision.Name) ? cityResponse.MostSpecificSubdivision.Name : countryName;
                        var postal = cityResponse.Postal.Code ?? string.Empty;
                        var lat = cityResponse.Location.Latitude;
                        var lon = cityResponse.Location.Longitude;

                        var result = new GeoLocationResult(countryCode, countryName, cityName, stateName, postal, lat, lon);
                        LookupCache[ip] = result;
                        AppLogger.Trace($"[GeoIpService] City resolved for {ip} -> {cityName}, {stateName}, {countryName} [{countryCode.ToUpperInvariant()}]");

                        SentrySdk.Metrics.EmitCounter("geoip_resolved_city", 1,
                        [
                            new KeyValuePair<string, object>("country", countryCode)
                        ]);

                        return result;
                    }
                }
                catch (AddressNotFoundException)
                {
                    AppLogger.Trace($"[GeoIpService] Address {ip} not found in City database.");
                }
                catch (GeoIP2Exception ex)
                {
                    AppLogger.Warn($"[GeoIpService] MMDB format error resolving city for IP {ip}: {ex.Message}");
                }
            }

            if (_countryReader != null)
            {
                try
                {
                    if (_countryReader.TryCountry(parsedIp, out var countryResponse) && countryResponse != null)
                    {
                        var countryCode = !string.IsNullOrEmpty(countryResponse.Country.IsoCode) ? countryResponse.Country.IsoCode.ToLowerInvariant() : "un";
                        var countryName = !string.IsNullOrEmpty(countryResponse.Country.Name) ? countryResponse.Country.Name : "Unknown Country";

                        var result = new GeoLocationResult(countryCode, countryName, "Connected Region", countryName, string.Empty, null, null);
                        LookupCache[ip] = result;
                        AppLogger.Trace($"[GeoIpService] Country resolved for {ip} -> {countryName} [{countryCode.ToUpperInvariant()}]");

                        SentrySdk.Metrics.EmitCounter("geoip_resolved_country", 1,
                        [
                            new KeyValuePair<string, object>("country", countryCode)
                        ]);

                        return result;
                    }
                }
                catch (AddressNotFoundException)
                {
                    AppLogger.Trace($"[GeoIpService] Address {ip} not found in Country database.");
                }
                catch (GeoIP2Exception ex)
                {
                    AppLogger.Warn($"[GeoIpService] MMDB format error resolving country for IP {ip}: {ex.Message}");
                }
            }
        }

        var fallbackResult = new GeoLocationResult("un", "Direct Region", "Connected Network", "Direct Routing", string.Empty, null, null);
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

    public static async Task<bool> UpdateDatabasesAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (IsUpdating)
        {
            AppLogger.Warn("[GeoIpService] Database update is already in progress.");
            return false;
        }

        var (accountId, licenseKey) = ResolveCredentials();
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(licenseKey))
        {
            AppLogger.Info("[GeoIpService] Database update skipped: MaxMind Account ID and License Key are not configured.");
            if (force)
            {
                ToastNotificationService.Instance.ShowToast(
                    "MaxMind Credentials Required",
                    "Currently using offline pre-bundled databases. To download newer updates, enter your MaxMind Account ID & License Key in Settings.",
                    "GEOIP_AUTH_NOTICE"
                );
            }
            return false;
        }

        IsUpdating = true;
        var transaction = SentrySdk.StartTransaction("UpdateGeoIpDatabases", "geoip.update");
        AppLogger.Info("[GeoIpService] Starting MaxMind GeoLite2 smart check and update cycle...");

        try
        {
            bool cityUpdated = await CheckAndUpdateEditionAsync("GeoLite2-City", CityDbPath, accountId, licenseKey, force, cancellationToken);
            bool countryUpdated = await CheckAndUpdateEditionAsync("GeoLite2-Country", CountryDbPath, accountId, licenseKey, force, cancellationToken);
            await CheckAndUpdateEditionAsync("GeoLite2-ASN", AsnDbPath, accountId, licenseKey, force, cancellationToken);

            if (cityUpdated || countryUpdated || force)
            {
                ReloadReaders();
                ToastNotificationService.Instance.ShowToast(
                    "GeoIP Databases Updated",
                    "MaxMind GeoLite2 binary databases refreshed and active.",
                    "GEOIP_UPDATE"
                );
                AppLogger.Info("[GeoIpService] GeoIP binary databases updated and reloaded.");
                transaction.Finish(SpanStatus.Ok);

                SentrySdk.Metrics.EmitCounter("geoip_databases_updated", 1);
                return true;
            }

            AppLogger.Info("[GeoIpService] GeoIP databases are already at the latest release. No download quota consumed.");
            transaction.Finish(SpanStatus.Ok);
            return false;
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("[GeoIpService] GeoIP database update canceled.");
            transaction.Finish(SpanStatus.Cancelled);
            return false;
        }
        catch (HttpRequestException httpEx)
        {
            AppLogger.Error($"[GeoIpService] HTTP request error during MaxMind update: {httpEx.Message}", httpEx);
            transaction.Finish(SpanStatus.Unavailable);
            ToastNotificationService.Instance.ShowToast("GeoIP Download Error", $"MaxMind server communication failure: {httpEx.Message}", "GEOIP_HTTP_ERR");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error("[GeoIpService] Error during MaxMind GeoIP update cycle.", ex);
            transaction.Finish(SpanStatus.UnknownError);
            ToastNotificationService.Instance.ShowToast("GeoIP Update Error", $"Failed updating GeoIP databases: {ex.Message}", "GEOIP_UPDATE_ERR");
            return false;
        }
        finally
        {
            IsUpdating = false;
        }
    }

    private static async Task<bool> CheckAndUpdateEditionAsync(
        string editionId,
        string targetMmdbPath,
        string accountId,
        string licenseKey,
        bool force,
        CancellationToken cancellationToken)
    {
        var downloadUrl = $"https://download.maxmind.com/geoip/databases/{editionId}/download?suffix=tar.gz";
        var authHeader = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{accountId}:{licenseKey}"));

        DateTimeOffset? remoteLastModified = null;

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, downloadUrl);
            headRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

            using var headResponse = await HttpClient.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (headResponse.IsSuccessStatusCode)
            {
                remoteLastModified = headResponse.Content.Headers.LastModified ?? headResponse.Headers.Date;
                AppLogger.Debug($"[GeoIpService] HEAD check for '{editionId}': Remote Last-Modified = {remoteLastModified:O}");
            }
            else
            {
                AppLogger.Warn($"[GeoIpService] HEAD check for '{editionId}' returned HTTP {headResponse.StatusCode}.");
            }
        }
        catch (HttpRequestException ex)
        {
            AppLogger.Warn($"[GeoIpService] HEAD request for '{editionId}' network issue: {ex.Message}");
        }

        if (!force && File.Exists(targetMmdbPath) && remoteLastModified.HasValue)
        {
            var localLastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(targetMmdbPath), TimeSpan.Zero);
            if (remoteLastModified.Value <= localLastWriteTime)
            {
                AppLogger.Info($"[GeoIpService] '{editionId}' is already up-to-date (Local: {localLastWriteTime:yyyy-MM-dd HH:mm UTC}, Remote: {remoteLastModified:yyyy-MM-dd HH:mm UTC}). Download skipped.");
                return false;
            }
        }

        AppLogger.Info($"[GeoIpService] Newer release available for '{editionId}'. Downloading archive...");

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        getRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);

        using var getResponse = await HttpClient.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!getResponse.IsSuccessStatusCode)
        {
            AppLogger.Error($"[GeoIpService] MaxMind server returned status {getResponse.StatusCode} for '{editionId}'.");
            return false;
        }

        await using var compressedStream = await getResponse.Content.ReadAsStreamAsync(cancellationToken);
        await using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
        await using var tarReader = new TarReader(gzipStream);

        string tempExtractFile = $"{targetMmdbPath}.tmp";

        while (await tarReader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            if (entry.Name.EndsWith(".mmdb", StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info($"[GeoIpService] Extracting '{entry.Name}' to '{tempExtractFile}'...");
                await entry.ExtractToFileAsync(tempExtractFile, overwrite: true, cancellationToken: cancellationToken);

                if (File.Exists(targetMmdbPath))
                {
                    File.Delete(targetMmdbPath);
                }
                File.Move(tempExtractFile, targetMmdbPath, overwrite: true);

                if (remoteLastModified.HasValue)
                {
                    File.SetLastWriteTimeUtc(targetMmdbPath, remoteLastModified.Value.UtcDateTime);
                }

                AppLogger.Info($"[GeoIpService] Successfully installed updated '{editionId}' to '{targetMmdbPath}'.");
                return true;
            }
        }

        AppLogger.Warn($"[GeoIpService] No .mmdb binary database found in the downloaded archive for '{editionId}'.");
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