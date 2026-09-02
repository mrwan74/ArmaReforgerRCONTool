using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class BanDialogViewModel : ViewModelBase
{
    private readonly List<PlayerModel> _targets;
    private readonly IRconService _rconService;
    private readonly PlayersViewModel _parent;

    [ObservableProperty] public partial string TargetNames { get; set; }
    [ObservableProperty] public partial string SelectedPreset { get; set; } = "1 Day";
    [ObservableProperty] public partial int CustomYears { get; set; }
    [ObservableProperty] public partial int CustomMonths { get; set; }
    [ObservableProperty] public partial int CustomWeeks { get; set; }
    [ObservableProperty] public partial int CustomDays { get; set; }
    [ObservableProperty] public partial int CustomHours { get; set; }
    [ObservableProperty] public partial int CustomMinutes { get; set; }
    [ObservableProperty] public partial int CustomSeconds { get; set; }
    [ObservableProperty] public partial string Reason { get; set; } = "Rule violation";
    [ObservableProperty] public partial string CommandPreview { get; set; } = string.Empty;
    [ObservableProperty] public partial string TotalCalculatedTimeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpiryDateText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsExecuting { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;

    [ObservableProperty] public partial bool AlsoBanIpAddress { get; set; } = true;
    [ObservableProperty] public partial bool IsBanIpVisible { get; set; }
    [ObservableProperty] public partial string BanIpCheckboxLabel { get; set; } = "Also ban IP Address";

    public bool IsCustomSelected => SelectedPreset == "Custom Duration";
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public BanDialogViewModel(List<PlayerModel> targets, IRconService rconService, PlayersViewModel parent)
    {
        _targets = targets;
        _rconService = rconService;
        _parent = parent;

        TargetNames = string.Join(", ", targets.Select(t => t.Name));

        var firstTarget = targets.FirstOrDefault();
        bool hasValidIp = firstTarget != null &&
                          !string.IsNullOrWhiteSpace(firstTarget.Ip) &&
                          !firstTarget.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase) &&
                          IPAddress.TryParse(firstTarget.Ip, out _);

        if (IsBattlEyeProtocol && hasValidIp)
        {
            IsBanIpVisible = true;
            BanIpCheckboxLabel = targets.Count == 1
                ? $"Also ban player IP address ({firstTarget!.Ip})"
                : "Also ban player IP addresses";
        }
        else
        {
            IsBanIpVisible = false;
        }

        AppLogger.Info($"[BanDialog:Init] Initialized for {targets.Count} target(s): '{TargetNames}' (Protocol: {_rconService.CurrentProtocol}, HasValidIP: {hasValidIp})");
        UpdateCalculations();
    }

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomSelected));
        AppLogger.Debug($"[BanDialog:Preset] Changed to '{value}'.");
        UpdateCalculations();
    }

    partial void OnAlsoBanIpAddressChanged(bool value) => UpdateCalculations();
    partial void OnCustomYearsChanged(int value) => UpdateCalculations();
    partial void OnCustomMonthsChanged(int value) => UpdateCalculations();
    partial void OnCustomWeeksChanged(int value) => UpdateCalculations();
    partial void OnCustomDaysChanged(int value) => UpdateCalculations();
    partial void OnCustomHoursChanged(int value) => UpdateCalculations();
    partial void OnCustomMinutesChanged(int value) => UpdateCalculations();
    partial void OnCustomSecondsChanged(int value) => UpdateCalculations();
    partial void OnReasonChanged(string value) => UpdateCalculations();

    [RelayCommand]
    private void SetPreset(string preset)
    {
        if (IsExecuting) return;
        SelectedPreset = preset;
    }

    [RelayCommand]
    private async Task CopyCommandPreviewAsync()
    {
        await ClipboardService.SetTextAsync(CommandPreview).ConfigureAwait(false);
        AppLogger.Debug($"[BanDialog:Clipboard] Copied command preview: '{CommandPreview}'");
        ToastNotificationService.Instance.ShowToast("Copied", "Copied ban command to clipboard.");
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
        _ => 86400
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

        var sampleTarget = _targets.FirstOrDefault();

        if (_rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn)
        {
            var targetPlayerId = sampleTarget != null ? sampleTarget.Id.ToString(CultureInfo.InvariantCulture) : "<Player#>";
            CommandPreview = $"#ban create {targetPlayerId} {totalSec} {Reason}";
        }
        else
        {
            var targetId = sampleTarget != null ? sampleTarget.Id.ToString(CultureInfo.InvariantCulture) : "<#>";
            long beMinutes = totalSec <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(totalSec / 60.0));
            CommandPreview = $"ban {targetId} {beMinutes} {Reason}";

            if (AlsoBanIpAddress && sampleTarget != null && !string.IsNullOrWhiteSpace(sampleTarget.Ip) && !sampleTarget.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                CommandPreview += $" && addBan {sampleTarget.Ip} {beMinutes} {Reason} && loadBans";
            }
        }

        AppLogger.Trace($"[BanDialog:Calculation] Calculated: {TotalCalculatedTimeText} -> '{CommandPreview}'");
    }

    [RelayCommand]
    private Task<bool> ConfirmBanAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsExecuting) return;
        IsExecuting = true;

        using var timing = AppLogger.Measure($"BanDialogViewModel.ConfirmBanAsync({_targets.Count} targets)");
        int successCount = 0;
        int failedCount = 0;

        try
        {
            var totalSec = CalculateTotalSeconds();
            int total = _targets.Count;
            AppLogger.Info($"[BanDialog:Execute] Starting sequential ban for {total} target(s) (Duration: {totalSec}s, Reason: '{Reason}', AlsoBanIp: {AlsoBanIpAddress})...");

            for (int i = 0; i < total; i++)
            {
                var player = _targets[i];
                ProgressStatus = $"Banning {player.Name} ({i + 1}/{total})...";
                AppLogger.Info($"[BanDialog:Execute] Target {i + 1}/{total}: '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}, IP: {player.Ip})...");

                bool isSuccess = await _rconService.BanPlayerWithOptionalIpAsync(player, totalSec, Reason, AlsoBanIpAddress).ConfigureAwait(false);

                long beMinutes = totalSec <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(totalSec / 60.0));
                var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                    ? $"#ban create {player.Id} {totalSec} {Reason}"
                    : $"ban {player.Id} {beMinutes} {Reason}";

                if (isSuccess)
                {
                    successCount++;
                    AppLogger.Info($"[BanDialog:Execute] Ban SUCCESS for '{player.Name}'.");
                    ToastNotificationService.Instance.ShowSuccess("Ban Executed", $"Banned {player.Name}", cmd, async () =>
                    {
                        AppLogger.Info($"[BanDialog:Undo] Ban undo invoked for '{player.Name}'. Reinstating unban...");
                        var allBans = await _rconService.GetBansAsync().ConfigureAwait(false);
                        var ban = allBans.FirstOrDefault(b => b.IdentityId == player.Uid || b.IdentityId == player.Guid || b.IdentityId == player.Ip);
                        if (ban != null)
                        {
                            await _rconService.RemoveBanAsync(ban).ConfigureAwait(false);
                            await _parent.TriggerPostBanRefreshAsync().ConfigureAwait(false);
                        }
                    });

                    _parent.RemovePlayerFromList(player);
                }
                else
                {
                    failedCount++;
                    AppLogger.Warn($"[BanDialog:Execute] Ban FAILED for '{player.Name}'. Command: '{cmd}'");
                    ToastNotificationService.Instance.ShowError(
                        "Ban Failed",
                        $"Could not ban {player.Name}: Server rejected command or timed out.",
                        cmd
                    );
                }
            }

            AppLogger.Info($"[BanDialog:Execute] Ban execution complete (Success: {successCount}, Failed: {failedCount}).");

            _parent.CloseDialog();
            await _parent.TriggerPostBanRefreshAsync().ConfigureAwait(false);

            if (total > 1)
            {
                if (failedCount == 0)
                {
                    ToastNotificationService.Instance.ShowSuccess("Batch Ban Complete", $"Successfully banned all {total} player(s).");
                }
                else
                {
                    ToastNotificationService.Instance.ShowWarning(
                        "Batch Ban Summary",
                        $"Processed {total} target(s): {successCount} succeeded, {failedCount} failed."
                    );
                }
            }
        }
        finally
        {
            IsExecuting = false;
            ProgressStatus = string.Empty;
        }
    });

    [RelayCommand]
    private void Close()
    {
        if (IsExecuting) return;
        AppLogger.Debug("[BanDialog:Close] Dialog closed.");
        _parent.CloseDialog();
    }
}