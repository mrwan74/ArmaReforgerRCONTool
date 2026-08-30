using Aptabase.Avalonia;
using Sentry;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.ExceptionalLogContext;
using Serilog.Exceptions;
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
using System.Text.RegularExpressions;
using ReforgerRcon.Models;

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

[SuppressMessage("Major Code Smell", "S3963:Static constructor is required to guarantee thread initialization order", Justification = "Guarantees Serilog pipeline, Sentry and Aptabase integration are configured before background operations start")]
public static partial class AppLogger
{
    private const string SerilogMessageTemplate = "{Message}";
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "appdata", "logs");
    private static readonly ConcurrentQueue<string> Breadcrumbs = new();
    private const int MaxBreadcrumbs = 1000;
    private static readonly Serilog.ILogger Logger;

    public static string SessionId { get; } = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
    public static string InstallationId { get; } = HardwareIdentityService.GetOrCreateHardwareId();
    public static string CurrentLogFilePath { get; }

    [GeneratedRegex(@"(?:password|pwd|rconpassword|#login)\s+([^\s\r\n\t]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex RconPasswordRegex();

    [GeneratedRegex(@"(?:Basic\s+)([A-Za-z0-9+/=]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex BasicAuthHeaderRegex();

    [GeneratedRegex(@"(?:LicenseKey[=:\s]+)([A-Za-z0-9_-]{8,})", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 500)]
    private static partial Regex LicenseKeyRegex();

    public static string SanitizeSensitiveData(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sanitized = input;
        try
        {
            sanitized = RconPasswordRegex().Replace(sanitized, m => $"{m.Value.Split(' ')[0]} [REDACTED]");
            sanitized = BasicAuthHeaderRegex().Replace(sanitized, "Basic [REDACTED]");
            sanitized = LicenseKeyRegex().Replace(sanitized, "LicenseKey=[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[CONTENT_TRUNCATED_DUE_TO_PARSING]";
        }
        catch (Exception)
        {
            return "[CONTENT_REDACTED_DUE_TO_EXCEPTION]";
        }

        return sanitized;
    }

    public static string ResolveAptabaseAppKey()
    {
        try
        {
            var embedded = TelemetrySecrets.GetEmbeddedAppKey();
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                return embedded.Trim();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Embedded Aptabase key notice: {ex.Message}");
        }

        var envKey = Environment.GetEnvironmentVariable("APTABASE_APP_KEY");
        if (!string.IsNullOrWhiteSpace(envKey))
        {
            return envKey.Trim();
        }

        var candidateFiles = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appdata", "aptabase_app_key.txt"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "aptabase_app_key.txt"),
            Path.Combine(AppContext.BaseDirectory, "aptabase_app_key.txt")
        };

        foreach (var file in candidateFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    var fileKey = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(fileKey)) return fileKey;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppLogger] Non-fatal Aptabase key file inspection notice for '{file}': {ex.Message}");
            }
        }

        return "A-EU-3982497621";
    }

    public static string ResolveSentryDsn()
    {
        try
        {
            var embedded = TelemetrySecrets.GetEmbeddedDsn();
            if (!string.IsNullOrWhiteSpace(embedded))
            {
                return embedded.Trim();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Embedded telemetry secret notice: {ex.Message}");
        }

        var envDsn = Environment.GetEnvironmentVariable("SENTRY_DSN");
        if (!string.IsNullOrWhiteSpace(envDsn))
        {
            return envDsn.Trim();
        }

        var candidateFiles = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appdata", "sentry_dsn.txt"),
            Path.Combine(AppContext.BaseDirectory, "Assets", "sentry_dsn.txt"),
            Path.Combine(AppContext.BaseDirectory, "sentry_dsn.txt")
        };

        foreach (var file in candidateFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    var fileDsn = File.ReadAllText(file).Trim();
                    if (!string.IsNullOrWhiteSpace(fileDsn)) return fileDsn;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppLogger] Non-fatal Sentry DSN file inspection notice for '{file}': {ex.Message}");
            }
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

        const string fileOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [T{ThreadId:D2}] [{CallerContext}] {Message:lj}{NewLine}{Exception}";
        const string debugOutputTemplate = "[{Timestamp:HH:mm:ss.fff}] [{Level:u3}] [T{ThreadId:D2}] [{CallerContext}] {Message:lj}{NewLine}{Exception}";

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
                .WriteTo.Async(a => a.File(
                    CurrentLogFilePath,
                    outputTemplate: fileOutputTemplate,
                    formatProvider: CultureInfo.InvariantCulture,
                    fileSizeLimitBytes: 104857600,
                    rollOnFileSizeLimit: true
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

        AptabaseLogging.OnLogMessage += entry =>
        {
            try
            {
                var cleanMsg = SanitizeSensitiveData(entry.Message);
                var caller = $"Aptabase:{entry.Category}";
                var crumb = $"[{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{entry.Level,-5}] [T{entry.ThreadId:D2}] [{caller}] {cleanMsg}";

                Breadcrumbs.Enqueue(crumb);
                while (Breadcrumbs.Count > MaxBreadcrumbs)
                {
                    Breadcrumbs.TryDequeue(out _);
                }

                var serilogLevel = entry.Level switch
                {
                    Microsoft.Extensions.Logging.LogLevel.Trace => LogEventLevel.Verbose,
                    Microsoft.Extensions.Logging.LogLevel.Debug => LogEventLevel.Debug,
                    Microsoft.Extensions.Logging.LogLevel.Information => LogEventLevel.Information,
                    Microsoft.Extensions.Logging.LogLevel.Warning => LogEventLevel.Warning,
                    Microsoft.Extensions.Logging.LogLevel.Error => LogEventLevel.Error,
                    Microsoft.Extensions.Logging.LogLevel.Critical => LogEventLevel.Fatal,
                    _ => LogEventLevel.Information
                };

                using (LogContext.PushProperty("CallerContext", caller))
                {
                    if (entry.Exception != null)
                    {
                        Logger.Write(serilogLevel, entry.Exception, SerilogMessageTemplate, cleanMsg);
                    }
                    else
                    {
                        Logger.Write(serilogLevel, SerilogMessageTemplate, cleanMsg);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppLogger] Aptabase log hook failure: {ex.Message}");
            }
        };

        LogEnvironmentDiagnostics();
    }

    public static void TrackEvent(string eventName, Dictionary<string, object>? props = null)
    {
        if (!Models.AppSettings.IsCrashReportingEnabled())
        {
            return;
        }

        try
        {
            if (AptabaseExtensions.IsInitialized)
            {
                var sanitizedProps = new Dictionary<string, object>
                {
                    ["installation_id"] = InstallationId
                };

                if (props != null)
                {
                    foreach (var (k, v) in props)
                    {
                        if (v is string s)
                        {
                            sanitizedProps[k] = SanitizeSensitiveData(s);
                        }
                        else
                        {
                            sanitizedProps[k] = v;
                        }
                    }
                }

                _ = AptabaseExtensions.Instance.TrackEvent(eventName, sanitizedProps);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Aptabase TrackEvent notice: {ex.Message}");
        }
    }

    public static void TrackError(Exception exception, bool fatal = false)
    {
        if (!Models.AppSettings.IsCrashReportingEnabled())
        {
            return;
        }

        try
        {
            if (AptabaseExtensions.IsInitialized)
            {
                var demystified = exception.Demystify();
                _ = AptabaseExtensions.Instance.TrackError(demystified, fatal);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Aptabase TrackError notice: {ex.Message}");
        }
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
        Info(string.Create(CultureInfo.InvariantCulture, $"Hardware Identity:   {InstallationId}"));
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

    public static TimingScope Measure(string operationName, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
    {
        return new TimingScope(operationName, member, path, line);
    }

    private static void Dispatch(LogLevel level, string message, Exception? ex, IReadOnlyDictionary<string, object?>? context, string member, string path, int line)
    {
        try
        {
            var cleanMessage = SanitizeSensitiveData(message);
            var demystifiedEx = ex?.Demystify();
            var file = Path.GetFileName(path);
            var threadId = Environment.CurrentManagedThreadId;
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            var callerContext = $"{file}:{line} -> {member}()";
            var crumb = string.Create(CultureInfo.InvariantCulture, $"[{timestamp}] [{level,-5}] [T{threadId:D2}] [{callerContext}] {cleanMessage}");
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
                    sentryData[kvp.Key] = SanitizeSensitiveData(kvp.Value?.ToString() ?? "null");
                }
            }

            SentrySdk.AddBreadcrumb(
                message: cleanMessage,
                category: member,
                type: null,
                data: sentryData,
                level: sentryBreadcrumbLevel
            );

            SentrySdk.Metrics.EmitCounter("app_logs_count", 1,
            [
                new KeyValuePair<string, object>("level", level.ToString()),
                new KeyValuePair<string, object>("member", member)
            ]);

            List<IDisposable> disposables = [LogContext.PushProperty("CallerContext", callerContext)];

            if (context?.Count > 0)
            {
                foreach (var kvp in context)
                {
                    disposables.Add(LogContext.PushProperty(kvp.Key, kvp.Value));
                }
            }

            using (new CompositeDisposable(disposables))
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
                    Logger.Write(serilogLevel, demystifiedEx, SerilogMessageTemplate, cleanMessage);

                    if (level is LogLevel.Error or LogLevel.Fatal)
                    {
                        if (Models.AppSettings.IsCrashReportingEnabled() && AptabaseExtensions.IsInitialized)
                        {
                            try
                            {
                                _ = AptabaseExtensions.Instance.TrackError(demystifiedEx, fatal: level == LogLevel.Fatal);
                            }
                            catch (Exception aptaEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[AppLogger] Aptabase TrackError notice: {aptaEx.Message}");
                            }
                        }

                        SentrySdk.CaptureException(demystifiedEx, scope =>
                        {
                            scope.SetTag("caller_member", member);
                            scope.SetTag("caller_file", file);
                            scope.SetTag("caller_line", line.ToString(CultureInfo.InvariantCulture));
                            scope.SetTag("installation_id", InstallationId);
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
                    Logger.Write(serilogLevel, SerilogMessageTemplate, cleanMessage);

                    if (level == LogLevel.Fatal)
                    {
                        SentrySdk.CaptureMessage(cleanMessage, SentryLevel.Fatal);
                    }
                }
            }
        }
        catch (Exception dispatchEx)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger:DispatchFailure] {dispatchEx.Message}");
        }
    }

    public static List<string> GetRecentBreadcrumbs() => [.. Breadcrumbs];

    public static void Flush()
    {
        try
        {
            Log.CloseAndFlush();
            SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Flush canceled cleanly
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Flush notice: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        try
        {
            Log.CloseAndFlush();
            SentrySdk.Close();
        }
        catch (OperationCanceledException)
        {
            // Shutdown cancellation handled cleanly
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Shutdown notice: {ex.Message}");
        }
    }

    public sealed class TimingScope : IDisposable
    {
        private readonly string _operationName;
        private readonly string _member;
        private readonly string _path;
        private readonly int _line;
        private readonly long _startTimestamp;
        private readonly long _initialMemory;
        private bool _isDisposed;

        public TimingScope(string operationName, string member, string path, int line)
        {
            _operationName = operationName;
            _member = member;
            _path = path;
            _line = line;
            _initialMemory = GC.GetAllocatedBytesForCurrentThread();
            _startTimestamp = Stopwatch.GetTimestamp();
            Dispatch(LogLevel.Trace, $"[TIMING:START] {_operationName}", null, null, _member, _path, _line);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
                var memoryAllocated = GC.GetAllocatedBytesForCurrentThread() - _initialMemory;
                var memFormatted = memoryAllocated >= 1024 ? $"{memoryAllocated / 1024.0:F1} KB" : $"{memoryAllocated} B";

                Dispatch(LogLevel.Debug, $"[TIMING:COMPLETED] {_operationName} took {elapsed.TotalMilliseconds:F2} ms (Allocated: {memFormatted})", null, null, _member, _path, _line);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TimingScope] Disposal notice: {ex.Message}");
            }
        }
    }

    private sealed class CompositeDisposable(IEnumerable<IDisposable> disposables) : IDisposable
    {
        private readonly IEnumerable<IDisposable> _disposables = disposables;

        public void Dispose()
        {
            foreach (var d in _disposables)
            {
                try
                {
                    d.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[CompositeDisposable] Element disposal notice: {ex.Message}");
                }
            }
        }
    }
}