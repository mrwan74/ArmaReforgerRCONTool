using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using Sentry;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ReforgerRcon.ViewModels;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Instance property for XAML data binding")]
[SuppressMessage("Minor Code Smell", "S1125:Boolean literals should not be redundant", Justification = "Nullable boolean comparison")]
[SuppressMessage("Style", "IDE0044:Add readonly modifier", Justification = "Fields are configured as readonly where applicable")]
public partial class BansViewModel : ViewModelBase
{
    public const string DefaultSortKey = "Default";
    private const string ProtocolTelemetryKey = "protocol";

    private readonly IRconService _rconService;
    private readonly DashboardViewModel _dashboard;
    private readonly List<BanModel> _allBans = [];
    private bool _isUpdatingSelection;
    private bool _isInitializing;

    [ObservableProperty] public partial ObservableCollection<BanModel> Bans { get; set; } = [];
    [ObservableProperty] public partial BanModel? SelectedBan { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial int SelectedCount { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomFetchMode))]
    public partial string SelectedFetchMode { get; set; }

    [ObservableProperty]
    public partial int CustomPageLimit { get; set; }

    public bool IsCustomFetchMode => SelectedFetchMode == "Custom Limit";

    public ObservableCollection<string> FetchModes { get; } =
    [
        "All Pages",
        "First Page Only",
        "Custom Limit"
    ];

    private bool? _isAllSelected = false;
    public bool? IsAllSelected
    {
        get => _isAllSelected;
        set
        {
            if (SetProperty(ref _isAllSelected, value))
            {
                OnPropertyChanged(nameof(SelectAllTooltipText));
                OnPropertyChanged(nameof(SelectAllButtonText));

                if (!_isUpdatingSelection && value.HasValue)
                {
                    ApplySelectAll(value.Value);
                }
            }
        }
    }

    public string SelectAllTooltipText => IsAllSelected is true
        ? "Click to deselect all"
        : "Click to select all (Ctrl+A)";

    public string SelectAllButtonText => IsAllSelected is true
        ? "Deselect All"
        : "Select All";

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CurrentSortField => _dashboard.SettingsTab.Settings.BansSortBy;
    public bool CurrentSortAscending => _dashboard.SettingsTab.Settings.BansSortAscending;

    public BansViewModel(IRconService rconService, DashboardViewModel dashboard)
    {
        _rconService = rconService;
        _dashboard = dashboard;

        _isInitializing = true;
        try
        {
            var settings = dashboard.SettingsTab.Settings;
            SelectedFetchMode = !string.IsNullOrWhiteSpace(settings.ReforgerBanFetchMode) ? settings.ReforgerBanFetchMode : "All Pages";
            CustomPageLimit = Math.Clamp(settings.ReforgerBanCustomPageLimit > 0 ? settings.ReforgerBanCustomPageLimit : 3, 1, 50);
            AppLogger.Debug($"[BansViewModel:Init] Initialized pagination settings: Mode='{SelectedFetchMode}', CustomLimit={CustomPageLimit}.");
        }
        finally
        {
            _isInitializing = false;
        }
    }

    [RelayCommand]
    public void OpenPaginationHelp()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[BansViewModel:Help] Displaying pagination help dialog...");
            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Reforger Ban List Pagination",
                "According to Bohemia Reforger documentation, the server returns bans in paginated batches of 25 records via '#ban list [page]'.\n\n" +
                "• All Pages: Programmatically requests each page sequentially until all active bans on the server are retrieved.\n\n" +
                "• First Page Only: Fetches only the first 25 bans (fastest, minimal server traffic).\n\n" +
                "• Custom Limit: Fetches sequentially up to your specified number of pages (e.g. 3 pages = up to 75 bans).",
                "Got It",
                false,
                () => Task.CompletedTask,
                () => _dashboard.CloseDialog()
            ));
        }, "Failed opening pagination help.");
    }

    partial void OnSelectedFetchModeChanged(string value)
    {
        if (_isInitializing) return;

        AppLogger.Info($"[BansViewModel:FetchMode] Reforger ban fetch mode changed to: '{value}'");
        var settings = _dashboard.SettingsTab.Settings;
        settings.ReforgerBanFetchMode = value;
        _ = _dashboard.SettingsTab.SaveSettingsAsync(showToast: false);
    }

    partial void OnCustomPageLimitChanged(int value)
    {
        if (_isInitializing) return;

        var clamped = Math.Clamp(value, 1, 50);
        AppLogger.Info($"[BansViewModel:FetchLimit] Reforger custom page limit set to: {clamped}");
        var settings = _dashboard.SettingsTab.Settings;
        settings.ReforgerBanCustomPageLimit = clamped;
        _ = _dashboard.SettingsTab.SaveSettingsAsync(showToast: false);
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        AppLogger.Debug($"[BansViewModel:MultiSelect] Mode changed to: {value} on thread T{Environment.CurrentManagedThreadId:D2}.");
        if (!value)
        {
            foreach (var b in Bans) b.IsSelected = false;
            UpdateSelectedState();
        }
    }

    [RelayCommand]
    public void ToggleSelectAll() => ExecuteSafe(() =>
    {
        if (Bans.Count == 0) return;
        if (!IsMultiSelectMode) IsMultiSelectMode = true;

        bool targetState = IsAllSelected is not true;
        AppLogger.Info($"[BansViewModel:SelectAll] ToggleSelectAll triggered: targetState={targetState} for {Bans.Count} rows.");
        ApplySelectAll(targetState);
    });

    private void ApplySelectAll(bool isSelected)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplySelectAll(isSelected));
            return;
        }

        ExecuteSafe(() =>
        {
            _isUpdatingSelection = true;
            try
            {
                foreach (var b in Bans)
                {
                    b.IsSelected = isSelected;
                }
                SelectedCount = isSelected ? Bans.Count : 0;
                IsAllSelected = isSelected;
                AppLogger.Debug($"[BansViewModel:SelectAll] Applied isSelected={isSelected} ({SelectedCount}/{Bans.Count} items selected).");
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        });
    }

    [RelayCommand]
    public Task<bool> RefreshBansAsync() => ExecuteSafeAsync(async () =>
    {
        await Dispatcher.UIThread.InvokeAsync(() => IsLoading = true);
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BansViewModel.RefreshBansAsync");

        try
        {
            int maxPages = SelectedFetchMode switch
            {
                "First Page Only" => 1,
                "Custom Limit" => Math.Max(1, CustomPageLimit),
                _ => 0
            };

            AppLogger.Debug($"[BansViewModel:Refresh] Requesting active ban list from server ({_rconService.CurrentProtocol}, FetchMode='{SelectedFetchMode}', MaxPages={maxPages})...");

            var selectedIdentities = new HashSet<string>(
                _allBans.Where(b => b.IsSelected).Select(b => b.IdentityId), StringComparer.OrdinalIgnoreCase);
            var selectedBanNos = new HashSet<int>(
                _allBans.Where(b => b.IsSelected).Select(b => b.BanNumber));
            bool wasAllSelected = IsAllSelected is true;

            var fetchedBans = await _rconService.GetBansAsync(maxPages).ConfigureAwait(false);
            _allBans.Clear();
            _allBans.AddRange(fetchedBans);

            foreach (var b in _allBans.Where(b => wasAllSelected || selectedBanNos.Contains(b.BanNumber) || selectedIdentities.Contains(b.IdentityId)))
            {
                b.IsSelected = true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
                _dashboard.ActiveBansCount = _allBans.Count;
                UpdateSelectedState();
            });

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[BansViewModel:Refresh] Loaded {_allBans.Count} ban records ({Bans.Count} visible, {SelectedCount} selected) in {elapsedMs:F2}ms.");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }, "Failed to refresh server bans from RCON.");

    public static string MapColumnTagToSortField(string? tag)
    {
        return tag switch
        {
            "ColReforgerIdentity" or "ColBeIdentity" => "GUID / IP Address",
            "ColBeMinutes" => "Minutes Left",
            "ColBeReason" => "Reason",
            "ColBeBanNo" or "ColReforgerBannedName" => DefaultSortKey,
            _ => string.Empty
        };
    }

    public void CycleColumnSort(string columnTag)
    {
        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var mappedField = MapColumnTagToSortField(columnTag);
            if (string.IsNullOrEmpty(mappedField)) return;

            var currentField = _dashboard.SettingsTab.Settings.BansSortBy;
            var currentAsc = _dashboard.SettingsTab.Settings.BansSortAscending;

            if (string.Equals(currentField, mappedField, StringComparison.OrdinalIgnoreCase))
            {
                if (currentAsc)
                {
                    _dashboard.SettingsTab.Settings.BansSortAscending = false;
                    AppLogger.Info($"[BansViewModel:Sort] Cycled sort for '{mappedField}' -> Descending.");
                }
                else
                {
                    _dashboard.SettingsTab.Settings.BansSortBy = DefaultSortKey;
                    _dashboard.SettingsTab.Settings.BansSortAscending = true;
                    AppLogger.Info($"[BansViewModel:Sort] Cycled sort for '{mappedField}' -> Default (raw server order).");
                }
            }
            else
            {
                _dashboard.SettingsTab.Settings.BansSortBy = mappedField;
                _dashboard.SettingsTab.Settings.BansSortAscending = true;
                AppLogger.Info($"[BansViewModel:Sort] Cycled sort column -> '{mappedField}' (Ascending).");
            }

            ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            AppLogger.Debug($"[BansViewModel:Sort] Column sort cycle complete in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }, "Failed to cycle ban column sort order.");
    }

    public void RemoveBanFromList(BanModel ban)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RemoveBanFromList(ban));
            return;
        }

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            int removed = _allBans.RemoveAll(b => b.IdentityId == ban.IdentityId || (b.BanNumber == ban.BanNumber && b.BanNumber != 0));

            var match = Bans.FirstOrDefault(b => b.IdentityId == ban.IdentityId || (b.BanNumber == ban.BanNumber && b.BanNumber != 0));
            if (match != null)
            {
                match.PropertyChanged -= OnBanPropertyChanged;
                Bans.Remove(match);
            }

            _dashboard.ActiveBansCount = _allBans.Count;
            UpdateSelectedState();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[BansViewModel:Remove] Removed ban #{ban.BanNumber} ({ban.IdentityId}) from UI list in {elapsedMs:F2}ms (Purged: {removed}, Remaining: {Bans.Count}).");
        }, "Failed to remove ban from visual list.");
    }

    public void ApplyFilter(string query, string searchType)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyFilter(query, searchType));
            return;
        }

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            using var timing = AppLogger.Measure($"BansViewModel.ApplyFilter('{query}', '{searchType}')");

            foreach (var b in Bans)
            {
                b.PropertyChanged -= OnBanPropertyChanged;
            }

            IEnumerable<BanModel> filtered = _allBans;

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = searchType switch
                {
                    "Name" => _allBans.Where(b => b.BannedName.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "UID" => _allBans.Where(b => b.IdentityId.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    _ => _allBans.Where(b =>
                        b.BannedName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        b.IdentityId.Contains(query, StringComparison.OrdinalIgnoreCase))
                };
            }

            var sortField = _dashboard.SettingsTab.Settings.BansSortBy;
            var isAscending = _dashboard.SettingsTab.Settings.BansSortAscending;

            if (!string.Equals(sortField, DefaultSortKey, StringComparison.OrdinalIgnoreCase))
            {
                filtered = sortField switch
                {
                    "GUID / IP Address" => isAscending
                        ? filtered.OrderBy(b => b.IdentityId)
                        : filtered.OrderByDescending(b => b.IdentityId),
                    "Minutes Left" => isAscending
                        ? filtered.OrderBy(b => b.DurationSeconds)
                        : filtered.OrderByDescending(b => b.DurationSeconds),
                    "Reason" => isAscending
                        ? filtered.OrderBy(b => b.Reason)
                        : filtered.OrderByDescending(b => b.Reason),
                    _ => filtered
                };
            }

            Bans = new ObservableCollection<BanModel>(filtered);

            foreach (var b in Bans)
            {
                b.PropertyChanged += OnBanPropertyChanged;
            }

            _dashboard.ActiveBansCount = _allBans.Count;
            UpdateSelectedState();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Trace($"[BansViewModel:Filter] Filtered {Bans.Count}/{_allBans.Count} bans in {elapsedMs:F2}ms (Query='{query}', Sort='{sortField}', Asc={isAscending}).");
        }, "Failed to apply ban search filter.");
    }

    private void OnBanPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (e.PropertyName == nameof(BanModel.IsSelected))
        {
            UpdateSelectedState();
        }
    }

    private void UpdateSelectedState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateSelectedState);
            return;
        }

        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            SelectedCount = Bans.Count(b => b.IsSelected);

            bool? newSelectionState;
            if (Bans.Count == 0 || SelectedCount == 0)
            {
                newSelectionState = false;
            }
            else if (SelectedCount == Bans.Count)
            {
                newSelectionState = true;
            }
            else
            {
                newSelectionState = null;
            }

            if (_isAllSelected != newSelectionState)
            {
                _isUpdatingSelection = true;
                try
                {
                    IsAllSelected = newSelectionState;
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            }
        }, "Failed to update ban selection state.");
    }

    [RelayCommand]
    public void RemoveBan(BanModel? ban)
    {
        ExecuteSafe(() =>
        {
            ban ??= SelectedBan;
            if (ban == null)
            {
                AppLogger.Warn("[BansViewModel:RemoveBan] RemoveBan invoked with null target.");
                ToastNotificationService.Instance.ShowWarning("No Ban Selected", "Select a ban to remove.");
                return;
            }

            var displayName = !string.IsNullOrWhiteSpace(ban.BannedName) && !ban.BannedName.Equals(BanImportExportService.DefaultBannedTargetName, StringComparison.OrdinalIgnoreCase)
                ? ban.BannedName
                : ban.IdentityId;

            AppLogger.Info($"[BansViewModel:RemoveBan] Prompting confirmation for ban removal: '{displayName}' (#{ban.BanNumber}, {ban.IdentityId})");

            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Confirm Ban Removal",
                $"Are you sure you want to remove the ban for:\n\n{displayName} ({ban.IdentityId})?",
                "Remove Ban",
                true,
                () => ExecuteRemoveBanAsync(ban),
                () => _dashboard.CloseDialog()
            ));
        }, "Failed preparing ban removal confirmation.");
    }

    private async Task ExecuteRemoveBanAsync(BanModel ban)
    {
        using var timing = AppLogger.Measure($"BansViewModel.ExecuteRemoveBanAsync(#{ban.BanNumber})");
        AppLogger.Info($"[BansViewModel:ExecuteRemove] Dispatching remove ban command for #{ban.BanNumber} ({ban.IdentityId})...");

        try
        {
            bool isSuccess = await _rconService.RemoveBanAsync(ban).ConfigureAwait(false);

            if (isSuccess)
            {
                RemoveBanFromList(ban);
                AppLogger.Info($"[BansViewModel:ExecuteRemove] Ban #{ban.BanNumber} ({ban.IdentityId}) successfully removed.");

                var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                    ? $"#ban remove {ban.IdentityId}"
                    : $"removeBan {ban.BanNumber}";

                ToastNotificationService.Instance.ShowSuccess("Ban Removed", $"Removed ban for {ban.BannedName}", cmd, async () =>
                {
                    AppLogger.Info($"[BansViewModel:Undo] Undo triggered for ban removal: {ban.IdentityId}. Reinstating ban...");
                    await _rconService.OfflineBanAsync(ban.IdentityId, ban.DurationSeconds, ban.Reason, false).ConfigureAwait(false);
                    await RefreshBansAsync().ConfigureAwait(false);
                });
            }
            else
            {
                AppLogger.Warn($"[BansViewModel:ExecuteRemove] Server rejected ban removal for #{ban.BanNumber} ({ban.IdentityId}).");
                ToastNotificationService.Instance.ShowError("Ban Removal Failed", $"Server timed out or ban #{ban.BanNumber} not found.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BansViewModel:ExecuteRemove] Exception executing remove ban for #{ban.BanNumber}: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Ban Removal Error", $"An unexpected error occurred: {ex.Message}");
        }
    }

    [RelayCommand]
    public void RemoveSelectedBans()
    {
        ExecuteSafe(() =>
        {
            var selected = Bans.Where(b => b.IsSelected).ToList();
            if (selected.Count == 0)
            {
                AppLogger.Warn("[BansViewModel:BatchRemove] RemoveSelectedBans called with 0 items selected.");
                ToastNotificationService.Instance.ShowWarning("No Bans Selected", "Select at least one ban to remove.");
                return;
            }

            AppLogger.Info($"[BansViewModel:BatchRemove] Prompting batch removal dialog for {selected.Count} ban(s)...");

            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Remove Selected Bans",
                $"Are you sure you want to remove {selected.Count} selected ban(s) from the server?",
                $"Remove {selected.Count} Ban(s)",
                true,
                () => ExecuteRemoveSelectedBansAsync(selected),
                () => _dashboard.CloseDialog()
            ));
        }, "Failed preparing batch ban removal.");
    }

    private async Task ExecuteRemoveSelectedBansAsync(List<BanModel> selected)
    {
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"BansViewModel.ExecuteRemoveSelectedBansAsync({selected.Count} bans)");
        int total = selected.Count;
        int successCount = 0;
        int failedCount = 0;

        try
        {
            for (int i = 0; i < total; i++)
            {
                var b = selected[i];
                AppLogger.Info($"[BansViewModel:BatchRemove] Processing removal {i + 1}/{total}: {b.BannedName} (#{b.BanNumber}, {b.IdentityId})...");

                bool isSuccess = await _rconService.RemoveBanAsync(b).ConfigureAwait(false);
                if (isSuccess)
                {
                    successCount++;
                    RemoveBanFromList(b);
                }
                else
                {
                    failedCount++;
                    AppLogger.Warn($"[BansViewModel:BatchRemove] Failed removing ban #{b.BanNumber} ({b.IdentityId}).");
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[BansViewModel:BatchRemove] Batch ban removal completed in {elapsedMs:F2}ms (Success: {successCount}, Failed: {failedCount}).");

            if (total > 1)
            {
                AppLogger.TrackEvent("moderation_batch_action", new Dictionary<string, object>
                {
                    ["action_type"] = "remove_ban",
                    [ProtocolTelemetryKey] = _rconService.CurrentProtocol.ToString(),
                    ["target_count"] = total,
                    ["success_count"] = successCount,
                    ["failed_count"] = failedCount,
                    ["duration_ms"] = elapsedMs
                });
            }

            if (total >= 10)
            {
                AppLogger.TrackEvent("moderation_large_batch_action", new Dictionary<string, object>
                {
                    ["action_type"] = "remove_ban",
                    ["target_count"] = total,
                    [ProtocolTelemetryKey] = _rconService.CurrentProtocol.ToString()
                });
            }

            await Dispatcher.UIThread.InvokeAsync(() => IsMultiSelectMode = false);

            if (failedCount == 0)
            {
                ToastNotificationService.Instance.ShowSuccess("Batch Ban Removal", $"Successfully removed all {total} ban(s).");
            }
            else
            {
                ToastNotificationService.Instance.ShowWarning("Batch Ban Removal", $"Completed: {successCount} removed, {failedCount} failed.");
            }

            await RefreshBansAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[BansViewModel:BatchRemove] Critical failure during batch ban removal: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Batch Removal Error", $"An error occurred: {ex.Message}");
        }
    }

    [RelayCommand]
    public Task<bool> ExportBansAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BansViewModel.ExportBansAsync");

        AppLogger.Info($"[BansViewModel:Export] Commencing export of {_allBans.Count} bans to .txt (Server: {_dashboard.Profile.ServerIp}:{_dashboard.Profile.Port}, Protocol: {_rconService.CurrentProtocol})...");
        var payload = BanImportExportService.ExportBansToText(_allBans, _rconService.CurrentProtocol);

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.StorageProvider is { } storage)
            {
                var protocolSuffix = _rconService.CurrentProtocol == RconProtocol.BattlEye ? "battleye" : "reforger";
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export Bans to Text File (.txt)",
                    DefaultExtension = "txt",
                    SuggestedFileName = $"bans_{protocolSuffix}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.txt",
                    FileTypeChoices =
                    [
                        new FilePickerFileType("Text Files (*.txt)")
                        {
                            Patterns = ["*.txt"]
                        }
                    ]
                }).ConfigureAwait(false);

                if (file != null)
                {
                    AppLogger.Info($"[BansViewModel:Export] User selected destination file: '{file.Path}'");
                    var writeStart = Stopwatch.GetTimestamp();

                    await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
                    await using var writer = new StreamWriter(stream, Encoding.UTF8);
                    await writer.WriteAsync(payload).ConfigureAwait(false);
                    await writer.FlushAsync().ConfigureAwait(false);

                    var writeElapsedMs = Stopwatch.GetElapsedTime(writeStart).TotalMilliseconds;
                    var totalElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                    AppLogger.TrackEvent("ban_export_completed", new Dictionary<string, object>
                    {
                        [ProtocolTelemetryKey] = _rconService.CurrentProtocol.ToString(),
                        ["total_bans"] = _allBans.Count,
                        ["destination"] = "File",
                        ["duration_ms"] = Math.Round(totalElapsedMs, 1)
                    });

                    AppLogger.Info($"[BansViewModel:Export] Successfully wrote {_allBans.Count} ban records ({payload.Length} chars) to '{file.Name}' in {writeElapsedMs:F2}ms (Total: {totalElapsedMs:F2}ms).");

                    await Dispatcher.UIThread.InvokeAsync(() =>
                        ToastNotificationService.Instance.ShowSuccess("Bans Exported", $"Saved {_allBans.Count} ban(s) to {file.Name}")
                    );
                    return;
                }

                AppLogger.Info("[BansViewModel:Export] User cancelled file save dialog.");
                return;
            }
        }
        catch (Exception fileEx)
        {
            AppLogger.Error($"[BansViewModel:Export] File export error: {fileEx.Message}. Falling back to clipboard buffer.", fileEx);
            ToastNotificationService.Instance.ShowWarning("File Save Notice", "Could not save to file directly; copied ban records to clipboard instead.");
        }

        await ClipboardService.SetTextAsync(payload).ConfigureAwait(false);
        var clipboardElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        AppLogger.TrackEvent("ban_export_completed", new Dictionary<string, object>
        {
            [ProtocolTelemetryKey] = _rconService.CurrentProtocol.ToString(),
            ["total_bans"] = _allBans.Count,
            ["destination"] = "Clipboard",
            ["duration_ms"] = Math.Round(clipboardElapsedMs, 1)
        });

        AppLogger.Info($"[BansViewModel:Export] Fallback clipboard copy complete in {clipboardElapsedMs:F2}ms ({_allBans.Count} ban records).");
        ToastNotificationService.Instance.ShowToast("Bans Exported", $"Copied {_allBans.Count} ban records (.txt) to clipboard.");
    }, "Failed exporting bans to file.");

    [RelayCommand]
    public Task<bool> ImportBansAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BansViewModel.ImportBansAsync");
        string? content = null;
        string? selectedFileName = null;

        AppLogger.Info("[BansViewModel:Import] Opening platform OpenFilePicker dialog for ban import (.txt)...");

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.StorageProvider is { } storage)
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import Bans (.txt)",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Text Files (*.txt)")
                        {
                            Patterns = ["*.txt"]
                        }
                    ]
                }).ConfigureAwait(false);

                if (files.Count > 0)
                {
                    var file = files[0];
                    selectedFileName = file.Name;
                    AppLogger.Info($"[BansViewModel:Import] User selected import file: '{file.Path}' (Name='{selectedFileName}').");

                    var readStart = Stopwatch.GetTimestamp();
                    await using var stream = await file.OpenReadAsync().ConfigureAwait(false);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    content = await reader.ReadToEndAsync().ConfigureAwait(false);

                    var readElapsedMs = Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                    AppLogger.Info($"[BansViewModel:Import] Read {content.Length} characters ({stream.Length} bytes) from '{selectedFileName}' in {readElapsedMs:F2}ms.");
                }
                else
                {
                    AppLogger.Info("[BansViewModel:Import] User cancelled file selection.");
                    return;
                }
            }
            else
            {
                AppLogger.Error("[BansViewModel:Import] StorageProvider unavailable on MainWindow.");
                ToastNotificationService.Instance.ShowError("Import Error", "Native file picker is unavailable.");
                return;
            }
        }
        catch (Exception fileEx)
        {
            AppLogger.Error($"[BansViewModel:Import] File read exception: {fileEx.Message}", fileEx);
            ToastNotificationService.Instance.ShowError("File Read Error", "Unable to read selected .txt file: " + fileEx.Message);
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            AppLogger.Warn($"[BansViewModel:Import] Selected file '{selectedFileName ?? "unknown"}' contained no readable data.");
            ToastNotificationService.Instance.ShowWarning("Empty File", "Selected file contains no data.");
            return;
        }

        var parseStart = Stopwatch.GetTimestamp();
        var parsed = BanImportExportService.ParseImportPayload(content, _allBans);
        var parseElapsedMs = Stopwatch.GetElapsedTime(parseStart).TotalMilliseconds;

        if (parsed.Count == 0)
        {
            AppLogger.Warn($"[BanImportExport:Import] Zero valid ban rows parsed from '{selectedFileName}' ({content.Length} chars) in {parseElapsedMs:F2}ms.");
            ToastNotificationService.Instance.ShowWarning("No Bans Found", "No valid ban records were found in the selected .txt file.");
            return;
        }

        var totalElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        AppLogger.TrackEvent("ban_import_file_selected", new Dictionary<string, object>
        {
            [ProtocolTelemetryKey] = _rconService.CurrentProtocol.ToString(),
            ["parsed_count"] = parsed.Count,
            ["new_count"] = parsed.Count(p => !p.IsDuplicate),
            ["duplicate_count"] = parsed.Count(p => p.IsDuplicate),
            ["parse_duration_ms"] = Math.Round(parseElapsedMs, 1)
        });

        AppLogger.Info($"[BansViewModel:Import] Successfully processed '{selectedFileName}' in {totalElapsedMs:F2}ms (Parsed={parsed.Count}, New={parsed.Count(p => !p.IsDuplicate)}, Duplicates={parsed.Count(p => p.IsDuplicate)}). Displaying preview dialog...");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _dashboard.ShowDialog(new BanImportPreviewViewModel(
                parsed,
                _rconService,
                _dashboard,
                () => _dashboard.CloseDialog()
            ));
        });
    }, "Failed opening or parsing ban import file.");

    private string FormatBanInfo(BanModel b)
    {
        if (IsReforgerProtocol)
        {
            return $"Banned Name: {b.BannedName}\n" +
                   $"Identity ID: {b.IdentityId}";
        }

        return $"[#]: {b.BanNumber}\n" +
               $"GUID/IP Address: {b.IdentityId}\n" +
               $"Minutes Left: {b.MinutesLeftText}\n" +
               $"Reason: {b.Reason}";
    }

    [RelayCommand]
    public Task<bool> CopyBanInfoAsync(BanModel? ban) => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        ban ??= SelectedBan;
        if (ban == null) return;
        var text = FormatBanInfo(ban);
        await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Debug($"[BansViewModel:Clipboard] Copied ban info for '{ban.BannedName}' ({ban.IdentityId}) in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied ban info for {ban.BannedName}");
    }, "Failed to copy ban info.");

    [RelayCommand]
    private Task<bool> CopyAllInfoAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        var selected = Bans.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0) selected = [.. Bans];

        var formattedEntries = selected.Select(FormatBanInfo);
        var text = string.Join("\n\n", formattedEntries);

        await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[BansViewModel:Clipboard] Copied {selected.Count} ban entries in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied ban list to clipboard.");
    }, "Failed to copy all ban entries.");

    [RelayCommand]
    private Task<bool> LoadBans() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info("[BansViewModel:LoadBans] Dispatching 'loadBans' command to reload bans.txt...");
        AppLogger.TrackEvent("battleye_load_bans_dispatched");
        await _rconService.SendCommandAsync("loadBans").ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Debug($"[BansViewModel:LoadBans] 'loadBans' dispatched in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Load Bans", "Reloaded bans from bans.txt", "loadBans");
    }, "Failed executing loadBans command.");

    [RelayCommand]
    private Task<bool> WriteBans() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info("[BansViewModel:WriteBans] Dispatching 'writeBans' command to persist bans.txt...");
        AppLogger.TrackEvent("battleye_write_bans_dispatched");
        await _rconService.SendCommandAsync("writeBans").ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Debug($"[BansViewModel:WriteBans] 'writeBans' dispatched in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Write Bans", "Saved bans to bans.txt", "writeBans");
    }, "Failed executing writeBans command.");
}