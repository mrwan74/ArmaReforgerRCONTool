using Aptabase.Avalonia;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaUI.DiagnosticsSupport;
using LuminaUI.Theming;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using ReforgerRcon.Views;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ReforgerRcon;

public partial class App : Application
{
    private bool _developerToolsAttached;

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
            if (!_developerToolsAttached)
            {
                _developerToolsAttached = true;
                try
                {
                    AppLogger.Info("Attaching AvaloniaUI Developer Tools diagnostics bridge (Port 29414)...");
                    this.AttachDeveloperTools();
                }
                catch (Exception devEx)
                {
                    AppLogger.Warn($"Failed attaching Developer Tools diagnostics bridge: {devEx.Message}", devEx);
                }
            }
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
                if (e.Exception is OperationCanceledException or TaskCanceledException)
                {
                    AppLogger.Debug($"[Dispatcher.UIThread.UnhandledExceptionFilter] Filtered expected {e.Exception.GetType().Name} from UI error handler.");
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

                var savedSettings = AppSettings.LoadFromDisk();
                AppSettings.ApplyThemeMode(savedSettings.ThemeMode);
                AppLogger.Info($"[App] Applied startup theme mode: {savedSettings.ThemeMode}");
            }
            catch (Exception themeEx)
            {
                AppLogger.Error("LuminaUI Theme initialization notice: " + themeEx.Message, themeEx);
                ToastNotificationService.Instance.ShowWarning("Theme Warning", "Failed to apply custom theme variant. Reverting to default.");
            }

            AppLogger.Info("Initializing MaxMind GeoIP2 Engine asynchronously...");
            GeoIpService.Initialize();

            // Safe background pre-warm of flag icons now that AssetLoader is registered
            FlagAssetService.PrewarmCache();

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppLogger.Info("Creating MainWindow instance...");
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
            sw.Stop();
            AppLogger.Info($"Framework initialization successfully completed in {sw.ElapsedMilliseconds} ms.");

            _ = Task.Run(() =>
            {
                try
                {
                    AppLogger.TrackEvent("app_started", new Dictionary<string, object>
                    {
                        ["os"] = RuntimeInformation.OSDescription,
                        ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
                        ["cores"] = Environment.ProcessorCount,
                        ["telemetry_enabled"] = AppSettings.IsCrashReportingEnabled()
                    });
                }
                catch (Exception ex)
                {
                    AppLogger.Debug($"[App] Failed tracking app_started event: {ex.Message}");
                }
            });
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