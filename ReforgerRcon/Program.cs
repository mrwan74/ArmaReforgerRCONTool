using Aptabase.Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Labs.Notifications;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Threading;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using Sentry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TimeZoneConverter;
using Velopack;
using Velopack.Logging;

namespace ReforgerRcon;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Avalonia internal avares resource schema paths")]
internal static partial class Program
{
    private const uint MbIconWarning = 0x00000030;
    private const uint MbIconError = 0x00000010;
    private const string ForbiddenSocketAccessLiteral = "forbidden by its access permissions";

    private static Mutex? _directoryMutex;
    private static FileStream? _directoryLockStream;
    private static IDisposable? _sentrySdk;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    public static int Main(string[] args)
    {
        var bootTimestamp = Stopwatch.GetTimestamp();

        // 1. VELOPACK HOOK INTERCEPTOR
        try
        {
            VelopackApp.Build()
                .SetLogger(VelopackLoggerBridge.Instance)
                .SetAutoApplyOnStartup(false)
                .OnFirstRun(v => System.Diagnostics.Trace.TraceInformation($"[Velopack] First launch detected for installed version: {v}"))
                .OnRestarted(v => System.Diagnostics.Trace.TraceInformation($"[Velopack] Application restarted successfully following update to version: {v}"))
                .Run();
        }
        catch (Exception veloEx)
        {
            System.Diagnostics.Trace.TraceError($"[CRITICAL] Velopack hook interceptor threw an exception: {veloEx}");
        }

        // 2. UNHANDLED EXCEPTION SAFETY NETS
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            var ex = e.ExceptionObject as Exception ?? new InvalidOperationException($"Non-exception domain object: {e.ExceptionObject}");
            var context = new Dictionary<string, object?>
            {
                ["is_terminating"] = e.IsTerminating,
                ["thread_id"] = Environment.CurrentManagedThreadId,
                ["ram_mb"] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
            };
            AppLogger.Fatal($"[Program:AppDomain] Unhandled domain exception captured (Terminating: {e.IsTerminating})", ex, context);
            CrashReportService.HandleFatalException("AppDomain.UnhandledException", ex, e.IsTerminating);
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            var context = new Dictionary<string, object?>
            {
                ["thread_id"] = Environment.CurrentManagedThreadId,
                ["inner_count"] = e.Exception.InnerExceptions.Count
            };
            AppLogger.Error("[Program:TaskScheduler] Unobserved task exception captured on finalizer thread", e.Exception, context);
            CrashReportService.HandleFatalException("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
            e.SetObserved();
        };

        // 3. SINGLE INSTANCE DIRECTORY MUTEX LOCK
        var lockStart = Stopwatch.GetTimestamp();
        if (!TryAcquireDirectoryLock(out var instanceLockHandle))
        {
            var runningDir = AppContext.BaseDirectory;
            var lockElapsedMs = Stopwatch.GetElapsedTime(lockStart).TotalMilliseconds;
            var alertMessage = $"Another instance of ARMA Reforger RCON is already running from this directory:\n\n{runningDir}\n\nOnly one instance per directory is allowed. To run multiple instances simultaneously, place the application in a separate folder.";

            try
            {
                var aptabaseKey = AppLogger.ResolveAptabaseAppKey();
                if (!string.IsNullOrWhiteSpace(aptabaseKey) && AppSettings.IsCrashReportingEnabled())
                {
                    var ephemeralClient = new AptabaseClient(aptabaseKey, null, null);
                    try
                    {
                        ephemeralClient.TrackEvent("double_launch_blocked", new Dictionary<string, object>
                        {
                            ["running_dir_hash"] = runningDir.GetHashCode().ToString("X8", CultureInfo.InvariantCulture),
                            ["os_platform"] = RuntimeInformation.OSDescription,
                            ["lock_check_ms"] = lockElapsedMs
                        }).Wait(TimeSpan.FromSeconds(1.5));
                    }
                    finally
                    {
                        ephemeralClient.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(1));
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Program:DoubleLaunchCheck] Telemetry dispatch notice: {ex.Message}");
            }

            if (OperatingSystem.IsWindows())
            {
                MessageBox(IntPtr.Zero, alertMessage, "ARMA Reforger RCON - Instance Already Running", MbIconWarning);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine(alertMessage);
                Console.ResetColor();
            }
            return 1;
        }

        using (instanceLockHandle)
        {
            try
            {
                AppLogger.InitializeFullLogging();
                CrashReportService.Initialize();

                // Pre-warm non-Avalonia engines (SQLite, GeoIP MMDB readers, Hardware identity) immediately
                StartDeferredBackgroundServices();

                AptabaseLogging.OnLogMessage += entry =>
                {
                    if (entry.Exception is AptabaseException or AptabaseTransmissionException ||
                        (entry.Exception is HttpRequestException httpEx && (httpEx.Message.Contains("aptabase", StringComparison.OrdinalIgnoreCase) || httpEx.Message.Contains(ForbiddenSocketAccessLiteral, StringComparison.OrdinalIgnoreCase))) ||
                        (entry.Exception is SocketException sockEx && (sockEx.Message.Contains("aptabase", StringComparison.OrdinalIgnoreCase) || sockEx.Message.Contains(ForbiddenSocketAccessLiteral, StringComparison.OrdinalIgnoreCase))) ||
                        entry.Message.Contains("aptabase.com", StringComparison.OrdinalIgnoreCase) ||
                        entry.Message.Contains(ForbiddenSocketAccessLiteral, StringComparison.OrdinalIgnoreCase) ||
                        entry.Category.Contains("Aptabase", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var context = new Dictionary<string, object?>
                    {
                        ["category"] = entry.Category,
                        ["thread_id"] = entry.ThreadId
                    };

                    if (entry.Level >= Microsoft.Extensions.Logging.LogLevel.Error)
                    {
                        AppLogger.Error($"[{entry.Category}] {entry.Message}", entry.Exception, context);
                    }
                    else if (entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
                    {
                        AppLogger.Warn($"[{entry.Category}] {entry.Message}", entry.Exception, context);
                    }
                };

                var bootElapsedMs = Stopwatch.GetElapsedTime(bootTimestamp).TotalMilliseconds;
                AppLogger.Info($"[Program:Main] Launching Avalonia application with ClassicDesktopStyle lifetime (PreBootTime={bootElapsedMs:F2}ms)...");

                var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

                AppLogger.Info($"[Program:Main] Application exited normally with return code {exitCode}.");
                return exitCode;
            }
            catch (Exception fatalAppEx)
            {
                AppLogger.Fatal("[Program:Main] Application terminated unexpectedly due to an uncaught top-level exception.", fatalAppEx);
                CrashReportService.HandleFatalException("Program.Main", fatalAppEx, isTerminating: true);

                if (OperatingSystem.IsWindows())
                {
                    MessageBox(IntPtr.Zero,
                        $"The application terminated unexpectedly:\n\n{fatalAppEx.GetType().Name}: {fatalAppEx.Message}\n\nA detailed diagnostic crash report has been generated in appdata/crash_reports/.",
                        "ARMA Reforger RCON - Critical Fault",
                        MbIconError);
                }

                return 1;
            }
            finally
            {
                var teardownStart = Stopwatch.GetTimestamp();
                AppLogger.Info("[Program:Main] Executing application teardown and resource flushing...");

                if (AptabaseExtensions.IsInitialized)
                {
                    try
                    {
                        AptabaseExtensions.Instance.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Trace($"[Program:Shutdown] Aptabase disposal notice: {ex.Message}");
                    }
                }

                GeoIpService.Shutdown();

                try
                {
                    _sentrySdk?.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[Program:Shutdown] Sentry disposal notice: {ex.Message}");
                }

                AppLogger.Flush();
                AppLogger.Shutdown();
                var teardownMs = Stopwatch.GetElapsedTime(teardownStart).TotalMilliseconds;
                System.Diagnostics.Trace.TraceInformation($"[Program:Main] Teardown complete in {teardownMs:F2}ms.");
            }
        }
    }

    public static void StartDeferredBackgroundServices()
    {
        _ = Task.Run(async () =>
        {
            var bgStart = Stopwatch.GetTimestamp();
            AppLogger.Debug("[Program:Background] Commencing background services parallel pre-warming...");

            try
            {
                var sqliteTask = Task.Run(async () =>
                {
                    SQLitePCL.Batteries_V2.Init();
                    await PlayerDatabaseStorageService.InitializeAsync().ConfigureAwait(false);
                });

                var geoIpTask = Task.Run(() =>
                {
                    _ = TZConvert.TryGetTimeZoneInfo("UTC", out _);
                    GeoIpService.PrewarmReaders();
                });

                var identityTask = Task.Run(() =>
                {
                    ColumnLayoutStorageService.Prewarm();
                    _ = HardwareIdentityService.GetOrCreateHardwareId();
                });

                await Task.WhenAll(sqliteTask, geoIpTask, identityTask).ConfigureAwait(false);

                // Start persistent background updater loop
                UpdateService.Instance.StartBackgroundLoop();

                var bgElapsedMs = Stopwatch.GetElapsedTime(bgStart).TotalMilliseconds;
                AppLogger.Info($"[Program:Background] Core background worker services initialized in {bgElapsedMs:F2}ms.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[Program:Background] Pre-warm service initialization notice: {ex.Message}", ex);
            }

            if (AppSettings.IsCrashReportingEnabled())
            {
                InitSentrySdkDeferred();
            }
        }, CancellationToken.None);
    }

    private static void InitSentrySdkDeferred()
    {
        if (!AppSettings.IsCrashReportingEnabled()) return;

        var dsn = AppLogger.ResolveSentryDsn();
        if (string.IsNullOrWhiteSpace(dsn)) return;

        var start = Stopwatch.GetTimestamp();
        try
        {
            _sentrySdk = SentrySdk.Init(options =>
            {
                options.Dsn = dsn;
                options.Debug = false;
                options.AutoSessionTracking = true;
                options.TracesSampleRate = 0.2;
                options.EnableLogs = true;
                options.AttachStacktrace = true;
                options.SendDefaultPii = false;
                options.Environment = "production";
                options.Release = "ReforgerRcon@0.9.0-alpha.5";

                options.SetBeforeSend((sentryEvent, _) => AppSettings.IsCrashReportingEnabled() ? sentryEvent : null);
                options.SetBeforeSendTransaction((tx, _) => AppSettings.IsCrashReportingEnabled() ? tx : null);
            });

            SentrySdk.ConfigureScope(scope =>
            {
                scope.User = new SentryUser { Id = AppLogger.InstallationId };
                scope.SetTag("installation_id", AppLogger.InstallationId);
            });

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[Program:Sentry] Sentry telemetry engine initialized successfully in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Program:Sentry] Deferred initialization notice: {ex.Message}", ex);
            _sentrySdk = null;
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is OperationCanceledException
            or TaskCanceledException
            or SocketException
            or IOException
            or ObjectDisposedException
            or UriFormatException)
        {
            return;
        }

        if (e.Exception is ArgumentException argEx && argEx.Message.Contains("releases.", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (e.Exception is AptabaseException or AptabaseTransmissionException ||
            (e.Exception is HttpRequestException httpEx && (httpEx.Message.Contains("aptabase.com", StringComparison.OrdinalIgnoreCase) || httpEx.Message.Contains("sentry.io", StringComparison.OrdinalIgnoreCase))) ||
            (e.Exception is SocketException sockEx && (sockEx.Message.Contains("aptabase.com", StringComparison.OrdinalIgnoreCase) || sockEx.Message.Contains("sentry.io", StringComparison.OrdinalIgnoreCase))) ||
            e.Exception.Message.Contains(ForbiddenSocketAccessLiteral, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppLogger.Trace($"[FirstChanceException] {e.Exception.GetType().FullName}: {e.Exception.Message}");
    }

    private static bool TryAcquireDirectoryLock(out IDisposable lockHandle)
    {
        lockHandle = null!;
        var start = Stopwatch.GetTimestamp();
        try
        {
            var baseDir = AppPaths.AppDataDirectory;
            var mutexName = $"Local\\ReforgerRcon_DirLock_{baseDir.GetHashCode():X8}";
            _directoryMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew) return false;

            if (!Directory.Exists(baseDir)) Directory.CreateDirectory(baseDir);

            var lockFilePath = Path.Combine(baseDir, "process.lock");
            _directoryLockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            lockHandle = new DirectoryLockDisposable(_directoryMutex, _directoryLockStream);
            return true;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            System.Diagnostics.Trace.TraceWarning($"[Program:Lock] Directory lock acquisition notice after {elapsedMs:F2}ms: {ex.Message}");
            _directoryLockStream?.Dispose();
            _directoryLockStream = null;
            _directoryMutex?.Dispose();
            _directoryMutex = null;
            return false;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new SkiaOptions
            {
                MaxGpuResourceSizeBytes = 256 * 1024 * 1024
            });

        try
        {
            builder.WithAppNotifications(new AppNotificationOptions
            {
                AppName = "ARMA Reforger RCON Tool (ARRT)",
                ClearOnAppClose = false,
                Channels =
                [
                    new NotificationChannel("default", "General Notifications", NotificationPriority.Default),
                    new NotificationChannel("players", "Player Join & Leave Alerts", NotificationPriority.High),
                    new NotificationChannel("watchlist", "Watchlist Alerts", NotificationPriority.Max),
                    new NotificationChannel("system", "System and Moderation Alerts", NotificationPriority.High)
                ]
            });
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[Program:Builder] Notifications registration notice: {ex.Message}");
        }

        var aptabaseKey = AppLogger.ResolveAptabaseAppKey();
        if (!string.IsNullOrWhiteSpace(aptabaseKey))
        {
            try
            {
                var storagePath = Path.Combine(AppPaths.AppDataDirectory, "analytics");

                builder.UseAptabase(aptabaseKey, new AptabaseOptions
                {
                    EnablePersistence = true,
                    EnableCrashReporting = false,
                    CaptureAvaloniaFrameworkLogs = false,
                    StoragePath = storagePath,
                    SuppressUIThreadCrashes = true,
                    ConsentCheck = AppSettings.IsCrashReportingEnabled,
                    ContextInjector = () =>
                    {
                        var settings = AppSettings.LoadFromDisk();
                        return new Dictionary<string, object>
                        {
                            ["installation_id"] = AppLogger.InstallationId,
                            ["theme_mode"] = settings.ThemeMode
                        };
                    }
                });
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[Program:Builder] Aptabase registration notice: {ex.Message}");
            }
        }

        Logger.Sink = new AvaloniaLogSink(LogEventLevel.Warning);
        return builder;
    }

    private sealed class DirectoryLockDisposable(Mutex mutex, FileStream lockStream) : IDisposable
    {
        private readonly Mutex _mutex = mutex;
        private readonly FileStream _lockStream = lockStream;
        private bool _isDisposed;

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _lockStream.Dispose();
            }
            catch (IOException ex)
            {
                AppLogger.Trace($"[Program:Lock] Lock stream disposal notice: {ex.Message}");
            }

            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException ex)
            {
                AppLogger.Trace($"[Program:Lock] Mutex release notice: {ex.Message}");
            }

            try
            {
                _mutex.Dispose();
            }
            catch (ObjectDisposedException ex)
            {
                AppLogger.Trace($"[Program:Lock] Mutex disposal notice: {ex.Message}");
            }
        }
    }
}