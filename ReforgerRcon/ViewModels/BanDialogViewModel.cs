using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class BanDialogViewModel(List<PlayerModel> targets, IRconService rconService, PlayersViewModel parent) : ViewModelBase
{
    private readonly List<PlayerModel> _targets = targets;
    private readonly IRconService _rconService = rconService;
    private readonly PlayersViewModel _parent = parent;

    [ObservableProperty] public partial string TargetNames { get; set; } = string.Join(", ", targets.Select(t => t.Name));
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

    public bool IsCustomSelected => SelectedPreset == "Custom Duration";

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomSelected));
        UpdateCalculations();
    }

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
        await ClipboardService.SetTextAsync(CommandPreview);
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
            var targetGuid = sampleTarget != null ? sampleTarget.Guid : "<GUID>";
            long beMinutes = totalSec <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(totalSec / 60.0));
            CommandPreview = $"addBan {targetGuid} {beMinutes} {Reason}";
        }
    }

    [RelayCommand]
    private Task<bool> ConfirmBanAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsExecuting) return;
        IsExecuting = true;

        int successCount = 0;
        int failedCount = 0;

        try
        {
            var totalSec = CalculateTotalSeconds();
            int total = _targets.Count;
            for (int i = 0; i < total; i++)
            {
                var player = _targets[i];
                ProgressStatus = $"Banning {player.Name} ({i + 1}/{total})...";
                AppLogger.Info($"[BanDialog] Sequentially banning target {i + 1}/{total}: {player.Name} (Duration: {totalSec}s)...");

                bool isSuccess = await _rconService.BanPlayerAsync(player, totalSec, Reason);

                long beMinutes = totalSec <= 0 ? 0 : Math.Max(1, (long)Math.Ceiling(totalSec / 60.0));
                var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                    ? $"#ban create {player.Id} {totalSec} {Reason}"
                    : $"addBan {player.Guid} {beMinutes} {Reason}";

                if (isSuccess)
                {
                    successCount++;
                    ToastNotificationService.Instance.ShowSuccess("Ban Executed", $"Banned {player.Name}", cmd, async () =>
                    {
                        var allBans = await _rconService.GetBansAsync();
                        var ban = allBans.FirstOrDefault(b => b.IdentityId == player.Uid || b.IdentityId == player.Guid);
                        if (ban != null)
                        {
                            await _rconService.RemoveBanAsync(ban);
                            await _parent.TriggerPostBanRefreshAsync();
                        }
                    });

                    _parent.RemovePlayerFromList(player);
                }
                else
                {
                    failedCount++;
                    AppLogger.Warn($"[BanDialog] Ban failed or timed out for {player.Name}. Retaining player in UI list.");
                    ToastNotificationService.Instance.ShowError(
                        "Ban Failed",
                        $"Could not ban {player.Name}: Server rejected command or timed out.",
                        cmd
                    );
                }
            }

            _parent.CloseDialog();
            await _parent.TriggerPostBanRefreshAsync();

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
        _parent.CloseDialog();
    }
}