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
            Dispatcher.UIThread.UnhandledExceptionFilter += (sender, e) =>
            {
                if (e.Exception is OperationCanceledException or TaskCanceledException)
                {
                    e.RequestCatch = false;
                }
            };

            Dispatcher.UIThread.UnhandledException += (sender, e) =>
            {
                CrashReportService.HandleFatalException("Dispatcher.UIThread.UnhandledException", e.Exception, isTerminating: false);
                e.Handled = true;
            };

            var savedSettings = AppSettings.LoadFromDisk();
            string themeMode = savedSettings.ThemeMode;
            bool windowGlassEnabled = savedSettings.EnableWindowGlass;

            try
            {
                LuminaThemeManager.Initialize(this);
                AppSettings.ApplyThemeMode(themeMode);
            }
            catch (Exception themeEx)
            {
                AppLogger.Error($"[App:FrameworkInit] Theme initialization error: {themeEx.Message}", themeEx);
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
            sw.Stop();

            var frameworkDurationMs = sw.Elapsed.TotalMilliseconds;
            AppLogger.Info($"[App:FrameworkInit] Framework initialization completed in {frameworkDurationMs:F2}ms.", context);

            // Offload all heavy telemetry and background services completely off the UI thread
            _ = Task.Run(async () =>
            {
                // Give the UI 350ms to paint the window before kicking off disk-heavy SQLite & GeoIP
                await Task.Delay(350).ConfigureAwait(false);

                // Start SQLite, GeoIP, and Update loops
                Program.StartDeferredBackgroundServices();
                FlagAssetService.PrewarmCommonFlags();
                PushNotificationService.Initialize();

                // Telemetry events
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

                bool isDebug = false;
#if DEBUG
                isDebug = true;
#endif
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
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Fatal("[App:FrameworkInit] Fatal failure during OnFrameworkInitializationCompleted.", ex, context);
            CrashReportService.HandleFatalException("App.OnFrameworkInitializationCompleted", ex, isTerminating: true);
            throw;
        }
    }
}