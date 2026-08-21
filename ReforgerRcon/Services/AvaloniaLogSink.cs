using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Logging;

namespace ReforgerRcon.Services;

public partial class AvaloniaLogSink(LogEventLevel minimumLevel = LogEventLevel.Warning) : ILogSink
{
    private readonly LogEventLevel _minimumLevel = minimumLevel;

    [GeneratedRegex(@"\{([a-zA-Z_][a-zA-Z0-9_]*)\}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedPropertyRegex();

    public bool IsEnabled(LogEventLevel level, string area) => level >= _minimumLevel;

    [SuppressMessage("Major Code Smell", "S2629:Logging templates should be constant", Justification = "Diagnostic adapter")]
    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        Forward(level, area, source, messageTemplate, []);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        Forward(level, area, source, messageTemplate, propertyValues);
    }

    private static void Forward(LogEventLevel level, string area, object? source, string messageTemplate, object?[] propertyValues)
    {
        var formatted = FormatMessage(messageTemplate, propertyValues);
        var sourceName = source?.GetType().Name ?? "Visual";
        var msg = $"[Avalonia:{area}] [{sourceName}] {formatted}";

        if (area.Equals("Binding", StringComparison.OrdinalIgnoreCase) && formatted.Contains("DataContext: Value is null", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Trace(msg);
            return;
        }

        switch (level)
        {
            case LogEventLevel.Verbose:
                AppLogger.Trace(msg);
                break;
            case LogEventLevel.Debug:
                AppLogger.Debug(msg);
                break;
            case LogEventLevel.Information:
                AppLogger.Info(msg);
                break;
            case LogEventLevel.Warning:
                AppLogger.Warn(msg);
                break;
            case LogEventLevel.Error:
            case LogEventLevel.Fatal:
                AppLogger.Error(msg);
                break;
        }
    }

    private static string FormatMessage(string template, object?[] propertyValues)
    {
        if (propertyValues == null || propertyValues.Length == 0)
        {
            return template;
        }

        try
        {
            int index = 0;
            var indexedTemplate = NamedPropertyRegex().Replace(template, _ =>
            {
                var currentIdx = index++;
                return currentIdx < propertyValues.Length ? $"{{{currentIdx}}}" : "null";
            });

            return string.Format(CultureInfo.InvariantCulture, indexedTemplate, propertyValues);
        }
        catch (FormatException)
        {
            return $"{template} [{string.Join(", ", propertyValues)}]";
        }
    }
}