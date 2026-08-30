using System;
using System.Collections.Generic;
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
    private readonly Lock _disposeLock = new();
    private bool _disposed;

    public AptabaseClient(string appKey, AptabaseOptions? options, ILogger<AptabaseClient>? logger)
    {
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
        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient), "AptabaseClient batch processor initialized with bounded channel capacity 1000.");
    }

    public Task TrackEvent(string eventName, Dictionary<string, object>? props = null, CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"TrackEvent skipped: client is disposed. Event: '{eventName}'.");
            return Task.CompletedTask;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        try
        {
            var ev = new EventData(eventName, props, _options?.ContextInjector);
            if (!_channel.Writer.TryWrite(ev))
            {
                AptabaseLogging.Log(_logger, LogLevel.Warning, nameof(AptabaseClient), $"Event buffer capacity reached. Dropped oldest event to queue: '{eventName}'.");
            }
            else
            {
                AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"Enqueued event '{eventName}' with {ev.Props?.Count ?? 0} property entries.");
            }
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"Unexpected failure writing event '{eventName}' to pipeline: {ex.Message}", ex);
        }

        return Task.CompletedTask;
    }

    public Task TrackError(Exception exception, bool fatal = false, CancellationToken cancellationToken = default)
        => ((IErrorTracker)this).TrackError(exception, fatal, fatal ? "crash" : "handled", cancellationToken);

    async Task IErrorTracker.TrackError(Exception exception, bool fatal, string kind, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"TrackError skipped: client is disposed. Exception: '{exception.GetType().Name}'.");
            return;
        }

        ArgumentNullException.ThrowIfNull(exception);

        var userMessage = fatal
            ? $"A critical application failure occurred: {exception.Message}."
            : $"An error occurred: {exception.Message}.";

        NotifyErrorOccurred(exception, fatal, kind, userMessage);

        var errorData = ErrorData.FromException(exception, fatal, kind);

        try
        {
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient), $"Transmitting {(fatal ? "FATAL" : "NON-FATAL")} error report ({exception.GetType().Name}) via AptabaseClientBase...");
            await _client.TrackError(errorData, cancellationToken).ConfigureAwait(false);
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient), $"Error telemetry report for '{exception.GetType().Name}' delivered successfully.");
        }
        catch (OperationCanceledException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), "TrackError operation was canceled during application shutdown.", ex);
        }
        catch (AptabaseException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"Aptabase HTTP error report transmission failure: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseClient), $"Unhandled fault delivering error telemetry: {ex.Message}", ex);
        }
    }

    private void NotifyErrorOccurred(Exception exception, bool fatal, string kind, string userMessage)
    {
        try
        {
            OnErrorOccurred?.Invoke(this, new AptabaseErrorEventArgs(exception, fatal, kind, userMessage));
            _options?.OnUserFacingNotification?.Invoke(userMessage, exception, fatal);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"Subscriber exception in OnErrorOccurred callback: {ex.Message}", ex);
        }
    }

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
                        canRead = await _channel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false);
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

                if (batch.Count > 0)
                {
                    await _client.TrackEvents(batch, CancellationToken.None).ConfigureAwait(false);
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
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseClient), $"Failed to send event batch to telemetry endpoint: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseClient), $"Unexpected error in event processor worker loop: {ex.Message}", ex);
            }
        }
    }

    private static async Task SafeDelayAsync(int millisecondsDelay, CancellationToken cancellationToken)
    {
        if (millisecondsDelay <= 0 || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(static state => ((TaskCompletionSource?)state)?.TrySetResult(), tcs).ConfigureAwait(false))
        {
            await Task.WhenAny(Task.Delay(millisecondsDelay, CancellationToken.None), tcs.Task).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseClient), "AptabaseClient initiating graceful shutdown and flush...");
        _channel.Writer.TryComplete();

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), "CancellationTokenSource already disposed during shutdown.", ex);
        }

        if (!_processingTask.IsCompleted)
        {
            try
            {
                await Task.WhenAny(_processingTask, Task.Delay(300, CancellationToken.None)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Clean cancellation
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseClient), $"Background worker shutdown notice: {ex.Message}", ex);
            }
        }

        _cts.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }
}