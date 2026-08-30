using System;
using System.Collections.Generic;
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
    private readonly Lock _disposeLock = new();
    private bool _disposed;

    public AptabasePersistentClient(string appKey, AptabaseOptions? options, ILogger<AptabasePersistentClient>? logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appKey);

        _options = options;
        _logger = logger;

        var storageDirectory = options?.StoragePath ?? GetDefaultStorageDirectory();
        _client = new AptabaseClientBase(appKey, options, logger);

        _channel = new PersistentEventDataChannel(new PersistentChannelOptions
        {
            SingleReader = true,
            ReliableEnumeration = true,
            PartitionCapacity = MaxPersistedEvents,
            Location = Path.Combine(storageDirectory, "EventData"),
        }, logger);

        _errorChannel = new PersistentErrorDataChannel(new PersistentChannelOptions
        {
            SingleReader = true,
            ReliableEnumeration = true,
            PartitionCapacity = MaxPersistedEvents,
            Location = Path.Combine(storageDirectory, "ErrorData"),
        }, logger);

        _processingTask = Task.Run(ProcessEventsBatchAsync, CancellationToken.None);
        _errorProcessingTask = Task.Run(ProcessErrorsAsync, CancellationToken.None);

        AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabasePersistentClient),
            $"Persistent Aptabase telemetry client active at '{storageDirectory}'.");
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
        if (_disposed)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"TrackEvent skipped: client is disposed. Event: '{eventName}'.");
            return;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        var eventData = new EventData(eventName, props, _options?.ContextInjector);

        try
        {
            await _channel.Writer.WriteAsync(eventData, cancellationToken).ConfigureAwait(false);
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"Persisted event '{eventName}' to disk queue.");
        }
        catch (OperationCanceledException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "TrackEvent write canceled.", ex);
        }
        catch (IOException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"Disk IO error writing event '{eventName}' to persistent storage: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"Unexpected error persisting event '{eventName}': {ex.Message}", ex);
        }
    }

    public Task TrackError(Exception exception, bool fatal = false, CancellationToken cancellationToken = default)
        => ((IErrorTracker)this).TrackError(exception, fatal, fatal ? "crash" : "handled", cancellationToken);

    async Task IErrorTracker.TrackError(Exception exception, bool fatal, string kind, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), $"TrackError skipped: client is disposed. Exception: '{exception.GetType().Name}'.");
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
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabasePersistentClient), $"Persisted error report '{errorData.ErrorType}' to disk queue.");
        }
        catch (OperationCanceledException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "TrackError write canceled.", ex);
        }
        catch (IOException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabasePersistentClient), $"Disk IO error writing error report to persistent storage: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabasePersistentClient), $"Unexpected failure persisting error report: {ex.Message}", ex);
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
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"Subscriber exception in OnErrorOccurred: {ex.Message}", ex);
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
                        if (eventData.EventName != InvalidPersistedEvent)
                        {
                            batch.Add(eventData);
                        }
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
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"Persistent event batch transmission failed. Retrying in {RetrySeconds}s: {ex.Message}", ex);
                await SafeDelayAsync(RetrySeconds * 1000, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"Unexpected error in batch event processor: {ex.Message}", ex);
            }
        }
    }

    private async Task ProcessErrorsAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                bool canRead = false;
                try
                {
                    canRead = await _errorChannel.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false);
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
                    if (errorData.ErrorType == InvalidPersistedError)
                    {
                        continue;
                    }

                    await _client.SendErrorAsync(errorData, CancellationToken.None).ConfigureAwait(false);
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
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"Persistent error transmission failed. Retrying in {RetrySeconds}s: {ex.Message}", ex);
                await SafeDelayAsync(RetrySeconds * 1000, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabasePersistentClient), $"Unexpected error in error channel reader: {ex.Message}", ex);
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

        AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabasePersistentClient), "AptabasePersistentClient shutting down channels and background tasks...");
        _channel.Writer.TryComplete();
        _errorChannel.Writer.TryComplete();

        try
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "CTS disposed notice: " + ex.Message, ex);
        }

        if (!_processingTask.IsCompleted || !_errorProcessingTask.IsCompleted)
        {
            try
            {
                await Task.WhenAny(Task.WhenAll(_processingTask, _errorProcessingTask), Task.Delay(300, CancellationToken.None)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Clean cancellation
            }
            catch (Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabasePersistentClient), "Background task shutdown notice: " + ex.Message, ex);
            }
        }

        _cts.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);

        GC.SuppressFinalize(this);
    }

    private sealed class PersistentEventDataChannel(PersistentChannelOptions options, ILogger? logger)
        : PersistentChannel<EventData, EventData>(options)
    {
        protected override async ValueTask<EventData> DeserializeAsync(Stream input, CancellationToken token)
        {
            try
            {
                var json = await ExtractJsonObject(input, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new EventData(InvalidPersistedEvent);
                }
                return JsonSerializer.Deserialize<EventData>(json) ?? new EventData(InvalidPersistedEvent);
            }
            catch (JsonException ex)
            {
                AptabaseLogging.Log(logger, LogLevel.Error, nameof(PersistentEventDataChannel), $"JSON corruption in event file: {ex.Message}", ex);
                return new EventData(InvalidPersistedEvent);
            }
        }

        protected override ValueTask SerializeAsync(EventData input, Stream output, CancellationToken token)
        {
            JsonSerializer.Serialize(output, input);
            output.WriteByte((byte)'\n');
            output.Flush();
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
            try
            {
                var json = await ExtractJsonObject(input, token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new ErrorData("invalid", InvalidPersistedError);
                }
                return JsonSerializer.Deserialize<ErrorData>(json) ?? new ErrorData("invalid", InvalidPersistedError);
            }
            catch (JsonException ex)
            {
                AptabaseLogging.Log(logger, LogLevel.Error, nameof(PersistentErrorDataChannel), $"JSON corruption in error file: {ex.Message}", ex);
                return new ErrorData("invalid", InvalidPersistedError);
            }
        }

        protected override ValueTask SerializeAsync(ErrorData input, Stream output, CancellationToken token)
        {
            JsonSerializer.Serialize(output, input);
            output.WriteByte((byte)'\n');
            output.Flush();
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