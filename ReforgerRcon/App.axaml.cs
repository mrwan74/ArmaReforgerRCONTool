using System;
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
        try
        {
            AppLogger.Info("Initializing Avalonia XAML Loader...");
            AvaloniaXamlLoader.Load(this);
            AppLogger.Info("Avalonia XAML resources successfully loaded.");

#if DEBUG
            AppLogger.Info("Enabling AvaloniaUI Developer Tools bridge. Press F12 while running to inspect visual tree.");
            this.AttachDeveloperTools();
#endif
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("Failed initializing Avalonia XAML resources.", ex);
            CrashReportService.HandleFatalException("App.Initialize", ex, isTerminating: true);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
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
            LuminaThemeManager.Initialize(this);

            AppLogger.Info("Initializing MaxMind GeoIP2 Engine...");
            GeoIpService.Initialize();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppLogger.Info("Creating MainWindow instance...");
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
            AppLogger.Info("Framework initialization successfully completed.");
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("Fatal exception during FrameworkInitializationCompleted.", ex);
            CrashReportService.HandleFatalException("App.OnFrameworkInitializationCompleted", ex, isTerminating: true);
            throw;
        }
    }
}