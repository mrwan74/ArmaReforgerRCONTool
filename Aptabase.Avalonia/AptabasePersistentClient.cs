using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotNext.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

public sealed class AptabasePersistentClient : IAptabaseClient, IErrorTracker
{
    public event EventHandler<AptabaseErrorEventArgs>? OnErrorOccurred;

    private const int MaxPersistedEvents = 2000;
    private const int MaxBatchSize = 25;
    private const int FlushIntervalMs = 2000;
    private const string InvalidPersistedEvent = "%%%DELETE%%%";
    private const string InvalidPersistedError = "%%%DELETE%%%";
    private const int RetrySeconds = 15;

    private readonly PersistentEventDataChannel _channel;
    private readonly PersistentErrorDataChannel _errorChannel;
    private readonly Task _processingTask;
    private readonly Task _errorProcessingTask;
    private readonly AptabaseClientBase _client;
    private readonly ILogger<AptabasePersistentClient>? _logger;
    private readonly AptabaseOptions? _options;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private long _persistedEventsCount;
    private long _persistedErrorsCount;

    [SuppressMessage("AsyncUsage", "PH_S007:AvoidStartingThreadsOrTasksInConstructor", Justification = "Background channel batch processor must be initialized alongside persistent client lifecycle")]
    public AptabasePersistentClient(string appKey, AptabaseOptions? options, ILogger<AptabasePersistentClient>? logger)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        _options = options;
        _logger = logger;

        var storageDirectory = options?.StoragePath ?? GetDefaultStorageDirectory();
        _client = new AptabaseClientBase(appKey, options, logger);

        var eventLocation = Path.Combine(storageDirectory, "EventData");
        var errorLocation = Path.Combine(storageDirectory, "ErrorData");

        _channel = new PersistentEventDataChannel(new PersistentChannelOptions
        {
            SingleReader = true,
            ReliableEnumeration = true,
            PartitionCapacity = MaxPersistedEvents,
            Location = eventLocation,
        }, logger);

        _errorChannel = new PersistentErrorDataChannel(new PersistentChannelOptions
        {
            SingleReader = true,
            ReliableEnumeration = true,
            PartitionCapacity = MaxPersistedEvents,
            Location = errorLocation,
        }, logger);

        _processingTask = Task.Run(ProcessEventsBatchAsync, CancellationToken.None);
        _errorProcessingTask = Task.Run(ProcessErrorsAsync, CancellationToken.None);

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabasePersistentClient),
            $"[AptabasePersistent:Init] Initialized in {elapsedMs:F2}ms at '{storageDirectory}' (Events: '{eventLocation}', Errors: '{errorLocation}').");
    }

    private static string GetDefaultStorageDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        var dir = Path.Combine(localAppData, ".aptabase");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public async Task TrackEvent(string eventName, Dictionary<string, object>? props = null, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _disposed) != 0)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackEvent] Skipped: disposed. Event: '{eventName}'.");
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var eventData = new EventData(eventName, props, _options?.ContextInjector);

        try
        {
            await _channel.Writer.WriteAsync(eventData, cancellationToken).ConfigureAwait(false);
            var count = Interlocked.Increment(ref _persistedEventsCount);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackEvent] Persisted '{eventName}' (Total={count}) in {elapsedMs:F2}ms.");
        }
        catch (OperationCanceledException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "[AptabasePersistent:TrackEvent] Canceled.", ex);
        }
        catch (IOException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackEvent] Disk error for '{eventName}': {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackEvent] Error persisting '{eventName}': {ex.Message}", ex);
        }
    }

    public Task TrackError(Exception exception, bool fatal = false, CancellationToken cancellationToken = default)
        => ((IErrorTracker)this).TrackError(exception, fatal, fatal ? "crash" : "handled", cancellationToken);

    async Task IErrorTracker.TrackError(Exception exception, bool fatal, string kind, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _disposed) != 0)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackError] Skipped: disposed. Type: '{exception.GetType().Name}'.");
            return;
        }

        ArgumentNullException.ThrowIfNull(exception);

        var userMessage = fatal
            ? $"A critical application failure occurred: {exception.Message}. Crash data saved locally."
            : $"An error occurred: {exception.Message}.";

        NotifyErrorOccurred(exception, fatal, kind, userMessage);

        var errorData = ErrorData.FromException(exception, fatal, kind);
        _client.EnrichError(errorData);

        try
        {
            await _errorChannel.Writer.WriteAsync(errorData, cancellationToken).ConfigureAwait(false);
            var count = Interlocked.Increment(ref _persistedErrorsCount);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackError] Persisted error '{errorData.ErrorType}' (Total={count}) in {elapsedMs:F2}ms.");
        }
        catch (OperationCanceledException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "[AptabasePersistent:TrackError] Canceled.", ex);
        }
        catch (IOException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackError] Disk error: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabasePersistentClient), $"[AptabasePersistent:TrackError] Error persisting error report: {ex.Message}", ex);
        }
    }

    private void NotifyErrorOccurred(Exception exception, bool fatal, string kind, string userMessage)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            OnErrorOccurred?.Invoke(this, new AptabaseErrorEventArgs(exception, fatal, kind, userMessage));
            _options?.OnUserFacingNotification?.Invoke(userMessage, exception, fatal);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"[AptabasePersistent:Notify] Dispatched callbacks in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"[AptabasePersistent:Notify] Callback exception: {ex.Message}", ex);
        }
    }

    [SuppressMessage("AsyncUsage", "PH_P008:ThrowOperationCanceledException", Justification = "Background channel batch processor terminates gracefully without throwing")]
    private async Task ProcessEventsBatchAsync()
    {
        var batch = new List<EventData>(MaxBatchSize);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                batch.Clear();

                while (_channel.Reader.TryRead(out var eventData) && batch.Count < MaxBatchSize)
                {
                    if (eventData.EventName != InvalidPersistedEvent)
                    {
                        batch.Add(eventData);
                    }
                }

                if (batch.Count == 0)
                {
                    bool canRead = false;
                    try
                    {
                        canRead = await _channel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ChannelClosedException)
                    {
                        break;
                    }

                    if (!canRead || _cts.IsCancellationRequested)
                    {
                        break;
                    }

                    while (_channel.Reader.TryRead(out var eventData) && batch.Count < MaxBatchSize)
                    {
                        if (eventData.EventName != InvalidPersistedEvent)
                        {
                            batch.Add(eventData);
                        }
                    }
                }

                if (batch.Count > 0 && !_cts.IsCancellationRequested)
                {
                    var flushStart = Stopwatch.GetTimestamp();
                    await _client.TrackEvents(batch, CancellationToken.None).ConfigureAwait(false);
                    var flushElapsedMs = Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds;
                    AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"[AptabasePersistent:Flush] Sent batch of {batch.Count} event(s) in {flushElapsedMs:F2}ms.");
                }

                if (_cts.IsCancellationRequested)
                {
                    break;
                }

                await SafeDelayAsync(FlushIntervalMs, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (AptabaseException ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"[AptabasePersistent:Flush] Transmission failed. Retrying in {RetrySeconds}s: {ex.Message}", ex);
                await SafeDelayAsync(RetrySeconds * 1000, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"[AptabasePersistent:Flush] Error in batch loop: {ex.Message}", ex);
            }
        }
    }

    [SuppressMessage("AsyncUsage", "PH_P008:ThrowOperationCanceledException", Justification = "Background channel error processor terminates gracefully without throwing")]
    private async Task ProcessErrorsAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                bool canRead = false;
                try
                {
                    canRead = await _errorChannel.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                if (!canRead || _cts.IsCancellationRequested)
                {
                    break;
                }

                while (_errorChannel.Reader.TryRead(out var errorData))
                {
                    if (errorData.ErrorType == InvalidPersistedError || _cts.IsCancellationRequested) continue;

                    var sendStart = Stopwatch.GetTimestamp();
                    await _client.SendErrorAsync(errorData, CancellationToken.None).ConfigureAwait(false);
                    var sendElapsedMs = Stopwatch.GetElapsedTime(sendStart).TotalMilliseconds;
                    AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"[AptabasePersistent:Error] Delivered error '{errorData.ErrorType}' in {sendElapsedMs:F2}ms.");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ChannelClosedException)
            {
                break;
            }
            catch (AptabaseException ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"[AptabasePersistent:Error] Transmission failed. Retrying in {RetrySeconds}s: {ex.Message}", ex);
                await SafeDelayAsync(RetrySeconds * 1000, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"[AptabasePersistent:Error] Channel error: {ex.Message}", ex);
            }
        }
    }

    private static async Task SafeDelayAsync(int millisecondsDelay, CancellationToken cancellationToken)
    {
        if (millisecondsDelay <= 0 || cancellationToken.IsCancellationRequested) return;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state => ((TaskCompletionSource?)state)?.TrySetResult(), tcs).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.Delay(millisecondsDelay, CancellationToken.None), tcs.Task).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        var startTimestamp = Stopwatch.GetTimestamp();
        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabasePersistentClient), "[AptabasePersistent:Dispose] Shutting down channels and background processors...");

        _channel.Writer.TryComplete();
        _errorChannel.Writer.TryComplete();

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "[AptabasePersistent:Dispose] CTS notice: " + ex.Message, ex);
        }

        try
        {
            await Task.WhenAll(_processingTask, _errorProcessingTask).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (TimeoutException)
        {
            AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabasePersistentClient), "[AptabasePersistent:Dispose] Worker tasks timed out during shutdown.");
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "[AptabasePersistent:Dispose] Worker notice: " + ex.Message, ex);
        }

        _cts.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabasePersistentClient), $"[AptabasePersistent:Dispose] Disposal finalized in {elapsedMs:F2}ms.");

        GC.SuppressFinalize(this);
    }

    private sealed class PersistentEventDataChannel(PersistentChannelOptions options, ILogger? logger)
        : PersistentChannel<EventData, EventData>(options)
    {
        protected override async ValueTask<EventData> DeserializeAsync(Stream input, CancellationToken token)
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                var json = await ExtractJsonObject(input, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new EventData(InvalidPersistedEvent);
                }
                var result = JsonSerializer.Deserialize<EventData>(json) ?? new EventData(InvalidPersistedEvent);
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                AptabaseLogging.Log(logger, LogLevel.Trace, nameof(PersistentEventDataChannel), $"[PersistentEventChannel:Deserialize] '{result.EventName}' in {elapsedMs:F2}ms.");
                return result;
            }
            catch (JsonException ex)
            {
                AptabaseLogging.Log(logger, LogLevel.Error, nameof(PersistentEventDataChannel), $"[PersistentEventChannel:Deserialize] JSON error: {ex.Message}", ex);
                return new EventData(InvalidPersistedEvent);
            }
        }

        protected override ValueTask SerializeAsync(EventData input, Stream output, CancellationToken token)
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            JsonSerializer.Serialize(output, input);
            output.WriteByte((byte)'\n');
            output.Flush();
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(logger, LogLevel.Trace, nameof(PersistentEventDataChannel), $"[PersistentEventChannel:Serialize] '{input.EventName}' to disk in {elapsedMs:F2}ms.");
            return ValueTask.CompletedTask;
        }

        private static async Task<string> ExtractJsonObject(Stream input, CancellationToken token)
        {
            StringBuilder sb = new();
            var b = new byte[1];
            while (await input.ReadAsync(b.AsMemory(0, 1), token).ConfigureAwait(false) > 0 && b[0] != '\n')
            {
                sb.Append((char)b[0]);
            }
            return sb.ToString();
        }
    }

    private sealed class PersistentErrorDataChannel(PersistentChannelOptions options, ILogger? logger)
        : PersistentChannel<ErrorData, ErrorData>(options)
    {
        protected override async ValueTask<ErrorData> DeserializeAsync(Stream input, CancellationToken token)
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                var json = await ExtractJsonObject(input, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new ErrorData("invalid", InvalidPersistedError);
                }
                var result = JsonSerializer.Deserialize<ErrorData>(json) ?? new ErrorData("invalid", InvalidPersistedError);
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                AptabaseLogging.Log(logger, LogLevel.Trace, nameof(PersistentErrorDataChannel), $"[PersistentErrorChannel:Deserialize] '{result.ErrorType}' in {elapsedMs:F2}ms.");
                return result;
            }
            catch (JsonException ex)
            {
                AptabaseLogging.Log(logger, LogLevel.Error, nameof(PersistentErrorDataChannel), $"[PersistentErrorChannel:Deserialize] JSON error: {ex.Message}", ex);
                return new ErrorData("invalid", InvalidPersistedError);
            }
        }

        protected override ValueTask SerializeAsync(ErrorData input, Stream output, CancellationToken token)
        {
            var startTimestamp = Stopwatch.GetTimestamp();
            JsonSerializer.Serialize(output, input);
            output.WriteByte((byte)'\n');
            output.Flush();
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(logger, LogLevel.Trace, nameof(PersistentErrorDataChannel), $"[PersistentErrorChannel:Serialize] '{input.ErrorType}' to disk in {elapsedMs:F2}ms.");
            return ValueTask.CompletedTask;
        }

        private static async Task<string> ExtractJsonObject(Stream input, CancellationToken token)
        {
            StringBuilder sb = new();
            var b = new byte[1];
            while (await input.ReadAsync(b.AsMemory(0, 1), token).ConfigureAwait(false) > 0 && b[0] != '\n')
            {
                sb.Append((char)b[0]);
            }
            return sb.ToString();
        }
    }
}