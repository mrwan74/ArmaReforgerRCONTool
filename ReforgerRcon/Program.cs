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
using System.Threading.Tasks;

namespace ReforgerRcon;

internal static partial class Program
{
    private const uint MbIconError = 0x00000010;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    public static void Main(string[] args)
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
                options.Release = "ReforgerRcon@1.0.0";
            });
        }

        try
        {
            using (sentrySdk)
            {
                CrashReportService.Initialize();
                AppLogger.Info(string.Create(CultureInfo.InvariantCulture, $"Process started with {args.Length} arguments. Global safety nets active."));

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
                var report = string.Create(CultureInfo.InvariantCulture, $"FATAL STARTUP CRASH\nSource: {source}\nException: {ex.GetType().FullName}: {ex.Message}\nStackTrace:\n{ex.StackTrace}\n\nHandler Fault: {fallbackEx.Message}");
                File.WriteAllText(crashFile, report);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    MessageBox(IntPtr.Zero, $"A fatal error occurred during startup:\n\n{ex.GetType().Name}: {ex.Message}\n\nDiagnostic report written to:\n{crashFile}", "ARMA Reforger RCON - Fatal Startup Error", MbIconError);
                }
            }
            catch
            {
                // Last-resort fallback
            }
        }
    }
}