using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class BansViewModel(IRconService rconService) : ViewModelBase
{
    private readonly IRconService _rconService = rconService;
    private List<BanModel> _allBans = [];
    private bool _isUpdatingSelection;

    [ObservableProperty] public partial ObservableCollection<BanModel> Bans { get; set; } = [];
    [ObservableProperty] public partial BanModel? SelectedBan { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial bool IsAllSelected { get; set; }

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    [RelayCommand]
    public Task<bool> RefreshBansAsync() => ExecuteSafeAsync(async () =>
    {
        _allBans = await _rconService.GetBansAsync();
        ApplyFilter(string.Empty, string.Empty);
    });

    public void ApplyFilter(string query, string searchType)
    {
        ExecuteSafe(() =>
        {
            foreach (var b in Bans)
            {
                b.PropertyChanged -= OnBanPropertyChanged;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                Bans = new ObservableCollection<BanModel>(_allBans);
            }
            else
            {
                IEnumerable<BanModel> filtered = searchType switch
                {
                    "Name" => _allBans.Where(b => b.BannedName.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "UID" => _allBans.Where(b => b.IdentityId.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    _ => _allBans.Where(b =>
                        b.BannedName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        b.IdentityId.Contains(query, StringComparison.OrdinalIgnoreCase))
                };

                Bans = new ObservableCollection<BanModel>(filtered);
            }

            foreach (var b in Bans)
            {
                b.PropertyChanged += OnBanPropertyChanged;
            }

            UpdateSelectedState();
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
    public Task<bool> RemoveBan(BanModel? ban) => ExecuteSafeAsync(async () =>
    {
        ban ??= SelectedBan;
        if (ban == null) return;
        await _rconService.RemoveBanAsync(ban);
        Bans.Remove(ban);
        var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
            ? $"#ban remove {ban.IdentityId}"
            : $"removeBan {ban.BanNumber}";

        ToastNotificationService.Instance.ShowToast("Ban Removed", $"Removed ban for {ban.BannedName}", cmd, async () =>
        {
            await _rconService.OfflineBanAsync(ban.IdentityId, ban.DurationSeconds, ban.Reason, false);
            await RefreshBansAsync();
        });
    });

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
        ban ??= SelectedBan;
        if (ban == null) return;
        var text = FormatBanInfo(ban);
        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied ban info for {ban.BannedName}");
    });

    [RelayCommand]
    private Task<bool> RemoveSelectedBans() => ExecuteSafeAsync(async () =>
    {
        var selected = Bans.Where(b => b.IsSelected).ToList();
        foreach (var b in selected)
        {
            await _rconService.RemoveBanAsync(b);
            Bans.Remove(b);
        }
        IsMultiSelectMode = false;
    });

    [RelayCommand]
    private Task<bool> CopyAllInfoAsync() => ExecuteSafeAsync(async () =>
    {
        var selected = Bans.Where(b => b.IsSelected).ToList();
        if (selected.Count == 0) selected = [.. Bans];

        var formattedEntries = selected.Select(FormatBanInfo);
        var text = string.Join("\n\n", formattedEntries);

        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied bans list to clipboard.");
    });

    [RelayCommand]
    private Task<bool> LoadBans() => ExecuteSafeAsync(async () =>
    {
        await _rconService.SendCommandAsync("loadBans");
        ToastNotificationService.Instance.ShowToast("Load Bans", "Reloaded bans from bans.txt", "loadBans");
    });

    [RelayCommand]
    private Task<bool> WriteBans() => ExecuteSafeAsync(async () =>
    {
        await _rconService.SendCommandAsync("writeBans");
        ToastNotificationService.Instance.ShowToast("Write Bans", "Saved bans to bans.txt", "writeBans");
    });
}