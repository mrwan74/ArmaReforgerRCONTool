using System;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Aptabase.Avalonia;

internal sealed class ErrorData
{
    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = string.Empty;

    [JsonPropertyName("errorType")]
    public string ErrorType { get; set; } = string.Empty;

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("osName")]
    public string? OsName { get; set; }

    [JsonPropertyName("osVersion")]
    public string? OsVersion { get; set; }

    [JsonPropertyName("appVersion")]
    public string? AppVersion { get; set; }

    [JsonPropertyName("sdkVersion")]
    public string? SdkVersion { get; set; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("isDebug")]
    public bool IsDebug { get; set; }

    public ErrorData()
    {
    }

    [JsonConstructor]
    public ErrorData(string errorMessage, string errorType, string? stackTrace = null)
    {
        ErrorMessage = errorMessage;
        ErrorType = errorType;
        StackTrace = stackTrace;
    }

    public static ErrorData FromException(Exception exception, bool fatal, string kind)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var prefix = fatal ? "Fatal " : "";
        var detailedMessage = BuildDetailedExceptionMessage(exception);
        var aggregatedStackTrace = BuildAggregatedStackTrace(exception);

        return new ErrorData(
            $"{prefix}{exception.GetType().Name}: {detailedMessage}",
            exception.GetType().Name,
            aggregatedStackTrace)
        {
            Severity = fatal ? "fatal" : "error",
            Kind = kind,
        };
    }

    private static string BuildDetailedExceptionMessage(Exception exception)
    {
        var sb = new StringBuilder(exception.Message);
        var current = exception.InnerException;

        for (var depth = 1; current is not null && depth <= 10; depth++)
        {
            sb.Append(CultureInfo.InvariantCulture, $" ---> [{current.GetType().Name}]: {current.Message}");
            current = current.InnerException;
        }

        return sb.ToString();
    }

    private static string BuildAggregatedStackTrace(Exception exception)
    {
        var sb = new StringBuilder();
        sb.AppendLine(exception.StackTrace);

        var current = exception.InnerException;

        for (var depth = 1; current is not null && depth <= 10; depth++)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"--- End of inner exception stack trace ({current.GetType().FullName}) ---");
            sb.AppendLine(current.StackTrace);
            current = current.InnerException;
        }

        return sb.ToString().TrimEnd();
    }
}