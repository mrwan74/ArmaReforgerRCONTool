using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

public sealed record AptabaseLogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    Exception? Exception,
    int ThreadId,
    int? TaskId,
    IReadOnlyDictionary<string, object>? Context = null
);

public static class AptabaseLogging
{
    public static event Action<AptabaseLogEntry>? OnLogMessage;

    [SuppressMessage("Performance", "CA1848:Use the LoggerMessage delegates", Justification = "Dynamic SDK log dispatcher routing varying runtime LogLevels")]
    internal static void Log(
        ILogger? logger,
        LogLevel level,
        string category,
        string message,
        Exception? exception = null,
        Dictionary<string, object>? context = null)
    {
        var entry = new AptabaseLogEntry(
            DateTimeOffset.UtcNow,
            level,
            category,
            message,
            exception,
            Environment.CurrentManagedThreadId,
            Task.CurrentId,
            context
        );

        try
        {
            OnLogMessage?.Invoke(entry);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AptabaseLogging] Subscriber event notice: {ex.Message}");
        }

        if (logger?.IsEnabled(level) is true)
        {
            if (exception is not null)
            {
                logger.Log(level, exception, "[{Category}] [Thread:{ThreadId}] {Message}", category, entry.ThreadId, message);
            }
            else
            {
                logger.Log(level, "[{Category}] [Thread:{ThreadId}] {Message}", category, entry.ThreadId, message);
            }
        }
    }
}