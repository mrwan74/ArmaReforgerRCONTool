using System;
using System.Collections.Generic;
using System.Diagnostics;
using Velopack.Logging;

namespace ReforgerRcon.Services;

/// <summary>
/// Bridges all Velopack engine diagnostic logs directly into ARRT's AppLogger and Visual Studio Debug output.
/// </summary>
public sealed class VelopackLoggerBridge : IVelopackLogger
{
    private const string VelopackCategory = "Velopack";
    private const string ThreadIdKey = "thread_id";

    public static VelopackLoggerBridge Instance { get; } = new();

    private VelopackLoggerBridge()
    {
    }

    public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
    {
        if (string.IsNullOrWhiteSpace(message) && exception == null) return;

        var context = new Dictionary<string, object?>
        {
            ["velopack_level"] = logLevel.ToString(),
            [ThreadIdKey] = Environment.CurrentManagedThreadId
        };

        var formattedMessage = $"[{VelopackCategory}] {message}";

        switch (logLevel)
        {
            case VelopackLogLevel.Trace:
                AppLogger.Trace(formattedMessage, context);
                break;

            case VelopackLogLevel.Debug:
                AppLogger.Debug(formattedMessage, context);
                break;

            case VelopackLogLevel.Information:
                AppLogger.Info(formattedMessage, context);
                break;

            case VelopackLogLevel.Warning:
                AppLogger.Warn(formattedMessage, exception, context);
                break;

            case VelopackLogLevel.Error:
                AppLogger.Error(formattedMessage, exception, context);
                break;

            case VelopackLogLevel.Critical:
                AppLogger.Fatal(formattedMessage, exception, context);
                break;
        }

        if (Debugger.IsAttached)
        {
            var debugLine = $"[{DateTime.Now:HH:mm:ss.fff}] [Velopack:{logLevel}] {message}";
            if (exception != null)
            {
                debugLine += $"{Environment.NewLine}{exception}";
            }
            Debugger.Log(0, VelopackCategory, debugLine + Environment.NewLine);
        }
    }
}