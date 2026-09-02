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

    private readonly ILogger? _logger;
    private readonly HttpClient? _http;
    private readonly AptabaseOptions? _options;
    private readonly Lock _sessionLock = new();
    private DateTimeOffset _lastTouched = DateTimeOffset.UtcNow;
    private string _sessionId = NewSessionId();
    private int _disposed;

    public AptabaseClientBase(string appKey, AptabaseOptions? options, ILogger? logger)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        var trimmedKey = appKey.Trim();
        _options = options;
        _logger = logger;

        var parts = trimmedKey.Split('-');
        if (parts.Length < 3 || !Hosts.ContainsKey(parts[1]))
        {
            throw new AptabaseConfigurationException($"The Aptabase App Key '{trimmedKey}' is invalid. Expected format: 'A-REGION-00000000'.");
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
        _http.DefaultRequestHeaders.Add("App-Key", trimmedKey);
        _http.Timeout = TimeSpan.FromSeconds(10);

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseClientBase),
            $"[AptabaseClientBase:Init] Client ready in {elapsedMs:F2}ms (Region: {region}, BaseUrl: '{baseUrl}', SessionId: {_sessionId}, OS: {SysInfo.OsName} {SysInfo.OsVersion}, Arch: {SysInfo.ProcessArchitecture}, Debug: {SysInfo.IsDebug}).");
    }

    private bool IsConsentGranted()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var result = _options?.ConsentCheck?.Invoke() ?? true;
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), $"[AptabaseClientBase:Consent] Evaluated telemetry consent ({result}) in {elapsedMs:F2}ms.");
            return result;
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"[AptabaseClientBase:Consent] Consent check delegate failed: {ex.Message}. Defaulting to false.", ex);
            return false;
        }
    }

    internal Task TrackEvent(EventData eventData, CancellationToken cancellationToken = default) =>
        TrackEvents([eventData], cancellationToken);

    internal async Task TrackEvents(IEnumerable<EventData> events, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClientBase), "[AptabaseClientBase:TrackEvents] Bypassed: cancellation requested or client disposed.");
            return;
        }

        if (!IsConsentGranted())
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "[AptabaseClientBase:TrackEvents] Withheld: user consent not granted.");
            return;
        }

        if (_http is null)
        {
            throw new AptabaseConfigurationException("HTTP Client is not initialized.");
        }

        var eventList = events.ToList();
        if (eventList.Count == 0) return;

        RefreshSession();

        foreach (var ev in eventList)
        {
            ev.SessionId = _sessionId;
            ev.SystemProps = SysInfo;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), $"[AptabaseClientBase:TrackEvents] Sending POST /api/v0/events with {eventList.Count} item(s)...");
            var body = JsonContent.Create(eventList);
            using var response = await _http.PostAsync("/api/v0/events", body, CancellationToken.None).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabaseClientBase),
                        $"[AptabaseClientBase:TrackEvents] Server rejected App Key (Status: {statusCode}) in {elapsedMs:F2}ms: {responseBody}.");
                    return;
                }

                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase),
                    $"[AptabaseClientBase:TrackEvents] HTTP POST /api/v0/events failed (Status: {statusCode}, Latency: {elapsedMs:F2}ms, Response: {responseBody})");

                if (response.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    throw new AptabaseTransmissionException($"Server error during events transmission: {response.StatusCode}", statusCode);
                }
            }
            else
            {
                AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseClientBase),
                    $"[AptabaseClientBase:TrackEvents] Successfully delivered {eventList.Count} event(s) in {elapsedMs:F2}ms.");
            }
        }
        catch (OperationCanceledException)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "[AptabaseClientBase:TrackEvents] Operation canceled during shutdown.");
        }
        catch (HttpRequestException ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"[AptabaseClientBase:TrackEvents] Network error after {elapsedMs:F2}ms: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Network failure sending analytics events: {ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"[AptabaseClientBase:TrackEvents] Socket error after {elapsedMs:F2}ms: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Socket failure connecting to Aptabase: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"[AptabaseClientBase:TrackEvents] Serialization error after {elapsedMs:F2}ms: {ex.Message}", ex);
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

    internal bool IsEnabled => _http is not null && Volatile.Read(ref _disposed) == 0;

    internal void EnrichError(ErrorData errorData)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
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

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase),
            $"[AptabaseClientBase:Enrich] Enriched ErrorData for '{errorData.ErrorType}' (SessionId: {_sessionId}) in {elapsedMs:F2}ms.");
    }

    internal async Task SendErrorAsync(ErrorData errorData, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClientBase), "[AptabaseClientBase:SendError] Bypassed: cancellation requested or client disposed.");
            return;
        }

        if (!IsConsentGranted())
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "[AptabaseClientBase:SendError] Withheld: user consent not granted.");
            return;
        }

        if (_http is null)
        {
            throw new AptabaseConfigurationException("HTTP Client is not initialized.");
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClientBase), $"[AptabaseClientBase:SendError] Posting error report '{errorData.ErrorType}' to /api/v0/error...");
            var body = JsonContent.Create(errorData);
            using var response = await _http.PostAsync("/api/v0/error", body, CancellationToken.None).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                var statusCode = (int)response.StatusCode;

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized)
                {
                    AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabaseClientBase),
                        $"[AptabaseClientBase:SendError] Server rejected App Key for error report in {elapsedMs:F2}ms: {responseBody}.");
                    return;
                }

                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase),
                    $"[AptabaseClientBase:SendError] HTTP POST /api/v0/error failed (Status: {statusCode}, Latency: {elapsedMs:F2}ms, Response: {responseBody})");

                if (response.StatusCode is >= HttpStatusCode.InternalServerError or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                {
                    throw new AptabaseTransmissionException($"Server error during error transmission: {response.StatusCode}", statusCode);
                }
            }
            else
            {
                AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseClientBase),
                    $"[AptabaseClientBase:SendError] Delivered error report '{errorData.ErrorType}' in {elapsedMs:F2}ms.");
            }
        }
        catch (OperationCanceledException)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), "[AptabaseClientBase:SendError] SendErrorAsync operation canceled.");
        }
        catch (HttpRequestException ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"[AptabaseClientBase:SendError] Network error delivering error report after {elapsedMs:F2}ms: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Network failure delivering error report: {ex.Message}", ex);
        }
        catch (SocketException ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"[AptabaseClientBase:SendError] Socket error delivering error report after {elapsedMs:F2}ms: {ex.Message}", ex);
            throw new AptabaseTransmissionException($"Socket failure delivering error report: {ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClientBase), $"[AptabaseClientBase:SendError] Serialization error after {elapsedMs:F2}ms: {ex.Message}", ex);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;

        var startTimestamp = Stopwatch.GetTimestamp();
        _http?.Dispose();
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClientBase), $"[AptabaseClientBase:Dispose] HTTP client resources disposed in {elapsedMs:F2}ms.");
        return ValueTask.CompletedTask;
    }

    private void RefreshSession()
    {
        lock (_sessionLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastTouched >= SessionTimeout)
            {
                var previousSession = _sessionId;
                _sessionId = NewSessionId();
                AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClientBase),
                    $"[AptabaseClientBase:Session] Session timeout reached (60m). Rotated session: '{previousSession}' -> '{_sessionId}'");
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