using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Sentry;
using Serilog;
using Serilog.Context;
using Serilog.Enrichers.WithCaller;
using Serilog.Events;
using Serilog.ExceptionalLogContext;
using Serilog.Exceptions;

namespace ReforgerRcon.Services;

public enum LogLevel
{
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal
}

[SuppressMessage("Major Code Smell", "S3963:Static constructor is required to guarantee thread initialization order", Justification = "Guarantees Serilog pipeline and Sentry integration are configured before background operations start")]
public static class AppLogger
{
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "appdata", "logs");
    private static readonly ConcurrentQueue<string> Breadcrumbs = new();
    private const int MaxBreadcrumbs = 250;
    private static readonly Serilog.ILogger Logger;

    public static string SessionId { get; } = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    public static string CurrentLogFilePath { get; }

    public static event Action<LogLevel, string, Exception?>? LogEmitted;

    public static string ResolveSentryDsn()
    {
        var envDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
        if (!string.IsNullOrWhiteSpace(envDsn))
        {
            return envDsn.Trim();
        }

        try
        {
            var localFile = Path.Combine(AppContext.BaseDirectory, "appdata", "sentry_dsn.txt");
            if (File.Exists(localFile))
            {
                var fileDsn = File.ReadAllText(localFile).Trim();
                if (!string.IsNullOrWhiteSpace(fileDsn))
                {
                    return fileDsn;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Non-fatal Sentry DSN file inspection notice: {ex.Message}");
        }

        return string.Empty;
    }

    static AppLogger()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                Directory.CreateDirectory(LogDirectory);
            }

            CleanupOldSessionLogs();
        }
        catch (IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Directory creation I/O error: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Directory creation permission error: {ex.Message}");
        }

        CurrentLogFilePath = Path.Combine(LogDirectory, $"reforger_rcon_session_{SessionId}.log");

        const string fileOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [T{ThreadId:D2}] [{Caller}] {Message:lj}{NewLine}{Exception}";
        const string debugOutputTemplate = "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [T{ThreadId:D2}] [{Caller}] {Message:lj}{NewLine}{Exception}";

        try
        {
            var config = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .Enrich.FromLogContext()
                .Enrich.WithExceptionalLogContext()
                .Enrich.WithThreadId()
                .Enrich.WithThreadName()
                .Enrich.WithProcessId()
                .Enrich.WithProcessName()
                .Enrich.WithDemystifiedStackTraces()
                .Enrich.WithExceptionDetails()
                .Enrich.WithCaller()
                .WriteTo.Async(a => a.File(
                    CurrentLogFilePath,
                    outputTemplate: fileOutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture
                ))
                .WriteTo.Debug(
                    outputTemplate: debugOutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture
                );

            var dsn = ResolveSentryDsn();
            if (!string.IsNullOrWhiteSpace(dsn))
            {
                config = config.WriteTo.Sentry(o =>
                {
                    o.Dsn = dsn;
                    o.InitializeSdk = false;
                    o.MinimumBreadcrumbLevel = LogEventLevel.Debug;
                    o.MinimumEventLevel = LogEventLevel.Error;
                });
            }

            Log.Logger = config.CreateLogger();
            Logger = Log.Logger;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Fallback logger initialization: {ex.Message}");
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger();
            Logger = Log.Logger;
        }

        LogEnvironmentDiagnostics();
    }

    private static void CleanupOldSessionLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory)) return;

            var logFiles = new DirectoryInfo(LogDirectory)
                .GetFiles("reforger_rcon_session_*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            const int maxRetainedSessions = 30;
            var cutoffDate = DateTime.UtcNow.AddDays(-14);

            for (int i = 0; i < logFiles.Count; i++)
            {
                var file = logFiles[i];
                if (i >= maxRetainedSessions || file.LastWriteTimeUtc < cutoffDate)
                {
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[AppLogger] Could not delete old session log '{file.Name}': {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Session log cleanup encountered an error: {ex.Message}");
        }
    }

    private static void LogEnvironmentDiagnostics()
    {
        Info("================================================================================");
        Info("APPLICATION INITIALIZATION: ARMA Reforger RCON Management Tool (ARRT)");
        Info(string.Create(CultureInfo.InvariantCulture, $"Timestamp (UTC):     {DateTime.UtcNow:O}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Session Log File:    {CurrentLogFilePath}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"OS Description:      {RuntimeInformation.OSDescription}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"OS Architecture:     {RuntimeInformation.OSArchitecture}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Process Arch:        {RuntimeInformation.ProcessArchitecture}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"CLR Runtime:         {RuntimeInformation.FrameworkDescription}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Process ID:          {Environment.ProcessId}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Base Directory:      {AppContext.BaseDirectory}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Culture:             {CultureInfo.CurrentCulture.Name}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"RAM Working Set:     {Environment.WorkingSet / (1024 * 1024)} MB"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Processor Count:     {Environment.ProcessorCount} Cores"));
        Info("================================================================================");
    }

    public static void Trace(string message, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Trace, message, null, null, member, path, line);

    public static void Trace(string message, IReadOnlyDictionary<string, object?>? context, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Trace, message, null, context, member, path, line);

    public static void Debug(string message, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Debug, message, null, null, member, path, line);

    public static void Debug(string message, IReadOnlyDictionary<string, object?>? context, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Debug, message, null, context, member, path, line);

    public static void Info(string message, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Info, message, null, null, member, path, line);

    public static void Info(string message, IReadOnlyDictionary<string, object?>? context, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Info, message, null, context, member, path, line);

    public static void Warn(string message, Exception? ex = null, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Warn, message, ex, null, member, path, line);

    public static void Warn(string message, Exception? ex, IReadOnlyDictionary<string, object?>? context, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Warn, message, ex, context, member, path, line);

    public static void Error(string message, Exception? ex = null, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Error, message, ex, null, member, path, line);

    public static void Error(string message, Exception? ex, IReadOnlyDictionary<string, object?>? context, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Error, message, ex, context, member, path, line);

    public static void Fatal(string message, Exception? ex = null, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Fatal, message, ex, null, member, path, line);

    public static void Fatal(string message, Exception? ex, IReadOnlyDictionary<string, object?>? context, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
        => Dispatch(LogLevel.Fatal, message, ex, context, member, path, line);

    private static void Dispatch(LogLevel level, string message, Exception? ex, IReadOnlyDictionary<string, object?>? context, string member, string path, int line)
    {
        var demystifiedEx = ex?.Demystify();
        var file = Path.GetFileName(path);
        var threadId = Environment.CurrentManagedThreadId;
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

        var crumb = string.Create(CultureInfo.InvariantCulture, $"[{timestamp}] [{level,-5}] [T{threadId:D2}] [{file}:{line} -> {member}()] {message}");
        Breadcrumbs.Enqueue(crumb);
        while (Breadcrumbs.Count > MaxBreadcrumbs)
        {
            Breadcrumbs.TryDequeue(out _);
        }

        var sentryBreadcrumbLevel = level switch
        {
            LogLevel.Trace or LogLevel.Debug => BreadcrumbLevel.Debug,
            LogLevel.Info => BreadcrumbLevel.Info,
            LogLevel.Warn => BreadcrumbLevel.Warning,
            LogLevel.Error or LogLevel.Fatal => BreadcrumbLevel.Error,
            _ => BreadcrumbLevel.Info
        };

        Dictionary<string, string>? sentryData = null;
        if (context != null)
        {
            sentryData = [];
            foreach (var kvp in context)
            {
                sentryData[kvp.Key] = kvp.Value?.ToString() ?? "null";
            }
        }

        SentrySdk.AddBreadcrumb(
            message: message,
            category: member,
            type: null,
            data: sentryData,
            level: sentryBreadcrumbLevel
        );

        IDisposable? propertyScope = null;
        if (context?.Count > 0)
        {
            List<IDisposable> disposables = [];
            foreach (var kvp in context)
            {
                disposables.Add(LogContext.PushProperty(kvp.Key, kvp.Value));
            }
            propertyScope = new CompositeDisposable(disposables);
        }

        using (propertyScope)
        {
            var serilogLevel = level switch
            {
                LogLevel.Trace => LogEventLevel.Verbose,
                LogLevel.Debug => LogEventLevel.Debug,
                LogLevel.Info => LogEventLevel.Information,
                LogLevel.Warn => LogEventLevel.Warning,
                LogLevel.Error => LogEventLevel.Error,
                LogLevel.Fatal => LogEventLevel.Fatal,
                _ => LogEventLevel.Information
            };

            if (demystifiedEx != null)
            {
                Logger.Write(serilogLevel, demystifiedEx, "{Message}", message);

                if (level is LogLevel.Error or LogLevel.Fatal)
                {
                    SentrySdk.CaptureException(demystifiedEx, scope =>
                    {
                        scope.SetTag("caller_member", member);
                        scope.SetTag("caller_file", file);
                        scope.SetTag("caller_line", line.ToString(CultureInfo.InvariantCulture));
                        if (context != null)
                        {
                            foreach (var kvp in context)
                            {
                                scope.SetExtra(kvp.Key, kvp.Value);
                            }
                        }
                    });
                }
            }
            else
            {
                Logger.Write(serilogLevel, "{Message}", message);

                if (level == LogLevel.Fatal)
                {
                    SentrySdk.CaptureMessage(message, SentryLevel.Fatal);
                }
            }
        }

        try
        {
            LogEmitted?.Invoke(level, crumb, demystifiedEx);
        }
        catch (Exception exHandler)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] LogEmitted event subscriber error: {exHandler.Message}");
        }
    }

    public static List<string> GetRecentBreadcrumbs() => [.. Breadcrumbs];

    public static void Flush()
    {
        Log.CloseAndFlush();
        SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
    }

    public static void Shutdown()
    {
        Log.CloseAndFlush();
        SentrySdk.Close();
    }

    private sealed class CompositeDisposable(IEnumerable<IDisposable> disposables) : IDisposable
    {
        private readonly IEnumerable<IDisposable> _disposables = disposables;

        public void Dispose()
        {
            foreach (var d in _disposables)
            {
                d.Dispose();
            }
        }
    }
}