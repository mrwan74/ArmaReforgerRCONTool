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
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "appdata", "logs");
    private static readonly Queue<string> Breadcrumbs = new();
    private static readonly Lock BreadcrumbsLock = new();
    private const int MaxBreadcrumbs = 2000;

    private static readonly ConcurrentQueue<(LogEventLevel Level, string Message, Exception? Exception, string CallerContext)> EarlyLogBuffer = new();
    private static Serilog.ILogger? _serilogLogger;
    private static int _isInDispatchFailure;
    private static long _totalLogsDispatched;
    private static long _totalSanitizationsExecuted;
    private static bool _isSerilogInitialized;
    private static readonly Lock InitLock = new();

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

    public static string CurrentLogFilePath { get; } = Path.Combine(LogDirectory, $"reforger_rcon_session_{SessionId}.log");
    public static string CurrentErrorLogFilePath { get; } = Path.Combine(LogDirectory, $"reforger_rcon_errors_{SessionId}.log");

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

        if (!input.Contains("password", StringComparison.OrdinalIgnoreCase) &&
            !input.Contains("pwd", StringComparison.OrdinalIgnoreCase) &&
            !input.Contains("login", StringComparison.OrdinalIgnoreCase) &&
            !input.Contains("Basic", StringComparison.OrdinalIgnoreCase) &&
            !input.Contains("LicenseKey", StringComparison.OrdinalIgnoreCase))
        {
            return input;
        }

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

    public static string ResolveAptabaseAppKey() => TelemetrySecrets.GetEmbeddedAppKey();

    public static string ResolveSentryDsn() => TelemetrySecrets.GetEmbeddedDsn();

    public static void InitializeFullLoggingBackground()
    {
        if (_isSerilogInitialized) return;

        _ = Task.Run(() =>
        {
            lock (InitLock)
            {
                if (_isSerilogInitialized) return;

                var startTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    if (!Directory.Exists(LogDirectory))
                    {
                        Directory.CreateDirectory(LogDirectory);
                    }

                    const string fileOutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [T{ThreadId:D2}] [{CallerContext}] {Message:lj}{NewLine}{Exception}";

                    var config = new LoggerConfiguration()
                        .MinimumLevel.Verbose()
                        .Enrich.FromLogContext()
                        .Enrich.WithThreadId()
                        .Enrich.WithProcessId()
                        .WriteTo.Async(a => a.File(
                            CurrentLogFilePath,
                            outputTemplate: fileOutputTemplate,
                            formatProvider: CultureInfo.InvariantCulture,
                            fileSizeLimitBytes: 104857600,
                            rollOnFileSizeLimit: true
                        ))
                        .WriteTo.Async(a => a.File(
                            CurrentErrorLogFilePath,
                            restrictedToMinimumLevel: LogEventLevel.Warning,
                            outputTemplate: fileOutputTemplate,
                            formatProvider: CultureInfo.InvariantCulture,
                            fileSizeLimitBytes: 52428800,
                            rollOnFileSizeLimit: true
                        ));

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
                    _serilogLogger = Log.Logger;
                    _isSerilogInitialized = true;

                    while (EarlyLogBuffer.TryDequeue(out var item))
                    {
                        using (LogContext.PushProperty(CallerContextPropertyName, item.CallerContext))
                        {
                            if (item.Exception != null)
                            {
                                _serilogLogger.Write(item.Level, item.Exception, SerilogMessageTemplate, item.Message);
                            }
                            else
                            {
                                _serilogLogger.Write(item.Level, SerilogMessageTemplate, item.Message);
                            }
                        }
                    }

                    var initElapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    LogEnvironmentDiagnostics(initElapsedMs);
                    CleanupOldSessionLogs();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[AppLogger] Full logger initialization notice: {ex.Message}");
                }
            }
        });
    }

    private static void EnqueueBreadcrumb(string crumb)
    {
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
                var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                Debug($"[AppLogger:TrackEvent] Dispatched '{eventName}' in {elapsedMs:F2}ms (PropsCount={sanitizedProps.Count}).");
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
            if (!Directory.Exists(LogDirectory)) return;

            var logDir = new DirectoryInfo(LogDirectory);
            var sessionFiles = logDir.GetFiles("reforger_rcon_session_*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            var errorFiles = logDir.GetFiles("reforger_rcon_errors_*.log")
                .OrderByDescending(f => f.CreationTimeUtc)
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
                catch
                {
                    // Ignore locked files
                }
            }

            foreach (var file in errorFiles.Skip(maxRetainedSessions).Concat(errorFiles.Where(f => f.LastWriteTimeUtc < cutoffDate)))
            {
                try
                {
                    file.Delete();
                    deleted++;
                }
                catch
                {
                    // Ignore locked files
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Cleaned up {deleted} old session log files in {elapsedMs:F2}ms.");
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
        Info(string.Create(CultureInfo.InvariantCulture, $"Hardware Identity:   {InstallationId}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Session Log File:    {CurrentLogFilePath}"));
        Info(string.Create(CultureInfo.InvariantCulture, $"Session Error Log:   {CurrentErrorLogFilePath}"));
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

    public static TimingScope Measure(string operationName, double slowThresholdMs = 25.0, [CallerMemberName] string member = "", [CallerFilePath] string path = "", [CallerLineNumber] int line = 0)
    {
        return new TimingScope(operationName, slowThresholdMs, member, path, line);
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
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

            var callerContext = $"{file}:{line} -> {member}()";
            var crumb = $"[{timestamp}] [{level,-5}] [T{threadId:D2}] [{callerContext}] {cleanMessage}";

            EnqueueBreadcrumb(crumb);
            System.Diagnostics.Debug.WriteLine(crumb);

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
            catch
            {
                // Suppress Sentry breadcrumb dispatch errors
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

            if (_serilogLogger != null)
            {
                using (LogContext.PushProperty(CallerContextPropertyName, callerContext))
                {
                    if (demystifiedEx != null)
                    {
                        _serilogLogger.Write(serilogLevel, demystifiedEx, SerilogMessageTemplate, cleanMessage);

                        if (level is LogLevel.Error or LogLevel.Fatal)
                        {
                            if (AppSettings.IsCrashReportingEnabled() && AptabaseExtensions.IsInitialized)
                            {
                                try
                                {
                                    _ = AptabaseExtensions.Instance.TrackError(demystifiedEx, fatal: level == LogLevel.Fatal);
                                }
                                catch
                                {
                                    // Suppress secondary telemetry exceptions
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
                                });
                            }
                            catch
                            {
                                // Suppress Sentry capture exceptions
                            }
                        }
                    }
                    else
                    {
                        _serilogLogger.Write(serilogLevel, SerilogMessageTemplate, cleanMessage);

                        if (level == LogLevel.Fatal)
                        {
                            try
                            {
                                SentrySdk.CaptureMessage(cleanMessage, SentryLevel.Fatal);
                            }
                            catch
                            {
                                // Suppress Sentry message exceptions
                            }
                        }
                    }
                }
            }
            else
            {
                EarlyLogBuffer.Enqueue((serilogLevel, cleanMessage, demystifiedEx, callerContext));
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
        catch
        {
            // Suppress flush exceptions
        }
    }

    public static void Shutdown()
    {
        try
        {
            Log.CloseAndFlush();
            SentrySdk.Close();
        }
        catch
        {
            // Suppress shutdown exceptions
        }
    }

    public sealed class TimingScope : IDisposable
    {
        private readonly string _operationName;
        private readonly double _slowThresholdMs;
        private readonly string _member;
        private readonly string _path;
        private readonly int _line;
        private readonly long _startTimestamp;
        private readonly long _initialMemory;
        private bool _isDisposed;

        public TimingScope(string operationName, double slowThresholdMs, string member, string path, int line)
        {
            _operationName = operationName;
            _slowThresholdMs = slowThresholdMs;
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

                var level = elapsed.TotalMilliseconds >= _slowThresholdMs ? LogLevel.Warn : LogLevel.Debug;
                var prefix = elapsed.TotalMilliseconds >= _slowThresholdMs ? "[TIMING:SLOW]" : "[TIMING:COMPLETED]";

                Dispatch(level, $"{prefix} {_operationName} took {elapsed.TotalMilliseconds:F2} ms (Allocated: {memFormatted}, Threshold: {_slowThresholdMs:F0}ms)", null, null, _member, _path, _line);
            }
            catch
            {
                // Suppress timing scope disposal faults
            }
        }
    }
}