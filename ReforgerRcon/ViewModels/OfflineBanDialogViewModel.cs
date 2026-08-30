using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class OfflineBanDialogViewModel : ViewModelBase
{
    private readonly string _uid;
    private readonly string _ip;
    private readonly IRconService _rconService;
    private readonly DatabaseViewModel _parent;

    [ObservableProperty] public partial string TargetIdentifier { get; set; }
    [ObservableProperty] public partial string TargetType { get; set; }
    [ObservableProperty] public partial ObservableCollection<string> AvailableTargetTypes { get; set; }
    [ObservableProperty] public partial string SelectedPreset { get; set; } = "1 Month";
    [ObservableProperty] public partial int CustomYears { get; set; }
    [ObservableProperty] public partial int CustomMonths { get; set; }
    [ObservableProperty] public partial int CustomWeeks { get; set; }
    [ObservableProperty] public partial int CustomDays { get; set; }
    [ObservableProperty] public partial int CustomHours { get; set; }
    [ObservableProperty] public partial int CustomMinutes { get; set; }
    [ObservableProperty] public partial int CustomSeconds { get; set; }
    [ObservableProperty] public partial string Reason { get; set; } = "Offline Ban - Exploiting/Rule Violation";
    [ObservableProperty] public partial string TotalCalculatedTimeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpiryDateText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsExecuting { get; set; }
    [ObservableProperty] public partial bool IsTargetTypeSelectionVisible { get; set; }

    public bool IsCustomSelected => SelectedPreset == "Custom Duration";
    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;

    public OfflineBanDialogViewModel(string uid, string ip, IRconService rconService, DatabaseViewModel parent)
    {
        _uid = uid?.Trim() ?? string.Empty;
        _ip = ip?.Trim() ?? string.Empty;
        _rconService = rconService;
        _parent = parent;

        bool hasValidIp = !string.IsNullOrWhiteSpace(_ip) &&
                          !_ip.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
                          IPAddress.TryParse(_ip, out _);

        if (IsReforgerProtocol)
        {
            TargetIdentifier = $"UID: {_uid}";
            AvailableTargetTypes = ["Identity ID (UID)"];
            TargetType = "Identity ID (UID)";
            IsTargetTypeSelectionVisible = false;
        }
        else if (hasValidIp)
        {
            TargetIdentifier = $"GUID: {_uid} | IP: {_ip}";
            AvailableTargetTypes = ["Both (GUID & IP)", "BattlEye GUID", "IP Address"];
            TargetType = "Both (GUID & IP)";
            IsTargetTypeSelectionVisible = true;
        }
        else
        {
            TargetIdentifier = $"GUID: {_uid}";
            AvailableTargetTypes = ["BattlEye GUID"];
            TargetType = "BattlEye GUID";
            IsTargetTypeSelectionVisible = false;
        }

        AppLogger.Info($"[OfflineBanDialog] Initialized for target UID: '{_uid}', IP: '{_ip}' ({TargetType})");
        UpdateCalculations();
    }

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomSelected));
        AppLogger.Debug($"[OfflineBanDialog] Preset changed to '{value}'.");
        UpdateCalculations();
    }

    partial void OnCustomYearsChanged(int value) => UpdateCalculations();
    partial void OnCustomMonthsChanged(int value) => UpdateCalculations();
    partial void OnCustomWeeksChanged(int value) => UpdateCalculations();
    partial void OnCustomDaysChanged(int value) => UpdateCalculations();
    partial void OnCustomHoursChanged(int value) => UpdateCalculations();
    partial void OnCustomMinutesChanged(int value) => UpdateCalculations();
    partial void OnCustomSecondsChanged(int value) => UpdateCalculations();

    [RelayCommand]
    private void SetPreset(string preset)
    {
        if (IsExecuting) return;
        SelectedPreset = preset;
    }

    private long CalculateTotalSeconds() => SelectedPreset switch
    {
        "1 Hour" => 3600,
        "6 Hours" => 21600,
        "1 Day" => 86400,
        "3 Days" => 259200,
        "1 Week" => 604800,
        "1 Month" => 2592000,
        "Permanent" => 0,
        "Custom Duration" =>
            (CustomYears * 31536000L) +
            (CustomMonths * 2592000L) +
            (CustomWeeks * 604800L) +
            (CustomDays * 86400L) +
            (CustomHours * 3600L) +
            (CustomMinutes * 60L) +
            CustomSeconds,
        _ => 2592000
    };

    private void UpdateCalculations()
    {
        var totalSec = CalculateTotalSeconds();
        if (totalSec <= 0)
        {
            TotalCalculatedTimeText = "Total: Permanent";
            ExpiryDateText = "Expires: Never";
        }
        else
        {
            var span = TimeSpan.FromSeconds(totalSec);
            TotalCalculatedTimeText = $"Total: {(int)span.TotalDays} days, {span.Hours} hours, {span.Minutes} mins ({totalSec} seconds)";
            ExpiryDateText = $"Expires: {DateTime.Now.AddSeconds(totalSec):MMM dd, yyyy HH:mm}";
        }
    }

    [RelayCommand]
    private Task<bool> ConfirmAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsExecuting) return;
        IsExecuting = true;

        using var timing = AppLogger.Measure($"OfflineBanDialogViewModel.ConfirmAsync('{_uid}')");

        try
        {
            var totalSec = CalculateTotalSeconds();
            bool banUid = IsReforgerProtocol || TargetType.Contains("GUID", StringComparison.OrdinalIgnoreCase) || TargetType.Contains("Both", StringComparison.OrdinalIgnoreCase);
            bool banIp = !IsReforgerProtocol && (TargetType.Contains("IP", StringComparison.OrdinalIgnoreCase) || TargetType.Contains("Both", StringComparison.OrdinalIgnoreCase));

            bool allSucceeded = true;
            AppLogger.Info($"[OfflineBanDialog] Executing offline ban for UID: '{_uid}', IP: '{_ip}' (Ban UID: {banUid}, Ban IP: {banIp}, Total Seconds: {totalSec})...");

            if (banUid)
            {
                if (!string.IsNullOrWhiteSpace(_uid) && !_uid.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    bool uidSuccess = await _rconService.OfflineBanAsync(_uid, totalSec, Reason, isIp: false).ConfigureAwait(false);
                    if (!uidSuccess)
                    {
                        allSucceeded = false;
                        AppLogger.Warn($"[OfflineBanDialog] Server rejected offline ban for UID: '{_uid}'.");
                        ToastNotificationService.Instance.ShowError("Offline Ban Failed", $"Server rejected ban for UID: {_uid}.");
                    }
                }
                else
                {
                    allSucceeded = false;
                    AppLogger.Warn("[OfflineBanDialog] Offline ban aborted: UID is missing or invalid.");
                    ToastNotificationService.Instance.ShowError("Invalid Target", "Cannot ban target: UID is missing or invalid.");
                }
            }

            if (banIp && allSucceeded && !string.IsNullOrWhiteSpace(_ip) && !_ip.Equals("N/A", StringComparison.OrdinalIgnoreCase) && IPAddress.TryParse(_ip, out _))
            {
                bool ipSuccess = await _rconService.OfflineBanAsync(_ip, totalSec, Reason, isIp: true).ConfigureAwait(false);
                if (!ipSuccess)
                {
                    allSucceeded = false;
                    AppLogger.Warn($"[OfflineBanDialog] Server rejected offline IP ban for '{_ip}'.");
                    ToastNotificationService.Instance.ShowError("IP Ban Failed", $"Server rejected IP ban for {_ip}.");
                }
            }

            if (allSucceeded)
            {
                AppLogger.Info("[OfflineBanDialog] Offline ban confirmed successfully.");
                ToastNotificationService.Instance.ShowSuccess(
                    "Offline Ban Complete",
                    $"Offline ban confirmed for {_uid}",
                    IsReforgerProtocol ? $"#ban create {_uid} {totalSec}" : $"addBan {_uid}",
                    async () =>
                    {
                        AppLogger.Info($"[OfflineBanDialog] Undo action triggered for offline ban '{_uid}'. Removing ban...");
                        var allBans = await _rconService.GetBansAsync().ConfigureAwait(false);
                        var ban = allBans.Find(b => b.IdentityId == _uid || b.IdentityId == _ip);
                        if (ban != null)
                        {
                            await _rconService.RemoveBanAsync(ban).ConfigureAwait(false);
                            await _parent.RefreshAfterOfflineBanAsync().ConfigureAwait(false);
                        }
                    }
                );

                _parent.CloseDialog();
                await _parent.RefreshAfterOfflineBanAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            IsExecuting = false;
        }
    });

    [RelayCommand]
    private void Close()
    {
        if (IsExecuting) return;
        AppLogger.Debug("[OfflineBanDialog] Operator closed offline ban dialog.");
        _parent.CloseDialog();
    }
}