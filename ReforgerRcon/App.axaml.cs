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
            AppLogger.Debug("[App:Initialize] Beginning Avalonia XAML resource tree loading...", context);
            AvaloniaXamlLoader.Load(this);
            sw.Stop();
            _xamlLoadDurationMs = sw.Elapsed.TotalMilliseconds;
            context["xaml_load_duration_ms"] = _xamlLoadDurationMs;
            AppLogger.Info($"[App:Initialize] Avalonia XAML resource tree loaded successfully in {_xamlLoadDurationMs:F2}ms.", context);

#if DEBUG
            if (!_developerToolsAttached)
            {
                _developerToolsAttached = true;
                try
                {
                    AppLogger.Trace("[App:Initialize] Configuring Avalonia Developer Tools (Gesture=F12, Protocol=DefaultHttp)...");
                    this.AttachDeveloperTools(options =>
                    {
                        options.ConnectOnStartup = false;
                        options.Gesture = new KeyGesture(Key.F12);
                        options.Protocol = DeveloperToolsProtocol.DefaultHttp;
                    });
                    AppLogger.Debug("[App:Initialize] Avalonia Developer Tools configured successfully.");
                }
                catch (Exception devEx)
                {
                    var devContext = new Dictionary<string, object?>
                    {
                        ["error_message"] = devEx.Message,
                        [StackTraceKey] = devEx.StackTrace
                    };
                    AppLogger.Warn($"[App:Initialize] Non-critical warning: Developer Tools attachment failed: {devEx.Message}", devEx, devContext);
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
            AppLogger.Fatal("[App:Initialize] Fatal failure encountered during Avalonia XAML resource loading.", ex, context);
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
            AppLogger.Debug("[App:FrameworkInit] Registering UIThread unhandled exception safety nets...", context);

            Dispatcher.UIThread.UnhandledExceptionFilter += (_, e) =>
            {
                if (e.Exception is OperationCanceledException or TaskCanceledException)
                {
                    AppLogger.Trace($"[App:SafetyNet] Filtered expected cancellation exception: {e.Exception.GetType().Name}.");
                    e.RequestCatch = false;
                }
                else
                {
                    AppLogger.Debug($"[App:SafetyNet] UnhandledExceptionFilter evaluating: {e.Exception.GetType().FullName}: {e.Exception.Message}");
                }
            };

            Dispatcher.UIThread.UnhandledException += (_, e) =>
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
                ToastNotificationService.Instance.ShowError("Application UI Error", $"An unexpected interface fault was intercepted: {e.Exception.Message}");
                e.Handled = true;
            };

            AppLogger.Info("[App:FrameworkInit] Initializing LuminaUI theme engine...", context);
            string themeMode = "System";
            bool windowGlassEnabled = false;

            try
            {
                LuminaThemeManager.Initialize(this);

                var savedSettings = AppSettings.LoadFromDisk();
                themeMode = savedSettings.ThemeMode;
                windowGlassEnabled = savedSettings.EnableWindowGlass;
                AppSettings.ApplyThemeMode(themeMode);
                AppLogger.Info($"[App:FrameworkInit] Theme configuration applied: Mode='{themeMode}', WindowGlass={windowGlassEnabled}.", new Dictionary<string, object?>
                {
                    ["theme_mode"] = themeMode,
                    ["window_glass_enabled"] = windowGlassEnabled
                });
            }
            catch (Exception themeEx)
            {
                var themeContext = new Dictionary<string, object?>
                {
                    ["theme_attempted"] = themeMode,
                    ["error_message"] = themeEx.Message
                };
                AppLogger.Error("[App:FrameworkInit] Exception during theme initialization: " + themeEx.Message, themeEx, themeContext);
                ToastNotificationService.Instance.ShowWarning("Theme Initialization Fault", "Failed to apply custom theme variant. Reverting to system default.");
            }

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                AppLogger.Info("[App:FrameworkInit] Initializing MainWindow visual component...", context);
                desktop.MainWindow = new MainWindow();
            }
            else
            {
                AppLogger.Warn($"[App:FrameworkInit] ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime. Current: {ApplicationLifetime?.GetType().FullName ?? "null"}", null, context);
            }

            base.OnFrameworkInitializationCompleted();
            sw.Stop();
            var frameworkDurationMs = sw.Elapsed.TotalMilliseconds;
            context["framework_init_ms"] = frameworkDurationMs;
            AppLogger.Info($"[App:FrameworkInit] Framework initialization finalized in {frameworkDurationMs:F2}ms.", context);

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
                AppLogger.Debug("[App:FrameworkInit] Dispatched startup benchmark telemetry event.");
            }
            catch (Exception telemetryEx)
            {
                AppLogger.Warn($"[App:FrameworkInit] Telemetry benchmark dispatch failed: {telemetryEx.Message}", telemetryEx);
            }

            AppLogger.Debug("[App:FrameworkInit] Launching deferred background worker services...");
            Program.StartDeferredBackgroundServices();
        }
        catch (Exception ex)
        {
            sw.Stop();
            context["duration_ms"] = sw.Elapsed.TotalMilliseconds;
            context["exception_type"] = ex.GetType().FullName;
            context["exception_message"] = ex.Message;
            context[StackTraceKey] = ex.StackTrace;
            AppLogger.Fatal("[App:FrameworkInit] Fatal exception in OnFrameworkInitializationCompleted.", ex, context);
            CrashReportService.HandleFatalException("App.OnFrameworkInitializationCompleted", ex, isTerminating: true);
            throw;
        }
    }
}