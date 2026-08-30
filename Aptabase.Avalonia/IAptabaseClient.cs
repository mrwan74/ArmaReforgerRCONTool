namespace Aptabase.Avalonia;

public sealed class AptabaseErrorEventArgs(Exception exception, bool isFatal, string source, string userMessage) : EventArgs
{
    public Exception Exception { get; } = exception;
    public bool IsFatal { get; } = isFatal;
    public string Source { get; } = source;
    public string UserMessage { get; } = userMessage;
    public DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
}

public interface IAptabaseClient : IAsyncDisposable
{
    event EventHandler<AptabaseErrorEventArgs>? OnErrorOccurred;

    Task TrackEvent(string eventName, Dictionary<string, object>? props = null, CancellationToken cancellationToken = default);

    Task TrackError(Exception exception, bool fatal = false, CancellationToken cancellationToken = default);
}

internal interface IErrorTracker
{
    Task TrackError(Exception exception, bool fatal, string kind, CancellationToken cancellationToken = default);
}