using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Theming;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace ReforgerRcon.ViewModels;

public partial class MainViewModel : ViewModelBase, IDisposable
{
    private bool _isDisposed;

    [ObservableProperty] public partial ViewModelBase CurrentView { get; set; }
    [ObservableProperty] public partial ErrorDetailsDialogViewModel? CurrentErrorViewModel { get; set; }
    [ObservableProperty] public partial bool IsErrorDialogVisible { get; set; }

    public MainViewModel()
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["thread_id"] = Environment.CurrentManagedThreadId,
            ["process_id"] = Environment.ProcessId,
            ["installation_id"] = AppLogger.InstallationId
        };

        AppLogger.Trace("[MainViewModel:Init] Commencing MainViewModel instantiation...", context);

        try
        {
            CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: true);
            CrashReportService.UnhandledErrorCaptured += OnUnhandledErrorCaptured;

            CheckForPendingCrashReports();

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["elapsed_ms"] = elapsedMs;
            AppLogger.Info($"[MainViewModel:Init] MainViewModel initialized successfully in {elapsedMs:F2}ms.", context);
        }
        catch (Exception ex)
        {
            context["error"] = ex.Message;
            AppLogger.Fatal("[MainViewModel:Init] Fatal exception constructing MainViewModel.", ex, context);
            CrashReportService.HandleFatalException("MainViewModel.Constructor", ex, isTerminating: true);
            throw;
        }
    }

    private void CheckForPendingCrashReports()
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Trace("[MainViewModel:CrashCheck] Querying CrashReportService for unobserved prior reports...");

        try
        {
            var pending = CrashReportService.GetAndClearPendingReports();
            if (pending.Count > 0)
            {
                var latest = pending[^1];
                AppLogger.Warn($"[MainViewModel:CrashCheck] Found {pending.Count} pending crash report(s). Displaying #{latest.ErrorId} ({latest.ExceptionType}).");
                OnUnhandledErrorCaptured(latest);
            }
            else
            {
                AppLogger.Trace("[MainViewModel:CrashCheck] No pending startup error reports found.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[MainViewModel:CrashCheck] Failed evaluating pending crash reports: " + ex.Message, ex);
            ToastNotificationService.Instance.ShowWarning("Crash Reporter Fault", "Failed reading pending crash logs from disk.");
        }
        finally
        {
            AppLogger.Trace($"[MainViewModel:CrashCheck] Completed check in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
    }

    private void OnUnhandledErrorCaptured(ErrorReportModel report)
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["error_id"] = report.ErrorId,
            ["source"] = report.Source,
            ["exception_type"] = report.ExceptionType,
            ["is_terminating"] = report.IsTerminating
        };

        AppLogger.Error($"[MainViewModel:Crash] Presenting global crash dialogue for #{report.ErrorId}: {report.ExceptionType} - {report.Message}", null, context);

        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                CurrentErrorViewModel = new ErrorDetailsDialogViewModel(report, CloseErrorDialog);
                IsErrorDialogVisible = true;
                AppLogger.Info($"[MainViewModel:Crash] Error dialogue rendered on UI thread in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            }, "Unable to display crash details dialog.");
        });
    }

    [RelayCommand]
    public void CloseErrorDialog()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[MainViewModel:Crash] User dismissed the error details dialog.");
            IsErrorDialogVisible = false;
            CurrentErrorViewModel = null;
        });
    }

    [RelayCommand]
    public static void ToggleTheme()
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>();

        try
        {
            LuminaThemeManager.ToggleThemeVariant();
            var currentActual = Application.Current?.ActualThemeVariant;
            var newMode = currentActual == ThemeVariant.Dark ? "Dark" : "Light";

            var settings = AppSettings.LoadFromDisk();
            var oldMode = settings.ThemeMode;
            settings.ThemeMode = newMode;
            AppSettings.SaveToDisk(settings);

            context["old_theme"] = oldMode;
            context["new_theme"] = newMode;
            context["elapsed_ms"] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            AppLogger.Info($"[MainViewModel:Theme] Theme toggled: {oldMode} -> {newMode} in {context["elapsed_ms"]:F2}ms.", context);
            AppLogger.TrackEvent("theme_toggled", new Dictionary<string, object>
            {
                ["theme_mode"] = newMode,
                ["source"] = "MainTitleBar"
            });
        }
        catch (Exception ex)
        {
            context["error"] = ex.Message;
            AppLogger.Error("[MainViewModel:Theme] Exception toggling application theme.", ex, context);
            ToastNotificationService.Instance.ShowWarning("Theme Error", "Failed to toggle visual theme: " + ex.Message);
        }
    }

    private void OnLoginSuccess(ServerProfile profile, IRconService rconService)
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["server_endpoint"] = profile.FormattedEndpoint,
            ["protocol"] = profile.Protocol.ToString(),
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[MainViewModel:Navigation] Login success reported. Transitioning from {CurrentView.GetType().Name} to DashboardView...", context);

        ExecuteSafe(() =>
        {
            var oldView = CurrentView;

            var dashboardVm = new DashboardViewModel(profile, rconService, OnDisconnect, OnSwitchProtocolAsync);
            dashboardVm.Initialize();
            CurrentView = dashboardVm;

            AppLogger.TrackEvent("navigation_view_changed", new Dictionary<string, object>
            {
                ["from_view"] = oldView.GetType().Name,
                ["to_view"] = nameof(DashboardViewModel),
                ["protocol"] = profile.Protocol.ToString()
            });

            if (oldView is IDisposable disposableOldView)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var disposeStart = Stopwatch.GetTimestamp();
                        disposableOldView.Dispose();
                        AppLogger.Debug($"[MainViewModel:Navigation] Disposed previous view: {disposableOldView.GetType().Name} in {Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds:F2}ms.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"[MainViewModel:Navigation] Error disposing old view ({disposableOldView.GetType().Name}): {ex.Message}", ex);
                    }
                }, DispatcherPriority.Background);
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[MainViewModel:Navigation] Dashboard transition completed in {elapsedMs:F2}ms.", context);
        }, "Failed transitioning to Server Dashboard.");
    }

    // Returning Task<bool> directly matches ExecuteSafeAsync concrete type, resolving CA1859
    private Task<bool> OnSwitchProtocolAsync(ServerProfile profile, RconProtocol newProtocol)
    {
        var start = Stopwatch.GetTimestamp();
        var oldProtocol = profile.Protocol;
        var context = new Dictionary<string, object?>
        {
            ["endpoint"] = profile.FormattedEndpoint,
            ["old_protocol"] = oldProtocol.ToString(),
            ["new_protocol"] = newProtocol.ToString()
        };

        AppLogger.Info($"[MainViewModel:SwitchProtocol] Switching protocol from {oldProtocol} to {newProtocol} for {profile.FormattedEndpoint}...", context);

        return ExecuteSafeAsync(async () =>
        {
            profile.Protocol = newProtocol;

            try
            {
                var profiles = ProfileStorageService.LoadProfilesFast();
                if (profiles.FirstOrDefault(p => p.Id == profile.Id || (p.ServerIp == profile.ServerIp && p.Port == profile.Port)) is { } match)
                {
                    match.Protocol = newProtocol;
                    ProfileStorageService.SaveProfilesFast(profiles);
                    AppLogger.Debug("[MainViewModel:SwitchProtocol] Updated saved profile protocol on disk.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[MainViewModel:SwitchProtocol] Profile update notice: {ex.Message}");
            }

            var newRconService = new RconService();
            bool success = await newRconService.ConnectAsync(profile).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            if (success)
            {
                AppLogger.Info($"[MainViewModel:SwitchProtocol] Successfully connected with new protocol {newProtocol} in {elapsedMs:F2}ms.");
                Dispatcher.UIThread.Post(() => OnLoginSuccess(profile, newRconService));
            }
            else
            {
                AppLogger.Warn($"[MainViewModel:SwitchProtocol] Connection with new protocol {newProtocol} failed after {elapsedMs:F2}ms. Returning to login.");
                ToastNotificationService.Instance.ShowError("Protocol Switch Failed", $"Could not reconnect to {profile.FormattedEndpoint} via {newProtocol}: {newRconService.LastConnectionError}");
                Dispatcher.UIThread.Post(OnDisconnect);
            }
        }, "Failed switching server connection protocol.");
    }

    private void OnDisconnect()
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info($"[MainViewModel:Navigation] Disconnect reported. Returning to LoginView from {CurrentView.GetType().Name}...");

        ExecuteSafe(() =>
        {
            var oldView = CurrentView;
            CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: false);

            AppLogger.TrackEvent("navigation_view_changed", new Dictionary<string, object>
            {
                ["from_view"] = oldView.GetType().Name,
                ["to_view"] = nameof(LoginViewModel)
            });

            if (oldView is IDisposable disposableOldView)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        var disposeStart = Stopwatch.GetTimestamp();
                        disposableOldView.Dispose();
                        AppLogger.Debug($"[MainViewModel:Navigation] Disposed previous view: {disposableOldView.GetType().Name} in {Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds:F2}ms.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"[MainViewModel:Navigation] Error disposing old view ({disposableOldView.GetType().Name}): {ex.Message}", ex);
                    }
                }, DispatcherPriority.Background);
            }

            AppLogger.Info($"[MainViewModel:Navigation] LoginView transition completed in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }, "Failed returning to server login screen.");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                var start = Stopwatch.GetTimestamp();
                AppLogger.Debug("[MainViewModel:Dispose] Unhooking CrashReportService event handlers and disposing child views...");

                CrashReportService.UnhandledErrorCaptured -= OnUnhandledErrorCaptured;

                if (CurrentView is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                        AppLogger.Debug("[MainViewModel:Dispose] Disposed active CurrentView.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"[MainViewModel:Dispose] Error disposing CurrentView ({CurrentView.GetType().Name}): {ex.Message}", ex);
                    }
                }

                AppLogger.Trace($"[MainViewModel:Dispose] Disposed in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            }
            _isDisposed = true;
        }
    }
}