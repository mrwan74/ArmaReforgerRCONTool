using System;
using System.Diagnostics;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

public sealed class AptabaseCrashReporter
{
    private readonly IAptabaseClient _client;
    private readonly ILogger<AptabaseCrashReporter>? _logger;
    private readonly AptabaseOptions? _options;
    private static readonly TimeSpan FatalFlushTimeout = TimeSpan.FromSeconds(4);

    public AptabaseCrashReporter(IAptabaseClient client, AptabaseOptions? options, ILogger<AptabaseCrashReporter>? logger)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _options = options;
        _logger = logger;

        RegisterSafetyNets();
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseCrashReporter),
            $"[AptabaseCrashReporter:Init] Safety nets (AppDomain, TaskScheduler, UIThread) registered in {elapsedMs:F2}ms.");
    }

    private void RegisterSafetyNets()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var hookStart = Stopwatch.GetTimestamp();
            if (e.ExceptionObject is Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                    $"[AptabaseCrashReporter:AppDomain] UnhandledException captured (Terminating={e.IsTerminating}, Type={ex.GetType().FullName}, Message='{ex.Message}')", ex);

                TrackError(ex, e.IsTerminating ? "crash" : "unhandled", e.IsTerminating);
            }
            else
            {
                var nonEx = new AptabaseException($"Non-exception object thrown in AppDomain: {e.ExceptionObject}");
                AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                    $"[AptabaseCrashReporter:AppDomain] Non-exception object captured (Terminating={e.IsTerminating})", nonEx);
                TrackError(nonEx, "crash", e.IsTerminating);
            }
            var hookElapsed = Stopwatch.GetElapsedTime(hookStart).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseCrashReporter), $"[AptabaseCrashReporter:AppDomain] Handled in {hookElapsed:F2}ms.");
        };

        TaskScheduler.UnobservedTaskException += (_, ueargs) =>
        {
            var hookStart = Stopwatch.GetTimestamp();
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseCrashReporter),
                $"[AptabaseCrashReporter:TaskScheduler] UnobservedTaskException captured ({ueargs.Exception.InnerExceptions.Count} inner exceptions).", ueargs.Exception);

            foreach (var inner in ueargs.Exception.Flatten().InnerExceptions)
            {
                TrackError(inner, "taskException", fatal: false);
            }

            ueargs.SetObserved();
            var hookElapsed = Stopwatch.GetElapsedTime(hookStart).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseCrashReporter), $"[AptabaseCrashReporter:TaskScheduler] Handled in {hookElapsed:F2}ms.");
        };

        Dispatcher.UIThread.UnhandledExceptionFilter += (_, e) =>
        {
            if (e.Exception is OperationCanceledException or TaskCanceledException)
            {
                AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseCrashReporter),
                    $"[AptabaseCrashReporter:Filter] Filtered expected cancellation: {e.Exception.GetType().Name}");
                e.RequestCatch = false;
            }
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            var hookStart = Stopwatch.GetTimestamp();
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                $"[AptabaseCrashReporter:UIThread] UnhandledException on UI thread: {e.Exception.GetType().FullName}: {e.Exception.Message}", e.Exception);

            TrackError(e.Exception, "uiDispatcherCrash", fatal: false);

            if (_options?.SuppressUIThreadCrashes ?? true)
            {
                e.Handled = true;
                AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseCrashReporter), "[AptabaseCrashReporter:UIThread] Exception marked Handled.");
            }

            var hookElapsed = Stopwatch.GetElapsedTime(hookStart).TotalMilliseconds;
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseCrashReporter), $"[AptabaseCrashReporter:UIThread] Handled in {hookElapsed:F2}ms.");
        };
    }

    private void TrackError(Exception e, string kind, bool fatal = false)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AptabaseLogging.Log(_logger, LogLevel.Debug, nameof(AptabaseCrashReporter), $"[AptabaseCrashReporter:Dispatch] Dispatching '{kind}' report (Fatal={fatal})...");
            var sendTask = _client is IErrorTracker tracker
                ? tracker.TrackError(e, fatal, kind)
                : _client.TrackError(e, fatal);

            if (fatal)
            {
                try
                {
                    sendTask.Wait(FatalFlushTimeout);
                    var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
                    AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseCrashReporter), $"[AptabaseCrashReporter:Dispatch] Fatal error flushed before exit in {elapsedMs:F2}ms.");
                }
                catch (Exception ex)
                {
                    AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseCrashReporter),
                        $"[AptabaseCrashReporter:Dispatch] Could not flush crash report: {ex.Message}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                $"[AptabaseCrashReporter:Dispatch] Failed dispatching report: {ex.Message}", ex);
        }
    }
}