using Aptabase.Avalonia;
using Ben.Demystifier;
using ReforgerRcon.Models;
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
using System.Threading;
using System.Threading.Tasks;

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

public static partial class AppLogger
{
    private const string SerilogMessageTemplate = "{Message}";
    private const string CallerContextPropertyName = "CallerContext";
    private const int MaxBreadcrumbs = 3000;

    private static readonly Queue<string> Breadcrumbs = new();
    private static readonly Lock BreadcrumbsLock = new();
    private static readonly ConcurrentQueue<(LogEventLevel Level, string Message, Exception? Exception, string CallerContext, string AppLogLevel, string? StructuredContext)> EarlyLogBuffer = new();
    private static readonly Lock InitLock = new();

    private static Serilog.ILogger? _logger;
    private static int _isInDispatchFailure;
    private static long _totalLogsDispatched;
    private static long _totalSanitizationsExecuted;
    private static bool _isSerilogInitialized;

    public static string SessionId { get; } = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

    private static string? _installationId;
    public static string InstallationId
    {
        get
        {
            if (string.IsNullOrEmpty(_installationId))
            {
                _installationId = HardwareIdentityService.GetOrCreateHardwareId();
            }
            return _installationId;
        }
    }

    public static string LogDirectory => AppPaths.LogsDirectory;
    public static string CurrentLogFilePath => Path.Combine(LogDirectory, $"reforger_rcon_session_{SessionId}.log");
    public static string CurrentErrorLogFilePath => Path.Combine(LogDirectory, $"reforger_rcon_errors_{SessionId}.log");

    [GeneratedRegex(@"(?:password|pwd|rconpassword|#login)\s+([^\s\r\n\t]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex RconPasswordRegex();

    [GeneratedRegex(@"(?:Basic\s+)([A-Za-z0-9+/=]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex BasicAuthHeaderRegex();

    [GeneratedRegex(@"(?:LicenseKey[=:\s]+)([A-Za-z0-9_-]{8,})", RegexOptions.IgnoreCase | RegexOptions.Compiled, matchTimeoutMilliseconds: 250)]
    private static partial Regex LicenseKeyRegex();

    public static string SanitizeSensitiveData(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        Interlocked.Increment(ref _totalSanitizationsExecuted);

        if (input.IndexOf("password", StringComparison.OrdinalIgnoreCase) < 0 &&
            input.IndexOf("pwd", StringComparison.OrdinalIgnoreCase) < 0 &&
            input.IndexOf("login", StringComparison.OrdinalIgnoreCase) < 0 &&
            input.IndexOf("Basic", StringComparison.OrdinalIgnoreCase) < 0 &&
            input.IndexOf("LicenseKey", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return input;
        }

        var sanitized = input;
        try
        {
            sanitized = RconPasswordRegex().Replace(sanitized, static m => $"{m.Value.Split(' ')[0]} [REDACTED]");
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

    public static string ResolveAptabaseAppKey() => TelemetrySecrets.GetEmbeddedAppKey();

    public static string ResolveSentryDsn() => TelemetrySecrets.GetEmbeddedDsn();

    public static void InitializeFullLogging()
    {
        if (_isSerilogInitialized) return;

        lock (InitLock)
        {
            if (_isSerilogInitialized) return;

            var startTimestamp = Stopwatch.GetTimestamp();
            try
            {
                var logDir = LogDirectory;
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                const string fileOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{AppLogLevel,-5}] [T{ThreadId:D2}|Task{TaskId}] [{CallerContext}] {Message:lj}{NewLine}{Exception}";

                var config = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .Enrich.FromLogContext()
                    .Enrich.WithThreadId()
                    .Enrich.WithProcessId()
                    .WriteTo.File(
                        CurrentLogFilePath,
                        outputTemplate: fileOutputTemplate,
                        formatProvider: CultureInfo.InvariantCulture,
                        fileSizeLimitBytes: 104857600,
                        flushToDiskInterval: TimeSpan.FromMilliseconds(500),
                        rollOnFileSizeLimit: true
                    )
                    .WriteTo.File(
                        CurrentErrorLogFilePath,
                        restrictedToMinimumLevel: LogEventLevel.Warning,
                        outputTemplate: fileOutputTemplate,
                        formatProvider: CultureInfo.InvariantCulture,
                        fileSizeLimitBytes: 52428800,
                        flushToDiskInterval: TimeSpan.FromMilliseconds(500),
                        rollOnFileSizeLimit: true
                    );

                var dsn = ResolveSentryDsn();
                if (!string.IsNullOrWhiteSpace(dsn))
                {
                    config = config.WriteTo.Sentry(o =>
                    {
                        o.Dsn = dsn;
                        o.InitializeSdk = false;
                        o.MinimumBreadcrumbLevel = LogEventLevel.Verbose;
                        o.MinimumEventLevel = LogEventLevel.Error;
                    });
                }

                Log.Logger = config.CreateLogger();
                _logger = Log.Logger;
                _isSerilogInitialized = true;

                while (EarlyLogBuffer.TryDequeue(out var item))
                {
                    using (LogContext.PushProperty(CallerContextPropertyName, item.CallerContext))
                    using (LogContext.PushProperty("AppLogLevel", item.AppLogLevel))
                    using (LogContext.PushProperty("TaskId", Task.CurrentId?.ToString(CultureInfo.InvariantCulture) ?? "-"))
                    {
                        var formattedMsg = string.IsNullOrEmpty(item.StructuredContext)
                            ? item.Message
                            : $"{item.Message} | Context: {item.StructuredContext}";

                        if (item.Exception != null)
                        {
                            _logger.Write(item.Level, item.Exception, SerilogMessageTemplate, formattedMsg);
                        }
                        else
                        {
                            _logger.Write(item.Level, SerilogMessageTemplate, formattedMsg);
                        }
                    }
                }

                var initElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                LogEnvironmentDiagnostics(initElapsedMs);
                _ = Task.Run(CleanupOldSessionLogs, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppLogger] Full logger initialization notice: {ex.Message}");
            }
        }
    }

    private static void EnqueueBreadcrumb(string crumb)
    {
        if (BreadcrumbsLock is null || Breadcrumbs is null) return;

        lock (BreadcrumbsLock)
        {
            Breadcrumbs.Enqueue(crumb);
            while (Breadcrumbs.Count > MaxBreadcrumbs)
            {
                Breadcrumbs.Dequeue();
            }
        }
    }

    public static void TrackEvent(string eventName, Dictionary<string, object>? props = null)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        if (!AppSettings.IsCrashReportingEnabled())
        {
            return;
        }

        try
        {
            if (AptabaseExtensions.IsInitialized)
            {
                var sanitizedProps = new Dictionary<string, object>
                {
                    ["installation_id"] = InstallationId,
                    ["session_id"] = SessionId,
                    ["memory_mb"] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
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
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                Trace($"[AppLogger:TrackEvent] Dispatched '{eventName}' in {elapsedMs:F2}ms (PropsCount={sanitizedProps.Count}).");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] TrackEvent notice: {ex.Message}");
        }
    }

    public static void TrackError(Exception exception, bool fatal = false)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        if (!AppSettings.IsCrashReportingEnabled())
        {
            return;
        }

        try
        {
            if (AptabaseExtensions.IsInitialized)
            {
                var demystified = exception.Demystify();
                _ = AptabaseExtensions.Instance.TrackError(demystified, fatal);
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                Debug($"[AppLogger:TrackError] Error delivered in {elapsedMs:F2}ms (Type={exception.GetType().Name}, Fatal={fatal}).");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] TrackError notice: {ex.Message}");
        }
    }

    private static void CleanupOldSessionLogs()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            var logDir = LogDirectory;
            if (!Directory.Exists(logDir)) return;

            var dirInfo = new DirectoryInfo(logDir);
            var sessionFiles = dirInfo.GetFiles("reforger_rcon_session_*.log")
                .OrderByDescending(static f => f.CreationTimeUtc)
                .ToList();

            var errorFiles = dirInfo.GetFiles("reforger_rcon_errors_*.log")
                .OrderByDescending(static f => f.CreationTimeUtc)
                .ToList();

            const int maxRetainedSessions = 30;
            var cutoffDate = DateTime.UtcNow.AddDays(-14);
            int deleted = 0;

            foreach (var file in sessionFiles.Skip(maxRetainedSessions).Concat(sessionFiles.Where(f => f.LastWriteTimeUtc < cutoffDate)))
            {
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppLogger] Log file locked: {ex.Message}");
                }
            }

            foreach (var file in errorFiles.Skip(maxRetainedSessions).Concat(errorFiles.Where(f => f.LastWriteTimeUtc < cutoffDate)))
            {
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppLogger] Error log file locked: {ex.Message}");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            Debug($"[AppLogger] Cleaned up {deleted} old session log files in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Log cleanup error: {ex.Message}");
        }
    }

    private static void LogEnvironmentDiagnostics(double initElapsedMs)
    {
        Info("================================================================================");
        Info("APPLICATION INITIALIZATION: ARMA Reforger RCON Management Tool (ARRT)");
        Info(string.Create(CultureInfo.InvariantCulture, $"Startup Latency:     {initElapsedMs:F2} ms"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Timestamp (UTC):     {DateTime.UtcNow:O}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Session ID:          {SessionId}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Hardware Identity:   {InstallationId}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Session Log File:    {CurrentLogFilePath}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Session Error Log:   {CurrentErrorLogFilePath}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"OS Description:      {RuntimeInformation.OSDescription}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"OS Architecture:     {RuntimeInformation.OSArchitecture}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Process Arch:        {RuntimeInformation.ProcessArchitecture}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"CLR Runtime:         {RuntimeInformation.FrameworkDescription}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Process ID:          {Environment.ProcessId}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Persistent Storage:  {AppPaths.AppDataDirectory}"));
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

    private static string FormatStructuredContext(IReadOnlyDictionary<string, object?>? context)
    {
        if (context == null || context.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var (k, v) in context)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(k).Append('=').Append(SanitizeSensitiveData(v?.ToString() ?? "null"));
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void Dispatch(LogLevel level, string message, Exception? ex, IReadOnlyDictionary<string, object?>? context, string member, string path, int line)
    {
        try
        {
            Interlocked.Increment(ref _totalLogsDispatched);
            var cleanMessage = SanitizeSensitiveData(message);
            var demystifiedEx = ex?.Demystify();
            var file = Path.GetFileName(path);
            var threadId = Environment.CurrentManagedThreadId;
            var taskId = Task.CurrentId?.ToString(CultureInfo.InvariantCulture) ?? "-";
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var structuredContextStr = FormatStructuredContext(context);

            var levelString = level switch
            {
                LogLevel.Trace => "TRACE",
                LogLevel.Debug => "DEBUG",
                LogLevel.Info => "INFO ",
                LogLevel.Warn => "WARN ",
                LogLevel.Error => "ERROR",
                LogLevel.Fatal => "FATAL",
                _ => "INFO "
            };

            var callerContext = $"{file}:{line} -> {member}()";
            var formattedContextSuffix = string.IsNullOrEmpty(structuredContextStr) ? string.Empty : $" | Context: {structuredContextStr}";
            var crumb = $"[{timestamp}] [{levelString}] [T{threadId:D2}|Task{taskId}] [{callerContext}] {cleanMessage}{formattedContextSuffix}";

            EnqueueBreadcrumb(crumb);

#if DEBUG
            System.Diagnostics.Debug.WriteLine(crumb);
            if (Debugger.IsAttached)
            {
                Debugger.Log(0, "ARRT", crumb + Environment.NewLine);
            }
#else
            if (level >= LogLevel.Info)
            {
                Console.WriteLine(crumb);
            }
#endif

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

            try
            {
                SentrySdk.AddBreadcrumb(
                    message: cleanMessage,
                    category: member,
                    type: null,
                    data: sentryData,
                    level: sentryBreadcrumbLevel
                );
            }
            catch (Exception crumbEx)
            {
                System.Diagnostics.Debug.WriteLine($"[AppLogger] Breadcrumb notice: {crumbEx.Message}");
            }

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

            if (_logger != null)
            {
                using (LogContext.PushProperty(CallerContextPropertyName, callerContext))
                using (LogContext.PushProperty("AppLogLevel", levelString))
                using (LogContext.PushProperty("TaskId", taskId))
                {
                    var msgToLog = string.IsNullOrEmpty(structuredContextStr)
                        ? cleanMessage
                        : $"{cleanMessage} | Context: {structuredContextStr}";

                    if (demystifiedEx != null)
                    {
                        _logger.Write(serilogLevel, demystifiedEx, SerilogMessageTemplate, msgToLog);

                        if (level is LogLevel.Error or LogLevel.Fatal)
                        {
                            if (AppSettings.IsCrashReportingEnabled() && AptabaseExtensions.IsInitialized)
                            {
                                try
                                {
                                    _ = AptabaseExtensions.Instance.TrackError(demystifiedEx, fatal: level == LogLevel.Fatal);
                                }
                                catch (Exception aptaEx)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[AppLogger] Telemetry notice: {aptaEx.Message}");
                                }
                            }

                            try
                            {
                                SentrySdk.CaptureException(demystifiedEx, scope =>
                                {
                                    scope.SetTag("caller_member", member);
                                    scope.SetTag("caller_file", file);
                                    scope.SetTag("caller_line", line.ToString(CultureInfo.InvariantCulture));
                                    scope.SetTag("installation_id", InstallationId);
                                    scope.SetTag("task_id", taskId);
                                    if (context != null)
                                    {
                                        foreach (var (k, v) in context)
                                        {
                                            scope.SetExtra(k, SanitizeSensitiveData(v?.ToString() ?? "null"));
                                        }
                                    }
                                });
                            }
                            catch (Exception sentryEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[AppLogger] Sentry capture notice: {sentryEx.Message}");
                            }
                        }
                    }
                    else
                    {
                        _logger.Write(serilogLevel, SerilogMessageTemplate, msgToLog);

                        if (level == LogLevel.Fatal)
                        {
                            try
                            {
                                SentrySdk.CaptureMessage(msgToLog, SentryLevel.Fatal);
                            }
                            catch (Exception msgEx)
                            {
                                System.Diagnostics.Debug.WriteLine($"[AppLogger] Sentry message notice: {msgEx.Message}");
                            }
                        }
                    }
                }
            }
            else
            {
                EarlyLogBuffer.Enqueue((serilogLevel, cleanMessage, demystifiedEx, callerContext, levelString, structuredContextStr));
            }
        }
        catch (Exception dispatchEx)
        {
            if (Interlocked.CompareExchange(ref _isInDispatchFailure, 1, 0) == 0)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[AppLogger:DispatchFailure] {dispatchEx.Message}\n{dispatchEx.StackTrace}");
                    CrashReportService.HandleFatalException("AppLogger.DispatchFailure", dispatchEx, isTerminating: false);
                }
                finally
                {
                    Interlocked.Exchange(ref _isInDispatchFailure, 0);
                }
            }
        }
    }

    public static List<string> GetRecentBreadcrumbs()
    {
        if (BreadcrumbsLock is null || Breadcrumbs is null) return [];

        lock (BreadcrumbsLock)
        {
            return [.. Breadcrumbs];
        }
    }

    public static void Flush()
    {
        try
        {
            Log.CloseAndFlush();
            SentrySdk.FlushAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
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
        private readonly int _threadId;
        private bool _isDisposed;

        public TimingScope(string operationName, string member, string path, int line)
        {
            _operationName = operationName;
            _member = member;
            _path = path;
            _line = line;
            _threadId = Environment.CurrentManagedThreadId;
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
                var memoryAllocated = Environment.CurrentManagedThreadId == _threadId
                    ? GC.GetAllocatedBytesForCurrentThread() - _initialMemory
                    : -1;

                var context = new Dictionary<string, object?>
                {
                    ["operation"] = _operationName,
                    ["duration_ms"] = elapsed.TotalMilliseconds,
                    ["allocated_bytes"] = memoryAllocated >= 0 ? memoryAllocated : "N/A"
                };

                if (memoryAllocated >= 0)
                {
                    var memFormatted = memoryAllocated >= 1024 ? $"{memoryAllocated / 1024.0:F1} KB" : $"{memoryAllocated} B";
                    Dispatch(LogLevel.Debug, $"[TIMING:END] {_operationName} took {elapsed.TotalMilliseconds:F2} ms (Allocated: {memFormatted})", null, context, _member, _path, _line);
                }
                else
                {
                    Dispatch(LogLevel.Debug, $"[TIMING:END] {_operationName} took {elapsed.TotalMilliseconds:F2} ms", null, context, _member, _path, _line);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AppLogger] Timing notice: {ex.Message}");
            }
        }
    }
}