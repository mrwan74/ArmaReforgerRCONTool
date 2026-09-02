using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using Sentry;

namespace ReforgerRcon.ViewModels;

public partial class BansViewModel(IRconService rconService, DashboardViewModel dashboard) : ViewModelBase
{
    public const string DefaultSortKey = "Default";

    private readonly IRconService _rconService = rconService;
    private readonly DashboardViewModel _dashboard = dashboard;
    private List<BanModel> _allBans = [];
    private bool _isUpdatingSelection;

    [ObservableProperty] public partial ObservableCollection<BanModel> Bans { get; set; } = [];
    [ObservableProperty] public partial BanModel? SelectedBan { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial bool IsAllSelected { get; set; }

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CurrentSortField => _dashboard.SettingsTab.Settings.BansSortBy;
    public bool CurrentSortAscending => _dashboard.SettingsTab.Settings.BansSortAscending;

    [RelayCommand]
    public Task<bool> RefreshBansAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("BansViewModel.RefreshBansAsync");
        AppLogger.Debug($"[BansViewModel:Refresh] Fetching ban records from server ({_rconService.CurrentProtocol})...");

        _allBans = await _rconService.GetBansAsync().ConfigureAwait(false);
        ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);

        _dashboard.ActiveBansCount = _allBans.Count;
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[BansViewModel:Refresh] Loaded {_allBans.Count} ban records ({Bans.Count} visible) in {elapsedMs:F2}ms.");
    });

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
        });
    }

    public void RemoveBanFromList(BanModel ban)
    {
        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            int removed = _allBans.RemoveAll(b => b.IdentityId == ban.IdentityId || (b.BanNumber == ban.BanNumber && b.BanNumber != 0));

            var match = Bans.FirstOrDefault(b => b.IdentityId == ban.IdentityId || (b.BanNumber == ban.BanNumber && b.BanNumber != 0));
            if (match != null)
            {
                Bans.Remove(match);
            }

            _dashboard.ActiveBansCount = _allBans.Count;
            UpdateSelectedState();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[BansViewModel:Remove] Removed ban #{ban.BanNumber} ({ban.IdentityId}) from list in {elapsedMs:F2}ms (Purged: {removed}).");
        });
    }

    public void ApplyFilter(string query, string searchType)
    {
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
        });
    }

    private void OnBanPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (e.PropertyName == nameof(BanModel.IsSelected))
        {
            UpdateSelectedState();
        }
    }

    partial void OnIsAllSelectedChanged(bool value)
    {
        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            _isUpdatingSelection = true;
            try
            {
                foreach (var b in Bans)
                {
                    b.IsSelected = value;
                }
                AppLogger.Debug($"[BansViewModel:SelectAll] Toggled IsAllSelected to {value} across {Bans.Count} entries.");
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        });
    }

    private void UpdateSelectedState()
    {
        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            bool allSelected = Bans.Count > 0 && Bans.All(b => b.IsSelected);
            if (IsAllSelected != allSelected)
            {
                _isUpdatingSelection = true;
                try
                {
                    IsAllSelected = allSelected;
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            }
        });
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
                return;
            }

            var displayName = !string.IsNullOrWhiteSpace(ban.BannedName) && !ban.BannedName.Equals("Banned Target", StringComparison.OrdinalIgnoreCase)
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
        });
    }

    private async Task ExecuteRemoveBanAsync(BanModel ban)
    {
        using var timing = AppLogger.Measure($"BansViewModel.ExecuteRemoveBanAsync(#{ban.BanNumber})");
        AppLogger.Info($"[BansViewModel:ExecuteRemove] Dispatching remove ban command for #{ban.BanNumber} ({ban.IdentityId})...");

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

    [RelayCommand]
    public void RemoveSelectedBans()
    {
        ExecuteSafe(() =>
        {
            var selected = Bans.Where(b => b.IsSelected).ToList();
            if (selected.Count == 0)
            {
                AppLogger.Warn("[BansViewModel:BatchRemove] RemoveSelectedBans called with 0 items selected.");
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
        });
    }

    private async Task ExecuteRemoveSelectedBansAsync(List<BanModel> selected)
    {
        using var timing = AppLogger.Measure($"BansViewModel.ExecuteRemoveSelectedBansAsync({selected.Count} bans)");
        int total = selected.Count;
        int successCount = 0;
        int failedCount = 0;

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

        IsMultiSelectMode = false;
        AppLogger.Info($"[BansViewModel:BatchRemove] Batch ban removal completed (Success: {successCount}, Failed: {failedCount}).");

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
    });

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
    });

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
    });

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
    });
}