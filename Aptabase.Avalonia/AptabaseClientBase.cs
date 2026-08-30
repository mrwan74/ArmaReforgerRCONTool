using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

internal sealed class AptabaseClientBase : IAsyncDisposable
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(60);
    private static readonly SystemInfo SysInfo = new();
    private static readonly FrozenDictionary<string, string> Hosts = new Dictionary<string, string>
    {
        ["US"] = "https://us.aptabase.com",
        ["EU"] = "https://eu.aptabase.com",
        ["DEV"] = "http://localhost:3000",
        ["SH"] = ""
    }.ToFrozenDictionary();

    private readonly string _appKey;
    private readonly ILogger? _logger;
    private readonly HttpClient? _http;
    private readonly AptabaseOptions? _options;
    private readonly Lock _sessionLock = new();
    private DateTimeOffset _lastTouched = DateTimeOffset.UtcNow;
    private string _sessionId = NewSessionId();

    public AptabaseClientBase(string appKey, AptabaseOptions? options, ILogger? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        _appKey = appKey.Trim();
        _options = options;
        _logger = logger;

        var parts = _appKey.Split('-');
        if (parts.Length < 3 || !Hosts.ContainsKey(parts[1]))
        {
            throw new AptabaseConfigurationException($"The Aptabase App Key '{_appKey}' is invalid. Expected format: 'A-REGION-00000000'.");
        }

        var region = parts[1];
        var baseUrl = GetBaseUrl(region, options);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new AptabaseConfigurationException("Base URL could not be resolved. Please specify Options.Host for Self-Hosted instances.");
        }

        SysInfo.IsDebug = options?.IsDebugMode ?? SystemInfo.IsInDebugMode(Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly());

        _http = region == "DEV" ? new HttpClient(new LocalHttpsClientHandler(logger)) : new HttpClient();
        _http.BaseAddress = new Uri(baseUrl);
        _http.DefaultRequestHeaders.Add("App-Key", _appKey);
        _http.Timeout = TimeSpan.FromSeconds(10);

        AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseClientBase),
            $"AptabaseClientBase ready. Region: {region}, BaseUrl: {baseUrl}, Session: {_sessionId}, OS: {SysInfo.OsName} {SysInfo.OsVersion}");
    }

    private bool IsConsentGranted()
    {
        try
        {
            return _options?.ConsentCheck?.Invoke() ?? true;
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"Consent check delegate failed: {ex.Message}. Defaulting to consent withheld.", ex);
            return false;
        }
    }

    internal Task TrackEvent(EventData eventData, CancellationToken cancellationToken = default) =>
        TrackEvents([eventData], cancellationToken);

    internal async Task TrackEvents(IEnumerable<EventData> events, CancellationToken cancellationToken = default)
    {
        if (!IsConsentGranted())
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "TrackEvents withheld: User telemetry consent not granted.");
            return;
        }

        if (_http is null)
        {
            throw new AptabaseConfigurationException("HTTP Client is not initialized.");
        }

        var eventList = events.ToList();
        if (eventList.Count == 0)
        {
            return;
        }

        RefreshSession();

        foreach (var ev in eventList)
        {
            ev.SessionId = _sessionId;
            ev.SystemProps = SysInfo;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var body = JsonContent.Create(eventList);
            using var response = await _http.PostAsync("/api/v0/events", body, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabaseClientBase),
                        $"Aptabase server rejected App Key '{_appKey}': {responseBody}.");
                    return;
                }

                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase),
                    $"HTTP POST /api/v0/events failed. Status: {statusCode}, Latency: {stopwatch.ElapsedMilliseconds}ms, Response: {responseBody}");

                if (response.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    throw new AptabaseTransmissionException($"Server error during events transmission: {response.StatusCode}", statusCode);
                }
            }
            else
            {
                AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseClientBase),
                    $"HTTP POST /api/v0/events SUCCESS. Delivered {eventList.Count} event(s) in {stopwatch.ElapsedMilliseconds}ms.");
            }
        }
        catch (OperationCanceledException)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "TrackEvents operation canceled on shutdown.");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"Network error during TrackEvents: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Network failure sending analytics events: {ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"Socket error during TrackEvents: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Socket failure connecting to Aptabase server: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"JSON serialization error during TrackEvents: {ex.Message}", ex);
            throw new AptabaseSerializationException($"Failed to serialize analytics events payload: {ex.Message}", ex);
        }
    }

    private const string PlatformName = "Avalonia";
    private const int MaxErrorMessage = 5000;
    private const int MaxErrorType = 100;
    private const int MaxStackTrace = 10000;
    private const int MaxPlatform = 30;
    private const int MaxOsName = 30;
    private const int MaxOsVersion = 100;
    private const int MaxAppVersion = 50;
    private const int MaxSdkVersion = 40;
    private const int MaxSessionId = 100;

    internal bool IsEnabled => _http is not null;

    internal void EnrichError(ErrorData errorData)
    {
        RefreshSession();

        errorData.SessionId = Truncate(_sessionId, MaxSessionId);
        errorData.Platform = Truncate(PlatformName, MaxPlatform);
        errorData.OsName = Truncate(SysInfo.OsName, MaxOsName);
        errorData.OsVersion = Truncate(SysInfo.OsVersion, MaxOsVersion);
        errorData.AppVersion = Truncate(SysInfo.AppVersion, MaxAppVersion);
        errorData.SdkVersion = Truncate(SysInfo.SdkVersion, MaxSdkVersion);
        errorData.IsDebug = SysInfo.IsDebug;

        errorData.ErrorMessage = Truncate(errorData.ErrorMessage, MaxErrorMessage)!;
        errorData.ErrorType = Truncate(errorData.ErrorType, MaxErrorType)!;
        errorData.StackTrace = Truncate(errorData.StackTrace, MaxStackTrace);
    }

    internal async Task SendErrorAsync(ErrorData errorData, CancellationToken cancellationToken = default)
    {
        if (!IsConsentGranted())
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "SendErrorAsync withheld: User telemetry consent not granted.");
            return;
        }

        if (_http is null)
        {
            throw new AptabaseConfigurationException("HTTP Client is not initialized.");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var body = JsonContent.Create(errorData);
            using var response = await _http.PostAsync("/api/v0/error", body, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabaseClientBase),
                        $"Aptabase server rejected App Key '{_appKey}' for error report: {responseBody}.");
                    return;
                }

                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase),
                    $"HTTP POST /api/v0/error failed. Status: {statusCode}, Latency: {stopwatch.ElapsedMilliseconds}ms, Response: {responseBody}");

                if (response.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    throw new AptabaseTransmissionException($"Server error during error transmission: {response.StatusCode}", statusCode);
                }
            }
            else
            {
                AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseClientBase),
                    $"HTTP POST /api/v0/error SUCCESS. Delivered error report '{errorData.ErrorType}' in {stopwatch.ElapsedMilliseconds}ms.");
            }
        }
        catch (OperationCanceledException)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "SendErrorAsync operation canceled on shutdown.");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"Network error sending error report: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Network failure delivering error report: {ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            stopwatch.Stop();
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"Socket error sending error report: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Socket failure delivering error report: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            stopwatch.Stop();
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"JSON serialization error: {ex.Message}", ex);
            throw new AptabaseSerializationException($"Failed to serialize error report: {ex.Message}", ex);
        }
    }

    internal Task TrackError(ErrorData errorData, CancellationToken cancellationToken = default)
    {
        EnrichError(errorData);
        return SendErrorAsync(errorData, cancellationToken);
    }

    private static string? Truncate(string? value, int maxLength)
        => value is { Length: > 0 } && value.Length > maxLength ? value[..maxLength] : value;

    public ValueTask DisposeAsync()
    {
        _http?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RefreshSession()
    {
        lock (_sessionLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastTouched >= SessionTimeout)
            {
                _sessionId = NewSessionId();
                AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClientBase), $"Session timed out. Generated new session ID: {_sessionId}");
            }

            _lastTouched = now;
        }
    }

    private static string NewSessionId()
    {
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var random = Random.Shared.NextInt64(0, 99999999);
        return $"{epoch}{random:D8}";
    }

    private static string? GetBaseUrl(string region, AptabaseOptions? options) =>
        region == "SH" ? options?.Host : Hosts.GetValueOrDefault(region);
}