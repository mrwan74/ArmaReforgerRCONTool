using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class BanImportPreviewViewModel : ViewModelBase
{
    private readonly IRconService _rconService;
    private readonly DashboardViewModel _dashboard;
    private readonly Action _onClose;

    [ObservableProperty] public partial ObservableCollection<ParsedImportBanItem> ImportItems { get; set; }
    [ObservableProperty] public partial int TotalParsedCount { get; set; }
    [ObservableProperty] public partial int NewBansCount { get; set; }
    [ObservableProperty] public partial int DuplicateCount { get; set; }
    [ObservableProperty] public partial int SelectedToImportCount { get; set; }
    [ObservableProperty] public partial bool IsExecuting { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;

    public BanImportPreviewViewModel(
        List<ParsedImportBanItem> parsedItems,
        IRconService rconService,
        DashboardViewModel dashboard,
        Action onClose)
    {
        var start = Stopwatch.GetTimestamp();
        ArgumentNullException.ThrowIfNull(parsedItems);
        ArgumentNullException.ThrowIfNull(rconService);
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(onClose);

        _rconService = rconService;
        _dashboard = dashboard;
        _onClose = onClose;

        ImportItems = new ObservableCollection<ParsedImportBanItem>(parsedItems);
        TotalParsedCount = parsedItems.Count;
        DuplicateCount = parsedItems.Count(p => p.IsDuplicate);
        NewBansCount = parsedItems.Count(p => !p.IsDuplicate);
        UpdateSelectedCount();

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[BanImportPreview:Init] View model initialized in {elapsedMs:F2}ms (TotalParsed={TotalParsedCount}, New={NewBansCount}, Duplicates={DuplicateCount}, Selected={SelectedToImportCount}, Protocol={_rconService.CurrentProtocol}, Thread=T{Environment.CurrentManagedThreadId:D2}).");
    }

    [RelayCommand]
    public void ToggleItemSelection(ParsedImportBanItem? item)
    {
        if (IsExecuting || item == null) return;

        ExecuteSafe(() =>
        {
            item.IsSelected = !item.IsSelected;
            UpdateSelectedCount();
            AppLogger.Debug($"[BanImportPreview:Selection] Toggled item '{item.Identity}' (IsSelected={item.IsSelected}, IsDuplicate={item.IsDuplicate}). Total selected: {SelectedToImportCount}/{TotalParsedCount}.");
        }, "Failed toggling ban selection.");
    }

    [RelayCommand]
    public void SelectAllNew()
    {
        if (IsExecuting) return;

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            int newlySelected = 0;
            foreach (var item in ImportItems)
            {
                item.IsSelected = !item.IsDuplicate;
                if (item.IsSelected) newlySelected++;
            }
            UpdateSelectedCount();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[BanImportPreview:SelectAllNew] Selected {newlySelected} new items in {elapsedMs:F2}ms. Total selected: {SelectedToImportCount}.");
        }, "Failed selecting new ban entries.");
    }

    [RelayCommand]
    public void SelectAll()
    {
        if (IsExecuting) return;

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            foreach (var item in ImportItems)
            {
                item.IsSelected = true;
            }
            UpdateSelectedCount();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[BanImportPreview:SelectAll] Selected all {ImportItems.Count} items in {elapsedMs:F2}ms.");
        }, "Failed selecting all items.");
    }

    [RelayCommand]
    public void DeselectAll()
    {
        if (IsExecuting) return;

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            foreach (var item in ImportItems)
            {
                item.IsSelected = false;
            }
            UpdateSelectedCount();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[BanImportPreview:DeselectAll] Cleared all selections in {elapsedMs:F2}ms.");
        }, "Failed deselecting items.");
    }

    private void UpdateSelectedCount()
    {
        SelectedToImportCount = ImportItems.Count(i => i.IsSelected);
    }

    [RelayCommand]
    private Task<bool> ConfirmImportAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsExecuting)
        {
            AppLogger.Warn("[BanImportPreview:Execute] ConfirmImportAsync called while already executing. Aborting duplicate invocation.");
            return;
        }

        var selected = ImportItems.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            AppLogger.Warn("[BanImportPreview:Execute] User confirmed import with zero items selected.");
            ToastNotificationService.Instance.ShowWarning("No Bans Selected", "Select at least one ban record to import.");
            return;
        }

        IsExecuting = true;
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"BanImportPreviewViewModel.ConfirmImportAsync({selected.Count} bans)");
        int successCount = 0;
        int failedCount = 0;

        var context = new Dictionary<string, object?>
        {
            ["count"] = selected.Count,
            ["protocol"] = _rconService.CurrentProtocol.ToString(),
            ["server_endpoint"] = $"{_dashboard.Profile.ServerIp}:{_dashboard.Profile.Port}",
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.TrackEvent("ban_import_started", new Dictionary<string, object>
        {
            ["count"] = selected.Count,
            ["protocol"] = _rconService.CurrentProtocol.ToString(),
            ["server_endpoint"] = $"{_dashboard.Profile.ServerIp}:{_dashboard.Profile.Port}"
        });

        try
        {
            int total = selected.Count;
            AppLogger.Info($"[BanImportPreview:Execute] Starting batch execution for {total} selected ban(s) via protocol {_rconService.CurrentProtocol}...", context);

            for (int i = 0; i < total; i++)
            {
                var item = selected[i];
                var itemIndex = i + 1;
                var itemStart = Stopwatch.GetTimestamp();

                await Dispatcher.UIThread.InvokeAsync(() => ProgressStatus = $"Importing {itemIndex}/{total}: {item.Identity}...");

                try
                {
                    AppLogger.Debug($"[BanImportPreview:Execute] [{itemIndex}/{total}] Dispatching ban for '{item.Identity}' (DurationSeconds: {item.DurationSeconds}, IsIP: {item.IsIpAddress}, Reason: '{item.Reason}')...");

                    bool success = await _rconService.OfflineBanAsync(
                        item.Identity,
                        item.DurationSeconds,
                        item.Reason,
                        item.IsIpAddress).ConfigureAwait(false);

                    var itemElapsedMs = Stopwatch.GetElapsedTime(itemStart).TotalMilliseconds;

                    if (success)
                    {
                        successCount++;
                        AppLogger.Info($"[BanImportPreview:Execute] [{itemIndex}/{total}] SUCCESS: Ban added for '{item.Identity}' in {itemElapsedMs:F2}ms.");
                    }
                    else
                    {
                        failedCount++;
                        AppLogger.Warn($"[BanImportPreview:Execute] [{itemIndex}/{total}] FAILED: Server rejected offline ban for '{item.Identity}' in {itemElapsedMs:F2}ms.");
                    }
                }
                catch (Exception banEx)
                {
                    failedCount++;
                    var itemElapsedMs = Stopwatch.GetElapsedTime(itemStart).TotalMilliseconds;
                    AppLogger.Error($"[BanImportPreview:Execute] [{itemIndex}/{total}] EXCEPTION during ban of '{item.Identity}' after {itemElapsedMs:F2}ms: {banEx.Message}", banEx);
                }

                await Task.Delay(35).ConfigureAwait(false);
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            var throughput = elapsedMs > 0 ? total / (elapsedMs / 1000.0) : 0;
            context["success_count"] = successCount;
            context["failed_count"] = failedCount;
            context["throughput_per_sec"] = throughput;
            context["elapsed_ms"] = elapsedMs;

            AppLogger.Info($"[BanImportPreview:Execute] Batch complete in {elapsedMs:F2}ms ({throughput:F1} bans/sec). Success: {successCount}, Failed: {failedCount}.", context);

            if (_rconService.CurrentProtocol == RconProtocol.BattlEye)
            {
                AppLogger.Info("[BanImportPreview:Execute] Dispatching BattlEye cache flush commands ('writeBans', 'loadBans')...");
                var syncStart = Stopwatch.GetTimestamp();
                try
                {
                    await _rconService.SendCommandAsync("writeBans").ConfigureAwait(false);
                    await Task.Delay(50).ConfigureAwait(false);
                    await _rconService.SendCommandAsync("loadBans").ConfigureAwait(false);
                    var syncElapsedMs = Stopwatch.GetElapsedTime(syncStart).TotalMilliseconds;
                    AppLogger.Info($"[BanImportPreview:Execute] BattlEye ban file synchronization complete in {syncElapsedMs:F2}ms.");
                }
                catch (Exception syncEx)
                {
                    AppLogger.Error($"[BanImportPreview:Execute] BattlEye post-import cache synchronization failed: {syncEx.Message}", syncEx);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _onClose();

                if (failedCount == 0)
                {
                    ToastNotificationService.Instance.ShowSuccess(
                        "Ban Import Complete",
                        $"Successfully imported all {successCount} ban(s) to server."
                    );
                }
                else
                {
                    ToastNotificationService.Instance.ShowWarning(
                        "Ban Import Partial",
                        $"Imported {successCount} ban(s), {failedCount} failed. Check log for details."
                    );
                }
            });

            AppLogger.TrackEvent("ban_import_completed", new Dictionary<string, object>
            {
                ["total"] = total,
                ["success_count"] = successCount,
                ["failed_count"] = failedCount,
                ["duration_ms"] = elapsedMs
            });

            AppLogger.Debug("[BanImportPreview:Execute] Triggering background refresh of active server ban table...");
            await _dashboard.BansTab.RefreshBansAsync().ConfigureAwait(false);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsExecuting = false;
                ProgressStatus = string.Empty;
            });
        }
    }, "Failed executing ban import.");

    [RelayCommand]
    private void Close()
    {
        if (IsExecuting)
        {
            AppLogger.Warn("[BanImportPreview:Close] Close command ignored while batch import is executing.");
            return;
        }

        AppLogger.Debug("[BanImportPreview:Close] Dialog dismissed by user.");
        _onClose();
    }
}