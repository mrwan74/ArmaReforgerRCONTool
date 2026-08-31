using Aptabase.Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Labs.Notifications;
using Avalonia.Logging;
using Avalonia.Platform;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using Sentry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TimeZoneConverter;

namespace ReforgerRcon;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Avalonia internal avares resource schema paths")]
internal static partial class Program
{
    private const uint MbIconError = 0x00000010;
    private const uint MbIconWarning = 0x00000030;
    private const string LogSeparatorLine = "================================================================================";
    private const string AppDataDirectoryName = "appdata";
    private const string AppIconFileName = "app.ico";
    private const string AppIconResourceUri = "avares://ReforgerRcon/Assets/app.ico";

    private static Mutex? _directoryMutex;
    private static FileStream? _directoryLockStream;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    public static void Main(string[] args)
    {
        _ = Task.Run(() =>
        {
            try
            {
                SQLitePCL.Batteries_V2.Init();
                _ = PlayerDatabaseStorageService.InitializeAsync();
                _ = TZConvert.TryGetTimeZoneInfo("UTC", out _);
                AppLogger.Debug("[Program] Asynchronous subsystem warm-up completed.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[Program] Background warmup encountered a non-fatal error: {ex.Message}", ex);
            }
        }, CancellationToken.None);

        if (!TryAcquireDirectoryLock(out var instanceLockHandle))
        {
            var runningDir = AppContext.BaseDirectory;
            var alertMessage = $"Another instance of ARMA Reforger RCON is already running from this directory:\n\n{runningDir}\n\nOnly one instance per directory is allowed. To run multiple instances simultaneously, place the application in a separate folder.";

            AppLogger.Warn($"[Program] Instance collision detected for directory: {runningDir}");

            if (OperatingSystem.IsWindows())
            {
                MessageBox(IntPtr.Zero, alertMessage, "ARMA Reforger RCON - Instance Already Running", MbIconWarning);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine(LogSeparatorLine);
                Console.Error.WriteLine("INSTANCE ALREADY RUNNING FOR THIS DIRECTORY");
                Console.Error.WriteLine($"Directory: {runningDir}");
                Console.Error.WriteLine("To run concurrent instances, execute from distinct directory paths.");
                Console.Error.WriteLine(LogSeparatorLine);
                Console.ResetColor();
            }

            return;
        }

        using (instanceLockHandle)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception ?? new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Non-exception domain object: {e.ExceptionObject}"));
                AppLogger.Fatal($"[AppDomain.UnhandledException] Terminating={e.IsTerminating}: {ex.Message}", ex);
                HandleEmergencyStartupCrash("AppDomain.UnhandledException", ex, e.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.Error("[TaskScheduler.UnobservedTaskException] Unobserved background task exception captured on finalizer thread.", e.Exception);
                HandleEmergencyStartupCrash("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
                e.SetObserved();
            };

            var dsn = AppLogger.ResolveSentryDsn();
            IDisposable? sentrySdk = null;

            if (!string.IsNullOrWhiteSpace(dsn))
            {
                try
                {
                    sentrySdk = SentrySdk.Init(options =>
                    {
                        options.Dsn = dsn;
                        options.Debug = false;
                        options.AutoSessionTracking = true;
                        options.TracesSampleRate = 0.2;
                        options.EnableLogs = true;
                        options.AttachStacktrace = true;
                        options.SendDefaultPii = false;
                        options.Environment = "production";
                        options.Release = "ReforgerRcon@0.8.60";

                        options.SetBeforeSend((sentryEvent, _) =>
                        {
                            if (!AppSettings.IsCrashReportingEnabled())
                            {
                                return null;
                            }

                            if (sentryEvent.Message?.Formatted != null)
                            {
                                sentryEvent.Message = AppLogger.SanitizeSensitiveData(sentryEvent.Message.Formatted);
                            }

                            return sentryEvent;
                        });

                        options.SetBeforeSendTransaction((transaction, _) =>
                        {
                            if (!AppSettings.IsCrashReportingEnabled())
                            {
                                return null;
                            }

                            return transaction;
                        });
                    });

                    SentrySdk.ConfigureScope(scope =>
                    {
                        scope.User = new SentryUser
                        {
                            Id = AppLogger.InstallationId
                        };
                        scope.SetTag("installation_id", AppLogger.InstallationId);
                    });

                    AppLogger.Info($"[Program] Sentry SDK initialized for installation {AppLogger.InstallationId} (Live Telemetry Allowed: {AppSettings.IsCrashReportingEnabled()}).");
                }
                catch (Exception ex)
                {
                    AppLogger.Warn($"[Program] Sentry SDK initialization skipped: {ex.Message}", ex);
                    sentrySdk = null;
                }
            }

            try
            {
                using (sentrySdk)
                {
                    CrashReportService.Initialize();
                    AppLogger.Info(string.Create(CultureInfo.InvariantCulture, $"Process started on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}) with {args.Length} argument(s). Directory lock active."));

                    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

                    if (AptabaseExtensions.IsInitialized)
                    {
                        try
                        {
                            var disposeTask = AptabaseExtensions.Instance.DisposeAsync().AsTask();
                            Task.WaitAny([disposeTask], 300);
                        }
                        catch (OperationCanceledException)
                        {
                            // Clean cancellation on exit
                        }
                        catch (Exception aptaEx)
                        {
                            AppLogger.Trace($"[Program] Aptabase shutdown flush notice: {aptaEx.Message}");
                        }
                    }

                    GeoIpService.Shutdown();
                    AppLogger.Info("Process shutting down cleanly. Flushing telemetry and log buffers.");
                    AppLogger.Shutdown();
                }
            }
            catch (Exception ex)
            {
                HandleEmergencyStartupCrash("Program.Main.Fatal", ex, isTerminating: true);
                throw;
            }
        }
    }

    private static bool TryAcquireDirectoryLock(out IDisposable lockHandle)
    {
        lockHandle = null!;
        try
        {
            var normalizedDirectory = Path.GetFullPath(AppContext.BaseDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();

            var directoryHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedDirectory)));

            var mutexName = OperatingSystem.IsWindows()
                ? $"Local\\ReforgerRcon_DirLock_{directoryHash}"
                : $"ReforgerRcon_DirLock_{directoryHash}";

            _directoryMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                return false;
            }

            var appDataDir = Path.Combine(AppContext.BaseDirectory, AppDataDirectoryName);
            if (!Directory.Exists(appDataDir))
            {
                Directory.CreateDirectory(appDataDir);
            }

            var lockFilePath = Path.Combine(appDataDir, "process.lock");
            _directoryLockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            lockHandle = new DirectoryLockDisposable(_directoryMutex, _directoryLockStream);
            return true;
        }
        catch (IOException ioEx)
        {
            AppLogger.Error($"[Program] Directory lock acquisition I/O collision: {ioEx.Message}", ioEx);
            _directoryLockStream?.Dispose();
            _directoryLockStream = null;
            _directoryMutex?.Dispose();
            _directoryMutex = null;
            return false;
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Error($"[Program] Directory lock acquisition permission error: {authEx.Message}", authEx);
            _directoryLockStream?.Dispose();
            _directoryLockStream = null;
            _directoryMutex?.Dispose();
            _directoryMutex = null;
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[Program] Unexpected error acquiring directory lock: {ex.Message}", ex);
            _directoryLockStream?.Dispose();
            _directoryLockStream = null;
            _directoryMutex?.Dispose();
            _directoryMutex = null;
            return false;
        }
    }

    private static string? ResolveAppIconDiskPath()
    {
        try
        {
            var candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", AppIconFileName),
                Path.Combine(AppContext.BaseDirectory, "assets", AppIconFileName),
                Path.Combine(AppContext.BaseDirectory, AppIconFileName),
                Path.Combine(AppContext.BaseDirectory, AppDataDirectoryName, AppIconFileName)
            };

            var existingPath = candidatePaths.FirstOrDefault(File.Exists);
            if (existingPath != null)
            {
                return existingPath;
            }

            var appDataDir = Path.Combine(AppContext.BaseDirectory, AppDataDirectoryName);
            if (!Directory.Exists(appDataDir))
            {
                Directory.CreateDirectory(appDataDir);
            }

            var targetFile = Path.Combine(appDataDir, AppIconFileName);
            var avaresUri = new Uri(AppIconResourceUri);
            if (AssetLoader.Exists(avaresUri))
            {
                using var srcStream = AssetLoader.Open(avaresUri);
                using var dstStream = File.Create(targetFile);
                srcStream.CopyTo(dstStream);
                return targetFile;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[Program] App icon disk resolution notice: {ex.Message}");
        }

        return null;
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont();

        try
        {
            var iconPath = ResolveAppIconDiskPath();
            AppLogger.Info($"[Program] Initializing native notifications with icon path: '{iconPath ?? "None"}'");

            builder.WithAppNotifications(new AppNotificationOptions
            {
                AppName = "ARMA Reforger RCON Tool (ARRT)",
                AppIcon = iconPath,
                ClearOnAppClose = false,
                Channels =
                [
                    new NotificationChannel("default", "General Notifications", NotificationPriority.Default),
                    new NotificationChannel("players", "Player Join & Leave Alerts", NotificationPriority.High),
                    new NotificationChannel("watchlist", "Watchlist Alerts", NotificationPriority.Max),
                    new NotificationChannel("system", "System and Moderation Alerts", NotificationPriority.High)
                ]
            });
            AppLogger.Info("[Program] Avalonia.Labs.Notifications integration successfully initialized on AppBuilder.");
        }
        catch (Exception notifEx)
        {
            AppLogger.Error($"[Program] Native notification initialization notice: {notifEx.Message}", notifEx);
        }

        var aptabaseKey = AppLogger.ResolveAptabaseAppKey();
        if (!string.IsNullOrWhiteSpace(aptabaseKey))
        {
            try
            {
                var storagePath = Path.Combine(AppContext.BaseDirectory, AppDataDirectoryName, "analytics");
                if (!Directory.Exists(storagePath))
                {
                    Directory.CreateDirectory(storagePath);
                }

                builder.UseAptabase(aptabaseKey, new AptabaseOptions
                {
                    EnablePersistence = true,
                    EnableCrashReporting = true,
                    CaptureAvaloniaFrameworkLogs = true,
                    AvaloniaLogEventLevel = LogEventLevel.Warning,
                    StoragePath = storagePath,
                    SuppressUIThreadCrashes = true,
                    ConsentCheck = AppSettings.IsCrashReportingEnabled,
                    ContextInjector = () =>
                    {
                        var settings = AppSettings.LoadFromDisk();
                        return new Dictionary<string, object>
                        {
                            ["installation_id"] = AppLogger.InstallationId,
                            ["theme_mode"] = settings.ThemeMode,
                            ["glass_enabled"] = settings.EnableWindowGlass,
                            ["audio_alerts_enabled"] = settings.AudioAlerts,
                            ["push_notifications_enabled"] = settings.PushNotifications,
                            ["geoip_city_ready"] = GeoIpService.IsCityDbLoaded,
                            ["geoip_country_ready"] = GeoIpService.IsCountryDbLoaded
                        };
                    },
                    OnUserFacingNotification = (msg, _, fatal) =>
                    {
                        if (fatal)
                        {
                            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
                            ToastNotificationService.Instance.ShowError("Critical Alert", msg);
                        }
                    }
                });

                AppLogger.Info($"[Program] Aptabase SDK attached to AppBuilder (Key: {aptabaseKey[..7]}..., Persistence: Active).");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[Program] Aptabase initialization warning: {ex.Message}", ex);
            }
        }

        Logger.Sink = new AvaloniaLogSink(LogEventLevel.Warning);
        return builder;
    }

    private static void HandleEmergencyStartupCrash(string source, Exception ex, bool isTerminating)
    {
        try
        {
            CrashReportService.HandleFatalException(source, ex, isTerminating);
        }
        catch (Exception fallbackEx)
        {
            try
            {
                var crashDir = Path.Combine(AppContext.BaseDirectory, AppDataDirectoryName, "crash_reports");
                Directory.CreateDirectory(crashDir);
                var crashFile = Path.Combine(crashDir, string.Create(CultureInfo.InvariantCulture, $"emergency_crash_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt"));
                var report = string.Create(CultureInfo.InvariantCulture, $"FATAL STARTUP CRASH\nInstallation ID: {AppLogger.InstallationId}\nOS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\nSource: {source}\nException: {ex.GetType().FullName}: {ex.Message}\nStackTrace:\n{ex.StackTrace}\n\nHandler Fault: {fallbackEx.Message}");
                File.WriteAllText(crashFile, report);

                if (OperatingSystem.IsWindows())
                {
                    MessageBox(IntPtr.Zero, $"A fatal error occurred during startup:\n\n{ex.GetType().Name}: {ex.Message}\n\nDiagnostic report written to:\n{crashFile}", "ARMA Reforger RCON - Fatal Startup Error", MbIconError);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(LogSeparatorLine);
                    Console.Error.WriteLine("FATAL APPLICATION STARTUP ERROR");
                    Console.Error.WriteLine($"Installation ID: {AppLogger.InstallationId}");
                    Console.Error.WriteLine($"Source:          {source}");
                    Console.Error.WriteLine($"Exception:       {ex.GetType().FullName}: {ex.Message}");
                    Console.Error.WriteLine($"Report:          {crashFile}");
                    Console.Error.WriteLine(LogSeparatorLine);
                    Console.ResetColor();
                }
            }
            catch (Exception diskEx)
            {
                Debug.WriteLine($"[Program] Failed writing emergency crash to disk: {diskEx.Message}");
            }
        }
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
            catch (IOException ioEx)
            {
                AppLogger.Trace($"[Program] Lock file stream disposal notice: {ioEx.Message}");
            }

            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException appEx)
            {
                AppLogger.Trace($"[Program] Mutex release notice: {appEx.Message}");
            }

            try
            {
                _mutex.Dispose();
            }
            catch (ObjectDisposedException dispEx)
            {
                AppLogger.Trace($"[Program] Mutex already disposed: {dispEx.Message}");
            }
        }
    }
}