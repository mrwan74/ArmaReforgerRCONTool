using Aptabase.Avalonia;
using Avalonia;
using Avalonia.Controls;
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
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ReforgerRcon;

public partial class App : Application
{
    private const string StackTraceKey = "stack_trace";
    private double _xamlLoadDurationMs;

#if DEBUG
    private bool _developerToolsAttached;
#endif

    public override void Initialize()
    {
        var sw = Stopwatch.StartNew();
        var context = new Dictionary<string, object?>
        {
            ["thread_id"] = Environment.CurrentManagedThreadId,
            ["process_id"] = Environment.ProcessId,
            ["os_description"] = RuntimeInformation.OSDescription,
            ["os_architecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["clr_runtime"] = RuntimeInformation.FrameworkDescription
        };

        try
        {
            AppLogger.Debug("[App:Initialize] Loading Avalonia XAML application markup and resources...", context);
            AvaloniaXamlLoader.Load(this);
            sw.Stop();
            _xamlLoadDurationMs = sw.Elapsed.TotalMilliseconds;
            context["xaml_load_duration_ms"] = _xamlLoadDurationMs;
            AppLogger.Info($"[App:Initialize] Avalonia XAML tree loaded in {_xamlLoadDurationMs:F2}ms.", context);

#if DEBUG
            if (!_developerToolsAttached)
            {
                _developerToolsAttached = true;
                try
                {
                    AppLogger.Trace("[App:Initialize] Attaching Avalonia Developer Tools (F12 shortcut)...");
                    this.AttachDeveloperTools(options =>
                    {
                        options.ConnectOnStartup = false;
                        options.Gesture = new KeyGesture(Key.F12);
                        options.Protocol = DeveloperToolsProtocol.DefaultHttp;
                    });
                    AppLogger.Debug("[App:Initialize] Developer Tools attached successfully.");
                }
                catch (Exception devEx)
                {
                    AppLogger.Warn($"[App:Initialize] Non-critical warning attaching Developer Tools: {devEx.Message}", devEx);
                }
            }
#endif
        }
        catch (Exception ex)
        {
            sw.Stop();
            context["duration_ms"] = sw.Elapsed.TotalMilliseconds;
            context["exception_type"] = ex.GetType().FullName;
            context["exception_message"] = ex.Message;
            context[StackTraceKey] = ex.StackTrace;
            AppLogger.Fatal("[App:Initialize] Fatal failure during Avalonia XAML resource tree loading.", ex, context);
            CrashReportService.HandleFatalException("App.Initialize", ex, isTerminating: true);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var sw = Stopwatch.StartNew();
        var context = new Dictionary<string, object?>
        {
            ["thread_id"] = Environment.CurrentManagedThreadId,
            ["process_id"] = Environment.ProcessId,
            ["working_set_mb"] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 2),
            ["processor_count"] = Environment.ProcessorCount
        };

        try
        {
            AppLogger.Debug("[App:FrameworkInit] Registering Dispatcher.UIThread unhandled exception safety nets...", context);

            Dispatcher.UIThread.UnhandledExceptionFilter += (sender, e) =>
            {
                if (e.Exception is OperationCanceledException or TaskCanceledException)
                {
                    AppLogger.Trace($"[App:SafetyNet] Filtered expected task cancellation exception: {e.Exception.GetType().Name}.");
                    e.RequestCatch = false;
                }
                else
                {
                    AppLogger.Debug($"[App:SafetyNet] UnhandledExceptionFilter evaluated: {e.Exception.GetType().FullName}: {e.Exception.Message}");
                }
            };

            Dispatcher.UIThread.UnhandledException += (sender, e) =>
            {
                var faultContext = new Dictionary<string, object?>
                {
                    ["thread_id"] = Environment.CurrentManagedThreadId,
                    ["exception_type"] = e.Exception.GetType().FullName,
                    ["message"] = e.Exception.Message,
                    [StackTraceKey] = e.Exception.StackTrace,
                    ["inner_exception"] = e.Exception.InnerException?.Message
                };

                AppLogger.Fatal("[App:SafetyNet] Unhandled UI thread exception intercepted by safety net.", e.Exception, faultContext);
                CrashReportService.HandleFatalException("Dispatcher.UIThread.UnhandledException", e.Exception, isTerminating: false);

                try
                {
                    ToastNotificationService.Instance.ShowError(
                        "Application Fault Intercepted",
                        $"An unhandled UI error was intercepted and logged: {e.Exception.Message}"
                    );
                }
                catch (Exception toastEx)
                {
                    AppLogger.Trace($"[App:SafetyNet] Notice displaying error toast: {toastEx.Message}");
                }

                e.Handled = true;
            };

            AppLogger.Info("[App:FrameworkInit] Initializing LuminaUI theme manager...", context);
            string themeMode = "System";
            bool windowGlassEnabled = false;

            try
            {
                LuminaThemeManager.Initialize(this);

                var savedSettings = AppSettings.LoadFromDisk();
                themeMode = savedSettings.ThemeMode;
                windowGlassEnabled = savedSettings.EnableWindowGlass;
                AppSettings.ApplyThemeMode(themeMode);

                AppLogger.Info($"[App:FrameworkInit] Theme applied: Mode='{themeMode}', WindowGlass={windowGlassEnabled}.");

                AppLogger.TrackEvent("notification_channel_status", new Dictionary<string, object>
                {
                    ["audio_enabled"] = savedSettings.AudioAlerts,
                    ["toast_enabled"] = savedSettings.ToastNotifications,
                    ["push_enabled"] = savedSettings.PushNotifications,
                    ["alert_on_join"] = savedSettings.AlertOnJoin,
                    ["alert_on_leave"] = savedSettings.AlertOnLeave,
                    ["alert_on_watchlist_join"] = savedSettings.AlertOnWatchlistJoin,
                    ["alert_on_watchlist_leave"] = savedSettings.AlertOnWatchlistLeave,
                    ["os_platform"] = RuntimeInformation.OSDescription
                });

                AppLogger.TrackEvent("theme_choice_updated", new Dictionary<string, object>
                {
                    ["theme_mode"] = themeMode,
                    ["window_glass_enabled"] = windowGlassEnabled,
                    ["is_startup"] = true
                });
            }
            catch (Exception themeEx)
            {
                AppLogger.Error($"[App:FrameworkInit] Theme initialization error: {themeEx.Message}. Falling back to default.", themeEx);
                ToastNotificationService.Instance.ShowWarning("Theme Warning", "Failed to apply custom theme variant; defaulted to system variant.");
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppLogger.Info("[App:FrameworkInit] Instantiating MainWindow for desktop lifetime...", context);
                desktop.MainWindow = new MainWindow();
            }
            else
            {
                AppLogger.Warn($"[App:FrameworkInit] ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime: {ApplicationLifetime?.GetType().FullName ?? "null"}");
            }

            base.OnFrameworkInitializationCompleted();
            sw.Stop();

            var frameworkDurationMs = sw.Elapsed.TotalMilliseconds;
            context["framework_init_ms"] = frameworkDurationMs;
            AppLogger.Info($"[App:FrameworkInit] Framework initialization completed in {frameworkDurationMs:F2}ms.", context);

            bool isDebug = false;
#if DEBUG
            isDebug = true;
#endif

            try
            {
                AppLogger.TrackEvent("app_startup_benchmark", new Dictionary<string, object>
                {
                    ["framework_init_ms"] = frameworkDurationMs,
                    ["xaml_load_ms"] = _xamlLoadDurationMs,
                    ["theme_mode"] = themeMode,
                    ["window_glass_enabled"] = windowGlassEnabled,
                    ["is_debug_build"] = isDebug,
                    ["processor_count"] = Environment.ProcessorCount,
                    ["ram_working_set_mb"] = Math.Round(Environment.WorkingSet / (1024.0 * 1024.0), 1)
                });
            }
            catch (Exception telemetryEx)
            {
                AppLogger.Warn($"[App:FrameworkInit] Telemetry dispatch notice: {telemetryEx.Message}", telemetryEx);
            }

            // Trigger asset and push services safely now that Avalonia is fully initialized
            _ = Task.Run(() =>
            {
                FlagAssetService.PrewarmCommonFlags();
                PushNotificationService.Initialize();
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            context["duration_ms"] = sw.Elapsed.TotalMilliseconds;
            context["exception_type"] = ex.GetType().FullName;
            context["exception_message"] = ex.Message;
            context[StackTraceKey] = ex.StackTrace;
            AppLogger.Fatal("[App:FrameworkInit] Fatal failure during OnFrameworkInitializationCompleted.", ex, context);
            CrashReportService.HandleFatalException("App.OnFrameworkInitializationCompleted", ex, isTerminating: true);
            throw;
        }
    }
}