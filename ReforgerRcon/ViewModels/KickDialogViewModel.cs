using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
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
    [ObservableProperty] public partial string Reason { get; set; } = "Kicked by Admin";
    [ObservableProperty] public partial bool IsExecuting { get; set; }
    [ObservableProperty] public partial string ProgressStatus { get; set; } = string.Empty;

    [RelayCommand]
    private Task<bool> ConfirmKickAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsExecuting) return;
        IsExecuting = true;

        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"KickDialogViewModel.ConfirmKickAsync({_targets.Count} targets)");
        int successCount = 0;
        int failedCount = 0;

        try
        {
            int total = _targets.Count;
            AppLogger.Info($"[KickDialog:Execute] Starting kick sequence for {total} player(s) (Reason: '{Reason}')...");

            AppLogger.TrackEvent("moderation_kick_executed", new Dictionary<string, object>
            {
                ["protocol"] = _rconService.CurrentProtocol.ToString(),
                ["target_count"] = total,
                ["is_batch"] = total > 1
            });

            if (total >= 10)
            {
                AppLogger.TrackEvent("moderation_large_batch_action", new Dictionary<string, object>
                {
                    ["action_type"] = "kick",
                    ["target_count"] = total,
                    ["protocol"] = _rconService.CurrentProtocol.ToString()
                });
            }

            for (int i = 0; i < total; i++)
            {
                var player = _targets[i];
                await Dispatcher.UIThread.InvokeAsync(() => ProgressStatus = $"Kicking {player.Name} ({i + 1}/{total})...");
                AppLogger.Info($"[KickDialog:Execute] Target {i + 1}/{total}: '{player.Name}' (ID: #{player.Id}, UID: {player.Uid})...");

                bool isSuccess = await _rconService.KickPlayerAsync(player, Reason).ConfigureAwait(false);

                var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                    ? $"#kick {player.Id} {Reason}"
                    : $"kick {player.Id} {Reason}";

                if (isSuccess)
                {
                    successCount++;
                    AppLogger.Info($"[KickDialog:Execute] Kick SUCCESS for '{player.Name}'.");
                    ToastNotificationService.Instance.ShowSuccess("Kick Executed", $"Kicked {player.Name}", cmd);
                    _parent.RemovePlayerFromList(player);
                }
                else
                {
                    failedCount++;
                    AppLogger.Warn($"[KickDialog:Execute] Kick FAILED for '{player.Name}'. Command: '{cmd}'");
                    ToastNotificationService.Instance.ShowError(
                        "Kick Failed",
                        $"Could not kick {player.Name} (ID: {player.Id}): Server timed out or player already left.",
                        cmd
                    );
                }
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[KickDialog:Execute] Kick execution complete in {elapsedMs:F2}ms (Success: {successCount}, Failed: {failedCount}).");

            if (total > 1)
            {
                AppLogger.TrackEvent("moderation_batch_action", new Dictionary<string, object>
                {
                    ["action_type"] = "kick",
                    ["protocol"] = _rconService.CurrentProtocol.ToString(),
                    ["target_count"] = total,
                    ["success_count"] = successCount,
                    ["failed_count"] = failedCount,
                    ["duration_ms"] = elapsedMs
                });
            }

            await Dispatcher.UIThread.InvokeAsync(() => _parent.CloseDialog());

            await _parent.RefreshPlayersAsync().ConfigureAwait(false);

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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsExecuting = false;
                ProgressStatus = string.Empty;
            });
        }
    });

    [RelayCommand]
    private void Close()
    {
        if (IsExecuting) return;
        AppLogger.Debug("[KickDialog:Close] Dialog closed.");
        _parent.CloseDialog();
    }
}