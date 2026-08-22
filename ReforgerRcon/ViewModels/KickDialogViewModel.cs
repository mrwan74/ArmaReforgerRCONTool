using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class KickDialogViewModel(List<PlayerModel> targets, IRconService rconService, PlayersViewModel parent) : ViewModelBase
{
    private readonly List<PlayerModel> _targets = targets;
    private readonly IRconService _rconService = rconService;
    private readonly PlayersViewModel _parent = parent;

    [ObservableProperty] public partial string TargetNames { get; set; } = string.Join(", ", targets.Select(t => $"{t.Name} (ID: {t.Id})"));
    [ObservableProperty] public partial string Reason { get; set; } = "Kicked by Administrator";
    [ObservableProperty] public partial bool IsExecuting { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;

    [RelayCommand]
    private Task<bool> ConfirmKickAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsExecuting) return;
        IsExecuting = true;

        int successCount = 0;
        int failedCount = 0;

        try
        {
            int total = _targets.Count;
            for (int i = 0; i < total; i++)
            {
                var player = _targets[i];
                ProgressStatus = $"Kicking {player.Name} ({i + 1}/{total})...";
                AppLogger.Info($"[KickDialog] Sequentially kicking target {i + 1}/{total}: {player.Name} (ID: {player.Id})...");

                bool isSuccess = await _rconService.KickPlayerAsync(player, Reason);

                var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                    ? $"#kick {player.Id} {Reason}"
                    : $"kick {player.Id} {Reason}";

                if (isSuccess)
                {
                    successCount++;
                    ToastNotificationService.Instance.ShowSuccess("Kick Executed", $"Kicked {player.Name}", cmd);
                    _parent.RemovePlayerFromList(player);
                }
                else
                {
                    failedCount++;
                    AppLogger.Warn($"[KickDialog] Kick failed or timed out for {player.Name}. Retaining player in UI list.");
                    ToastNotificationService.Instance.ShowError(
                        "Kick Failed",
                        $"Could not kick {player.Name} (ID: {player.Id}): Server timed out or player already left.",
                        cmd
                    );
                }
            }

            _parent.CloseDialog();
            await _parent.RefreshPlayersAsync();

            if (total > 1)
            {
                if (failedCount == 0)
                {
                    ToastNotificationService.Instance.ShowSuccess("Batch Kick Complete", $"Successfully kicked all {total} player(s).");
                }
                else
                {
                    ToastNotificationService.Instance.ShowWarning(
                        "Batch Kick Summary",
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