using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class GeoIpUpdateDialogViewModel : ViewModelBase, IDisposable
{
    private readonly Action _onClose;
    private readonly CancellationTokenSource _cts = new();
    private bool _isDisposed;

    [ObservableProperty] public partial string CurrentStatusTitle { get; set; } = "Initializing...";
    [ObservableProperty] public partial double ProgressPercentage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionButtonText))]
    public partial bool IsInProgress { get; set; } = true;

    [ObservableProperty] public partial bool IsFinished { get; set; }
    [ObservableProperty] public partial bool HasError { get; set; }

    public string ActionButtonText => IsInProgress ? "Cancel" : "Done";

    [ObservableProperty] public partial string CityStatusText { get; set; } = "Waiting...";
    [ObservableProperty] public partial MaterialIconKind CityIconKind { get; set; } = MaterialIconKind.ClockOutline;

    [ObservableProperty] public partial string CountryStatusText { get; set; } = "Waiting...";
    [ObservableProperty] public partial MaterialIconKind CountryIconKind { get; set; } = MaterialIconKind.ClockOutline;

    [ObservableProperty] public partial ObservableCollection<string> ActivityLogs { get; set; } = [];

    public GeoIpUpdateDialogViewModel(Action onClose)
    {
        _onClose = onClose;
        AppLogger.Debug("[GeoIpUpdateDialogViewModel:Init] Instantiated dialog. Launching update task...");
        _ = RunUpdateAsync();
    }

    private async Task RunUpdateAsync()
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info("[GeoIpUpdateDialog:Update] Initiating MaxMind GeoIP update task...");

        var progressHandler = new Progress<GeoIpProgressReport>(report =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                CurrentStatusTitle = report.CurrentOperation;
                ProgressPercentage = report.OverallProgressPercentage;

                if (!string.IsNullOrWhiteSpace(report.DetailLog))
                {
                    var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
                    ActivityLogs.Add($"[{timestamp}] {report.DetailLog}");
                    AppLogger.Trace($"[GeoIpUpdateDialog:Progress] [{report.OverallProgressPercentage:F0}%] {report.DetailLog}");
                }

                UpdateStepState(report.CityStatus, report.CityStatusMessage, k => CityIconKind = k, s => CityStatusText = s);
                UpdateStepState(report.CountryStatus, report.CountryStatusMessage, k => CountryIconKind = k, s => CountryStatusText = s);
            });
        });

        try
        {
            bool success = await GeoIpService.UpdateDatabasesAsync(force: true, progressHandler, _cts.Token).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            Dispatcher.UIThread.Post(() =>
            {
                IsInProgress = false;
                IsFinished = true;
                HasError = !success && !_cts.IsCancellationRequested;

                if (success)
                {
                    CurrentStatusTitle = "Databases Updated Successfully";
                    AppLogger.Info($"[GeoIpUpdateDialog:Update] MaxMind GeoIP databases updated successfully in {elapsedMs:F2}ms.");
                }
                else if (_cts.IsCancellationRequested)
                {
                    CurrentStatusTitle = "Update Canceled";
                    AppLogger.Info($"[GeoIpUpdateDialog:Update] Update was cancelled by operator after {elapsedMs:F2}ms.");
                }
                else
                {
                    CurrentStatusTitle = "Update Completed with Warnings";
                    AppLogger.Warn($"[GeoIpUpdateDialog:Update] GeoIP update completed with warnings after {elapsedMs:F2}ms. Inspect activity log.");
                }

                ProgressPercentage = 100;
            });
        }
        catch (OperationCanceledException opEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[GeoIpUpdateDialog:Update] Operation cancelled after {elapsedMs:F2}ms: {opEx.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                IsInProgress = false;
                IsFinished = true;
                CurrentStatusTitle = "Update Canceled by Operator";
                ActivityLogs.Add($"[{DateTime.Now:HH:mm:ss.fff}] Cancelled by operator.");
            });
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Error($"[GeoIpUpdateDialog:Update] Fatal error updating GeoIP databases after {elapsedMs:F2}ms: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("GeoIP Update Failed", $"Fatal error updating MaxMind databases: {ex.Message}");
            Dispatcher.UIThread.Post(() =>
            {
                IsInProgress = false;
                IsFinished = true;
                HasError = true;
                CurrentStatusTitle = "Update Failed";
                ActivityLogs.Add($"[{DateTime.Now:HH:mm:ss.fff}] Fatal error: {ex.Message}");
            });
        }
    }

    private static void UpdateStepState(
        GeoIpStepStatus status,
        string message,
        Action<MaterialIconKind> setIcon,
        Action<string> setMessage)
    {
        setMessage(message);
        setIcon(status switch
        {
            GeoIpStepStatus.InProgress => MaterialIconKind.ProgressDownload,
            GeoIpStepStatus.Completed => MaterialIconKind.CheckCircleOutline,
            GeoIpStepStatus.Skipped => MaterialIconKind.CheckAll,
            GeoIpStepStatus.Failed => MaterialIconKind.AlertCircleOutline,
            _ => MaterialIconKind.ClockOutline
        });
    }

    [RelayCommand]
    private void CancelOrClose()
    {
        if (IsInProgress)
        {
            try
            {
                AppLogger.Warn("[GeoIpUpdateDialog:Cancel] Cancellation requested by operator.");
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Disposed CTS safely bypassed
            }
        }
        else
        {
            AppLogger.Debug("[GeoIpUpdateDialog:Close] Dialog dismissed.");
            _onClose();
        }
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var start = Stopwatch.GetTimestamp();
        var text = string.Join(Environment.NewLine, ActivityLogs);
        await ClipboardService.SetTextAsync(text, CancellationToken.None).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[GeoIpUpdateDialog:Clipboard] Copied {ActivityLogs.Count} update log line(s) ({text.Length} chars) in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Copied", "Copied update activity log to clipboard.");
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
                AppLogger.Debug("[GeoIpUpdateDialog:Dispose] Disposing dialog CTS...");
                _cts.Dispose();
            }
            _isDisposed = true;
        }
    }
}