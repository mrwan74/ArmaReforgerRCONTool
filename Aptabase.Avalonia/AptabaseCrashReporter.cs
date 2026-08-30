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
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _options = options;
        _logger = logger;

        RegisterSafetyNets();
        AptabaseLogging.Log(_logger, LogLevel.Information, nameof(AptabaseCrashReporter), "Aptabase global safety nets (AppDomain, TaskScheduler, Dispatcher.UIThread) registered.");
    }

    private void RegisterSafetyNets()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                    $"[SAFETY NET] AppDomain UnhandledException captured. Terminating: {e.IsTerminating}, Type: {ex.GetType().FullName}, Message: {ex.Message}", ex);

                TrackError(ex, e.IsTerminating ? "crash" : "unhandled", e.IsTerminating);
            }
            else
            {
                var nonEx = new AptabaseException($"Non-exception object thrown in AppDomain: {e.ExceptionObject}");
                AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                    $"[SAFETY NET] AppDomain non-exception object captured. Terminating: {e.IsTerminating}", nonEx);
                TrackError(nonEx, "crash", e.IsTerminating);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, ueargs) =>
        {
            AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseCrashReporter),
                $"[SAFETY NET] TaskScheduler.UnobservedTaskException captured with {ueargs.Exception.InnerExceptions.Count} inner exception(s).", ueargs.Exception);

            foreach (var inner in ueargs.Exception.Flatten().InnerExceptions)
            {
                TrackError(inner, "taskException", fatal: false);
            }

            ueargs.SetObserved();
        };

        Dispatcher.UIThread.UnhandledExceptionFilter += (_, e) =>
        {
            if (e.Exception is OperationCanceledException or TaskCanceledException)
            {
                AptabaseLogging.Log(_logger, LogLevel.Trace, nameof(AptabaseCrashReporter),
                    $"[Dispatcher.UnhandledExceptionFilter] Filtered expected cancellation: {e.Exception.GetType().Name}");
                e.RequestCatch = false;
            }
        };

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                $"[SAFETY NET] Avalonia Dispatcher.UIThread UnhandledException captured: {e.Exception.GetType().FullName}: {e.Exception.Message}", e.Exception);

            TrackError(e.Exception, "uiDispatcherCrash", fatal: false);

            if (_options?.SuppressUIThreadCrashes ?? true)
            {
                e.Handled = true;
            }
        };
    }

    private void TrackError(Exception e, string kind, bool fatal = false)
    {
        try
        {
            var sendTask = _client is IErrorTracker tracker
                ? tracker.TrackError(e, fatal, kind)
                : _client.TrackError(e, fatal);

            if (fatal)
            {
                try
                {
                    sendTask.Wait(FatalFlushTimeout);
                }
                catch (Exception ex)
                {
                    AptabaseLogging.Log(_logger, LogLevel.Error, nameof(AptabaseCrashReporter),
                        $"Could not flush crash report before process exit: {ex.Message}", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AptabaseLogging.Log(_logger, LogLevel.Critical, nameof(AptabaseCrashReporter),
                $"Failed to dispatch fatal error report to telemetry pipeline: {ex.Message}", ex);
        }
    }
}