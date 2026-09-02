using Aptabase.Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Labs.Notifications;
using Avalonia.Logging;
using Avalonia.Platform;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using Sentry;
using Svg.Skia;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TimeZoneConverter;

namespace ReforgerRcon;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Avalonia internal avares resource schema paths")]
internal static partial class Program
{
    private const uint MbIconWarning = 0x00000030;
    private const string AppDataDirectoryName = "appdata";
    private static Mutex? _directoryMutex;
    private static FileStream? _directoryLockStream;
    private static IDisposable? _sentrySdk;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;

        if (!TryAcquireDirectoryLock(out var instanceLockHandle))
        {
            var runningDir = AppContext.BaseDirectory;
            var alertMessage = $"Another instance of ARMA Reforger RCON is already running from this directory:\n\n{runningDir}\n\nOnly one instance per directory is allowed. To run multiple instances simultaneously, place the application in a separate folder.";

            if (OperatingSystem.IsWindows())
            {
                MessageBox(IntPtr.Zero, alertMessage, "ARMA Reforger RCON - Instance Already Running", MbIconWarning);
            }
            return;
        }

        using (instanceLockHandle)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception ?? new InvalidOperationException($"Non-exception domain object: {e.ExceptionObject}");
                CrashReportService.HandleFatalException("AppDomain.UnhandledException", ex, e.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                CrashReportService.HandleFatalException("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
                e.SetObserved();
            };

            CrashReportService.Initialize();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

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
            _sentrySdk?.Dispose();
            AppLogger.Shutdown();
        }
    }

    public static void StartDeferredBackgroundServices()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000).ConfigureAwait(false);
            AppLogger.InitializeFullLoggingBackground();

            if (AppSettings.IsCrashReportingEnabled())
            {
                InitSentrySdkDeferred();
            }

            // Warm up SQLite, TimeZone database, GeoIP, PushNotifications, and SVG rasterization engine
            SQLitePCL.Batteries_V2.Init();
            _ = PlayerDatabaseStorageService.InitializeAsync();
            _ = TZConvert.TryGetTimeZoneInfo("UTC", out _);
            GeoIpService.Initialize();
            PushNotificationService.Initialize();

            try
            {
                using var dummySvg = new SKSvg();
                await using var dummyStream = new MemoryStream("<svg width='1' height='1'></svg>"u8.ToArray());
                _ = dummySvg.Load(dummyStream);
            }
            catch
            {
                // Suppress SVG engine pre-warm notice
            }
        }, CancellationToken.None);
    }

    private static void InitSentrySdkDeferred()
    {
        if (!AppSettings.IsCrashReportingEnabled()) return;

        var dsn = AppLogger.ResolveSentryDsn();
        if (string.IsNullOrWhiteSpace(dsn)) return;

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
                options.Release = "ReforgerRcon@0.8.64";

                options.SetBeforeSend((sentryEvent, _) => AppSettings.IsCrashReportingEnabled() ? sentryEvent : null);
                options.SetBeforeSendTransaction((tx, _) => AppSettings.IsCrashReportingEnabled() ? tx : null);
            });

            SentrySdk.ConfigureScope(scope =>
            {
                scope.User = new SentryUser { Id = AppLogger.InstallationId };
                scope.SetTag("installation_id", AppLogger.InstallationId);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Program:Sentry] Deferred initialization notice: {ex.Message}", ex);
            _sentrySdk = null;
        }
    }

    private static void OnFirstChanceException(object? sender, FirstChanceExceptionEventArgs e)
    {
        if (e.Exception is OperationCanceledException or TaskCanceledException) return;
        AppLogger.Trace($"[FirstChanceException] {e.Exception.GetType().FullName}: {e.Exception.Message}");
    }

    private static bool TryAcquireDirectoryLock(out IDisposable lockHandle)
    {
        lockHandle = null!;
        try
        {
            var baseDir = AppContext.BaseDirectory;
            var mutexName = $"Local\\ReforgerRcon_DirLock_{baseDir.GetHashCode():X8}";
            _directoryMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew) return false;

            var appDataDir = Path.Combine(baseDir, AppDataDirectoryName);
            if (!Directory.Exists(appDataDir)) Directory.CreateDirectory(appDataDir);

            var lockFilePath = Path.Combine(appDataDir, "process.lock");
            _directoryLockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            lockHandle = new DirectoryLockDisposable(_directoryMutex, _directoryLockStream);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[Program:Lock] Directory lock acquisition notice: {ex.Message}");
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
            .WithInterFont();

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
                var storagePath = Path.Combine(AppContext.BaseDirectory, AppDataDirectoryName, "analytics");

                builder.UseAptabase(aptabaseKey, new AptabaseOptions
                {
                    EnablePersistence = true,
                    EnableCrashReporting = true,
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