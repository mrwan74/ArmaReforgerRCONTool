using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

public sealed class AptabaseClient : IAptabaseClient, IErrorTracker
{
    public event EventHandler<AptabaseErrorEventArgs>? OnErrorOccurred;

    private const int MaxBatchSize = 25;
    private const int FlushIntervalMs = 2000;

    private readonly Channel<EventData> _channel;
    private readonly Task _processingTask;
    private readonly AptabaseClientBase _client;
    private readonly ILogger<AptabaseClient>? _logger;
    private readonly AptabaseOptions? _options;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;
    private long _totalEventsEnqueued;
    private long _totalBatchesFlushed;
    private long _totalErrorsHandled;

    [SuppressMessage("AsyncUsage", "PH_S007:AvoidStartingThreadsOrTasksInConstructor", Justification = "Background channel batch processor must be initialized alongside client lifecycle")]
    public AptabaseClient(string appKey, AptabaseOptions? options, ILogger<AptabaseClient>? logger)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        _options = options;
        _logger = logger;
        _client = new AptabaseClientBase(appKey, options, logger);
        _channel = Channel.CreateBounded<EventData>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        _processingTask = Task.Run(ProcessEventsBatchAsync, CancellationToken.None);

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient),
            $"[AptabaseClient:Init] In-memory telemetry pipeline initialized in {elapsedMs:F2}ms (ChannelCapacity=1000, MaxBatchSize={MaxBatchSize}, FlushInterval={FlushIntervalMs}ms).");
    }

    public Task TrackEvent(string eventName, Dictionary<string, object>? props = null, CancellationToken cancellationToken = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _disposed) != 0)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"[AptabaseClient:TrackEvent] Discarded: instance disposed. Event: '{eventName}'.");
            return Task.CompletedTask;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        try
        {
            var ev = new EventData(eventName, props, _options?.ContextInjector);
            if (!_channel.Writer.TryWrite(ev))
            {
                AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabaseClient), $"[AptabaseClient:TrackEvent] Bounded event buffer full (1000 items). Oldest event dropped to write: '{eventName}'.");
            }
            else
            {
                var count = Interlocked.Increment(ref _totalEventsEnqueued);
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"[AptabaseClient:TrackEvent] Enqueued '{eventName}' (TotalEnqueued={count}, Props={ev.Props?.Count ?? 0}) in {elapsedMs:F2}ms.");
            }
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"[AptabaseClient:TrackEvent] Failed enqueuing telemetry event '{eventName}': {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public Task TrackError(Exception exception, bool fatal = false, CancellationToken cancellationToken = default)
        => ((IErrorTracker)this).TrackError(exception, fatal, fatal ? "crash" : "handled", cancellationToken);

    async Task IErrorTracker.TrackError(Exception exception, bool fatal, string kind, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        if (Volatile.Read(ref _disposed) != 0)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"[AptabaseClient:TrackError] Discarded: instance disposed. Exception: '{exception.GetType().FullName}'.");
            return;
        }

        ArgumentNullException.ThrowIfNull(exception);

        var errCount = Interlocked.Increment(ref _totalErrorsHandled);
        var userMessage = fatal
            ? $"A critical application failure occurred: {exception.Message}."
            : $"An error occurred: {exception.Message}.";

        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient),
            $"[AptabaseClient:TrackError] Processing error report #{errCount} for '{exception.GetType().Name}' (Fatal={fatal}, Kind={kind}, Message='{exception.Message}')...");

        NotifyErrorOccurred(exception, fatal, kind, userMessage);

        var errorData = ErrorData.FromException(exception, fatal, kind);

        try
        {
            await _client.TrackError(errorData, cancellationToken).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient),
                $"[AptabaseClient:TrackError] Delivered error report #{errCount} for '{exception.GetType().Name}' in {elapsedMs:F2}ms (Fatal={fatal}).");
        }
        catch (OperationCanceledException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), "[AptabaseClient:TrackError] Error transmission cancelled during shutdown.", ex);
        }
        catch (AptabaseException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"[AptabaseClient:TrackError] Aptabase endpoint rejected error report: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseClient), $"[AptabaseClient:TrackError] Unhandled error during error dispatch: {ex.Message}", ex);
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
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"[AptabaseClient:Notify] Dispatched local OnErrorOccurred handlers in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"[AptabaseClient:Notify] Callback subscriber threw exception: {ex.Message}", ex);
        }
    }

    [SuppressMessage("AsyncUsage", "PH_P008:ThrowOperationCanceledException", Justification = "Background channel batch processor loop terminates gracefully on shutdown without throwing first-chance exceptions")]
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
                    batch.Add(eventData);
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
                        batch.Add(eventData);
                    }
                }

                if (batch.Count > 0 && !_cts.IsCancellationRequested)
                {
                    var batchStart = Stopwatch.GetTimestamp();
                    await _client.TrackEvents(batch, _cts.Token).ConfigureAwait(false);
                    var flushedCount = Interlocked.Increment(ref _totalBatchesFlushed);
                    var batchElapsedMs = Stopwatch.GetElapsedTime(batchStart).TotalMilliseconds;
                    AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient),
                        $"[AptabaseClient:Worker] Flushed event batch #{flushedCount} ({batch.Count} events) in {batchElapsedMs:F2}ms.");
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
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"[AptabaseClient:Worker] Transmission error sending batch: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseClient), $"[AptabaseClient:Worker] Unhandled loop error: {ex.Message}", ex);
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
        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient), "[AptabaseClient:Dispose] Initiating graceful shutdown of AptabaseClient...");

        _channel.Writer.TryComplete();

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), "[AptabaseClient:Dispose] CTS notice: " + ex.Message, ex);
        }

        try
        {
            await _processingTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }
        catch (TimeoutException)
        {
            AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabaseClient), "[AptabaseClient:Dispose] Worker task timed out during shutdown.");
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), "[AptabaseClient:Dispose] Worker notice: " + ex.Message, ex);
        }

        _cts.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);

        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient),
            $"[AptabaseClient:Dispose] Teardown complete in {elapsedMs:F2}ms (Enqueued={_totalEventsEnqueued}, FlushedBatches={_totalBatchesFlushed}, ErrorsHandled={_totalErrorsHandled}).");

        GC.SuppressFinalize(this);
    }
}