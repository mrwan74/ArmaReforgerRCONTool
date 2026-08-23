using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaUI.DiagnosticsSupport;
using LuminaUI.Theming;
using ReforgerRcon.Services;
using ReforgerRcon.Views;

namespace ReforgerRcon;

public partial class App : Application
{
    public override void Initialize()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            AppLogger.Info("Initializing Avalonia XAML Loader...");
            AvaloniaXamlLoader.Load(this);
            sw.Stop();
            AppLogger.Info($"Avalonia XAML resources successfully loaded in {sw.ElapsedMilliseconds} ms.");

#if DEBUG
            AppLogger.Info("Enabling AvaloniaUI Developer Tools bridge. Press F12 while running to inspect visual tree.");
            this.AttachDeveloperTools();
#endif
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Fatal("Failed initializing Avalonia XAML resources.", ex);
            CrashReportService.HandleFatalException("App.Initialize", ex, isTerminating: true);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            Dispatcher.UIThread.UnhandledExceptionFilter += (_, e) =>
            {
                if (e.Exception is OperationCanceledException)
                {
                    AppLogger.Debug("[Dispatcher.UIThread.UnhandledExceptionFilter] Filtered expected OperationCanceledException from UI crash handler.");
                    e.RequestCatch = false;
                }
            };

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLogger.Fatal("[Dispatcher.UIThread.UnhandledException] Unhandled exception on UI thread.", e.Exception);
                CrashReportService.HandleFatalException("Dispatcher.UIThread.UnhandledException", e.Exception, isTerminating: false);
                e.Handled = true;
            };

            AppLogger.Info("Initializing LuminaUI Theme Engine...");
            try
            {
                LuminaThemeManager.Initialize(this);
            }
            catch (Exception themeEx)
            {
                AppLogger.Error("LuminaUI Theme initialization notice: " + themeEx.Message, themeEx);
                ToastNotificationService.Instance.ShowWarning("Theme Warning", "Failed to apply custom theme variant. Reverting to dark default.");
            }

            AppLogger.Info("Initializing MaxMind GeoIP2 Engine...");
            try
            {
                GeoIpService.Initialize();
            }
            catch (Exception geoEx)
            {
                AppLogger.Error("GeoIP engine initialization failed: " + geoEx.Message, geoEx);
                ToastNotificationService.Instance.ShowWarning("GeoIP Warning", "Geolocation lookup engine could not initialize. Operating in offline mode.");
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppLogger.Info("Creating MainWindow instance...");
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
            sw.Stop();
            AppLogger.Info($"Framework initialization successfully completed in {sw.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Fatal("Fatal exception during FrameworkInitializationCompleted.", ex);
            CrashReportService.HandleFatalException("App.OnFrameworkInitializationCompleted", ex, isTerminating: true);
            throw;
        }
    }
}