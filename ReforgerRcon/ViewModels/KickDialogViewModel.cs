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

    [RelayCommand]
    private async Task ConfirmKickAsync()
    {
        foreach (var player in _targets)
        {
            await _rconService.KickPlayerAsync(player, Reason);
            var cmd = $"#kick {player.Id} {Reason}";
            ToastNotificationService.Instance.ShowToast("Kick Executed", $"Kicked {player.Name}", cmd);
        }
        _parent.CloseDialog();
    }

    [RelayCommand]
    private void Close() => _parent.CloseDialog();
}