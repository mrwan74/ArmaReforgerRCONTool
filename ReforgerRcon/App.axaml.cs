using Aptabase.Avalonia;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaUI.DiagnosticsSupport;
using LuminaUI.Theming;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using ReforgerRcon.Views;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ReforgerRcon;

public partial class App : Application
{
#if DEBUG
    private bool _developerToolsAttached;
#endif

    public override void Initialize()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            AppLogger.Info("[App:Init] Loading Avalonia XAML resources...");
            AvaloniaXamlLoader.Load(this);
            sw.Stop();
            AppLogger.Info($"[App:Init] Avalonia XAML loaded in {sw.ElapsedMilliseconds}ms.");

#if DEBUG
            if (!_developerToolsAttached)
            {
                _developerToolsAttached = true;
                try
                {
                    AppLogger.Info("[App:Init] Configuring Avalonia Developer Tools (Local F12 shortcut, on-demand connect)...");
                    this.AttachDeveloperTools(options =>
                    {
                        options.ConnectOnStartup = false;
                        options.Gesture = new KeyGesture(Key.F12);
                        options.Protocol = DeveloperToolsProtocol.DefaultHttp;
                    });
                }
                catch (Exception devEx)
                {
                    AppLogger.Warn($"Failed attaching Developer Tools: {devEx.Message}", devEx);
                }
            }
#endif
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Fatal("[App:Init] Failed initializing XAML resources.", ex);
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
                    AppLogger.Debug($"[App:SafetyNet] Filtered expected cancellation {e.Exception.GetType().Name}.");
                    e.RequestCatch = false;
                }
            };

            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLogger.Fatal("[App:SafetyNet] Unhandled exception on UI thread.", e.Exception);
                CrashReportService.HandleFatalException("Dispatcher.UIThread.UnhandledException", e.Exception, isTerminating: false);
                e.Handled = true;
            };

            AppLogger.Info("[App:Init] Initializing LuminaUI theme engine...");
            try
            {
                LuminaThemeManager.Initialize(this);

                var savedSettings = AppSettings.LoadFromDisk();
                AppSettings.ApplyThemeMode(savedSettings.ThemeMode);
                AppLogger.Info($"[App:Init] Applied startup theme: {savedSettings.ThemeMode}");
            }
            catch (Exception themeEx)
            {
                AppLogger.Error("[App:Init] Theme init notice: " + themeEx.Message, themeEx);
                ToastNotificationService.Instance.ShowWarning("Theme Warning", "Failed to apply custom theme variant. Reverting to default.");
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppLogger.Info("[App:Init] Creating MainWindow instance...");
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
            sw.Stop();
            AppLogger.Info($"[App:Init] Framework initialization completed in {sw.ElapsedMilliseconds}ms.");

            // Start deferred background services after UI display
            Program.StartDeferredBackgroundServices();
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Fatal("[App:Init] Fatal exception in FrameworkInitializationCompleted.", ex);
            CrashReportService.HandleFatalException("App.OnFrameworkInitializationCompleted", ex, isTerminating: true);
            throw;
        }
    }
}