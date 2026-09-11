using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class UpdateDialogViewModel : ViewModelBase, IDisposable
{
    private const string ElapsedMsKey = "elapsed_ms";
    private const string ThreadIdKey = "thread_id";

    private readonly Action _onClose;
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;

    [ObservableProperty] public partial string CurrentVersion { get; set; }
    [ObservableProperty] public partial string LatestVersion { get; set; }
    [ObservableProperty] public partial string ReleaseNotesUrl { get; set; }
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial bool IsReadyToRestart { get; set; }
    [ObservableProperty] public partial bool HasFailed { get; set; }
    [ObservableProperty] public partial int DownloadProgress { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "An update is ready to download.";
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    public UpdateDialogViewModel(string currentVersion, string latestVersion, Action onClose)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        CurrentVersion = currentVersion;
        LatestVersion = latestVersion;
        _onClose = onClose;

        var cleanTag = latestVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? latestVersion : $"v{latestVersion}";
        ReleaseNotesUrl = $"{UpdateService.GitHubRepoUrl}/releases/tag/{cleanTag}";

        var context = new Dictionary<string, object?>
        {
            ["current_version"] = CurrentVersion,
            ["latest_version"] = LatestVersion,
            ["release_notes_url"] = ReleaseNotesUrl,
            [ThreadIdKey] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[UpdateDialog:Init] Initializing UpdateDialogViewModel for v{CurrentVersion} -> v{LatestVersion}...", context);

        try
        {
            UpdateService.Instance.StateChanged += OnUpdateServiceStateChanged;
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[UpdateDialog:Init] Dialog ViewModel ready in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UpdateDialog:Init] Failed attaching UpdateService StateChanged listener: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowWarning("Update Dialog Notice", "Failed to bind update status listener.");
        }
    }

    private void OnUpdateServiceStateChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                DownloadProgress = UpdateService.Instance.DownloadPercentage;
                StatusText = UpdateService.Instance.StatusDetails;
                AppLogger.Trace($"[UpdateDialog:Progress] Progress updated: {DownloadProgress}% - '{StatusText}' (Thread={Environment.CurrentManagedThreadId})");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[UpdateDialog:Progress] Exception updating progress UI properties: {ex.Message}", ex);
            }
        });
    }

    [RelayCommand]
    private async Task OpenReleaseNotesAsync()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["url"] = ReleaseNotesUrl,
            [ThreadIdKey] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[UpdateDialog:ReleaseNotes] Launching release notes URL in system browser: '{ReleaseNotesUrl}'...", context);

        try
        {
            bool success = await UrlLauncherService.OpenUrlAsync(ReleaseNotesUrl).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context["success"] = success;

            if (success)
            {
                AppLogger.Debug($"[UpdateDialog:ReleaseNotes] Default browser launched successfully in {elapsedMs:F2}ms.", context);
            }
            else
            {
                AppLogger.Warn("[UpdateDialog:ReleaseNotes] Browser launch failed; fallback clipboard invoked.", null, context);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UpdateDialog:ReleaseNotes] Exception launching release notes: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Browser Launch Failed", "Unable to open release notes: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task StartUpdateAsync()
    {
        if (IsDownloading || IsReadyToRestart)
        {
            AppLogger.Warn($"[UpdateDialog:StartUpdate] Update invocation rejected: invalid state (IsDownloading={IsDownloading}, IsReady={IsReadyToRestart}).");
            return;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["target_version"] = LatestVersion,
            ["current_version"] = CurrentVersion,
            [ThreadIdKey] = Environment.CurrentManagedThreadId
        };

        IsDownloading = true;
        HasFailed = false;
        ErrorMessage = string.Empty;
        StatusText = "Connecting and downloading update package...";

        AppLogger.Info($"[UpdateDialog:StartUpdate] User initiated download workflow for v{LatestVersion}...", context);

        try
        {
            bool success = await UpdateService.Instance.DownloadUpdateAsync(_cts.Token).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context["success"] = success;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsDownloading = false;
                if (success)
                {
                    IsReadyToRestart = true;
                    StatusText = $"Version v{LatestVersion} downloaded and verified successfully.";
                    AppLogger.Info($"[UpdateDialog:StartUpdate] Update package v{LatestVersion} verified and ready for restart in {elapsedMs:F2}ms.", context);
                }
                else
                {
                    HasFailed = true;
                    ErrorMessage = "Failed to download update package. Check your network connection and retry.";
                    AppLogger.Warn($"[UpdateDialog:StartUpdate] Download returned false after {elapsedMs:F2}ms.", null, context);
                }
            }, DispatcherPriority.Normal, _cts.Token);
        }
        catch (OperationCanceledException opEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            AppLogger.Info($"[UpdateDialog:StartUpdate] Download operation was cancelled by operator after {elapsedMs:F2}ms: {opEx.Message}", context);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsDownloading = false;
                StatusText = "Download cancelled by user.";
            }, DispatcherPriority.Normal, CancellationToken.None);
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            context[ElapsedMsKey] = elapsedMs;
            context["exception_type"] = ex.GetType().FullName;
            context["error_message"] = ex.Message;
            context["stack_trace"] = ex.StackTrace;

            AppLogger.Fatal($"[UpdateDialog:StartUpdate] Fatal exception downloading update package after {elapsedMs:F2}ms: {ex.Message}", ex, context);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsDownloading = false;
                HasFailed = true;
                ErrorMessage = $"Download error: {ex.Message}";
                ToastNotificationService.Instance.ShowError("Download Error", $"Failed downloading update: {ex.Message}");
            }, DispatcherPriority.Normal, CancellationToken.None);
        }
    }

    [RelayCommand]
    private void RestartAndApply()
    {
        var context = new Dictionary<string, object?>
        {
            ["target_version"] = LatestVersion,
            ["current_version"] = CurrentVersion,
            [ThreadIdKey] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[UpdateDialog:Restart] Administrator confirmed application restart for v{LatestVersion}.", context);

        try
        {
            UpdateService.Instance.RestartAndApply();
        }
        catch (Exception ex)
        {
            context["exception_type"] = ex.GetType().FullName;
            context["error_message"] = ex.Message;
            context["stack_trace"] = ex.StackTrace;

            AppLogger.Fatal($"[UpdateDialog:Restart] Critical failure executing restart: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Restart Failed", $"Failed executing application restart: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Close()
    {
        var context = new Dictionary<string, object?>
        {
            ["is_downloading"] = IsDownloading,
            ["is_ready_to_restart"] = IsReadyToRestart,
            [ThreadIdKey] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info("[UpdateDialog:Close] User dismissed the update dialog (will prompt again on subsequent launch).", context);

        try
        {
            if (IsDownloading)
            {
                AppLogger.Debug("[UpdateDialog:Close] Cancelling active background download token...");
                _cts.Cancel();
            }

            _onClose();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UpdateDialog:Close] Exception during dialog dismissal: {ex.Message}", ex, context);
        }
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
                try
                {
                    UpdateService.Instance.StateChanged -= OnUpdateServiceStateChanged;
                    _cts.Dispose();
                    var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    AppLogger.Debug($"[UpdateDialogViewModel:Dispose] Disposed event subscriptions and CTS in {elapsedMs:F2}ms.");
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"[UpdateDialogViewModel:Dispose] Exception during teardown: {ex.Message}", ex);
                }
            }
            _isDisposed = true;
        }
    }
}