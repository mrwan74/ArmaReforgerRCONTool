using Avalonia;
using Avalonia.Controls;
using Avalonia.Logging;
using ReforgerRcon.Services;
using Sentry;
using Sentry.Profiling;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon;

internal static partial class Program
{
    private const uint MbIconError = 0x00000010;
    private const uint MbIconWarning = 0x00000030;
    private const string LogSeparatorLine = "================================================================================";

    private static Mutex? _directoryMutex;
    private static FileStream? _directoryLockStream;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    public static void Main(string[] args)
    {
        if (!TryAcquireDirectoryLock(out var instanceLockHandle))
        {
            var runningDir = AppContext.BaseDirectory;
            var alertMessage = $"Another instance of ARMA Reforger RCON is already running from this directory:\n\n{runningDir}\n\nOnly one instance per directory is allowed. To run multiple instances simultaneously, place the application in a separate folder.";

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
                HandleEmergencyStartupCrash("AppDomain.UnhandledException", ex, e.IsTerminating);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                HandleEmergencyStartupCrash("TaskScheduler.UnobservedTaskException", e.Exception, isTerminating: false);
                e.SetObserved();
            };

            var dsn = AppLogger.ResolveSentryDsn();
            IDisposable? sentrySdk = null;

            if (!string.IsNullOrWhiteSpace(dsn))
            {
                sentrySdk = SentrySdk.Init(options =>
                {
                    options.Dsn = dsn;
                    options.Debug = false;
                    options.AutoSessionTracking = true;
                    options.TracesSampleRate = 1.0;
                    options.ProfilesSampleRate = 1.0;
                    options.AddIntegration(new ProfilingIntegration(TimeSpan.FromMilliseconds(500)));
                    options.EnableLogs = true;
                    options.AttachStacktrace = true;
                    options.SendDefaultPii = false;
                    options.Environment = "production";
                    options.Release = "ReforgerRcon@0.7.2";
                });
            }

            try
            {
                using (sentrySdk)
                {
                    CrashReportService.Initialize();
                    AppLogger.Info(string.Create(CultureInfo.InvariantCulture, $"Process started on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}) with {args.Length} arguments. Directory lock active."));

                    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);

                    AppLogger.Info("Process shutting down cleanly. Flushing buffers.");
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
            var mutexName = $"Local\\ReforgerRcon_DirLock_{directoryHash}";

            _directoryMutex = new Mutex(true, mutexName, out bool createdNew);
            if (!createdNew)
            {
                return false;
            }

            var appDataDir = Path.Combine(AppContext.BaseDirectory, "appdata");
            if (!Directory.Exists(appDataDir))
            {
                Directory.CreateDirectory(appDataDir);
            }

            var lockFilePath = Path.Combine(appDataDir, "process.lock");
            _directoryLockStream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            lockHandle = new DirectoryLockDisposable(_directoryMutex, _directoryLockStream);
            return true;
        }
        catch
        {
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
                var crashDir = Path.Combine(AppContext.BaseDirectory, "appdata", "crash_reports");
                Directory.CreateDirectory(crashDir);
                var crashFile = Path.Combine(crashDir, string.Create(CultureInfo.InvariantCulture, $"emergency_crash_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt"));
                var report = string.Create(CultureInfo.InvariantCulture, $"FATAL STARTUP CRASH\nOS: {RuntimeInformation.OSDescription}\nArchitecture: {RuntimeInformation.ProcessArchitecture}\nSource: {source}\nException: {ex.GetType().FullName}: {ex.Message}\nStackTrace:\n{ex.StackTrace}\n\nHandler Fault: {fallbackEx.Message}");
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
                    Console.Error.WriteLine($"Source:    {source}");
                    Console.Error.WriteLine($"Exception: {ex.GetType().FullName}: {ex.Message}");
                    Console.Error.WriteLine($"Report:    {crashFile}");
                    Console.Error.WriteLine(LogSeparatorLine);
                    Console.ResetColor();
                }
            }
            catch (Exception diskEx)
            {
                System.Diagnostics.Debug.WriteLine($"[Program] Failed writing emergency crash to disk: {diskEx.Message}");
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
            catch
            {
                // Ignored during shutdown
            }

            try
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
            catch
            {
                // Ignored during shutdown
            }
        }
    }
}