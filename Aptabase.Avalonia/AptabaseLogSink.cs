using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

public sealed class AptabaseLogSink(LogEventLevel minimumLevel, ILogger? logger = null) : ILogSink
{
    private readonly LogEventLevel _minimumLevel = minimumLevel;
    private readonly ILogger? _logger = logger;

    public bool IsEnabled(LogEventLevel level, string area) => level >= _minimumLevel;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (!IsEnabled(level, area)) return;

        var sourceName = source?.GetType().Name ?? "Avalonia";
        var logLevel = ConvertLogLevel(level);

        AptabaseLogging.Log(_logger, logLevel, $"Avalonia:{area}", $"[{sourceName}] {messageTemplate}");
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (!IsEnabled(level, area)) return;

        var sourceName = source?.GetType().Name ?? "Avalonia";
        var logLevel = ConvertLogLevel(level);
        string formattedMessage;

        try
        {
            formattedMessage = propertyValues.Length > 0
                ? string.Format(System.Globalization.CultureInfo.InvariantCulture, messageTemplate, propertyValues)
                : messageTemplate;
        }
        catch
        {
            formattedMessage = $"{messageTemplate} (Values: {string.Join(", ", propertyValues)})";
        }

        AptabaseLogging.Log(_logger, logLevel, $"Avalonia:{area}", $"[{sourceName}] {formattedMessage}");
    }

    private static LogLevel ConvertLogLevel(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => LogLevel.Trace,
        LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        LogEventLevel.Error => LogLevel.Error,
        LogEventLevel.Fatal => LogLevel.Critical,
        _ => LogLevel.Information
    };
}