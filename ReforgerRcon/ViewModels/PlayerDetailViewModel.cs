using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class PlayerDetailViewModel(PlayerModel player, IRconService rconService, PlayersViewModel parent) : ViewModelBase
{
    private readonly IRconService _rconService = rconService;
    private readonly PlayersViewModel _parent = parent;

    [ObservableProperty] public partial PlayerModel Player { get; set; } = player;

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    [RelayCommand]
    private static async Task CopyFieldAsync(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;
        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied: {text}");
    }

    [RelayCommand]
    private void EditComment() => _parent.OpenSetComment(Player);

    [RelayCommand]
    private void Kick()
    {
        _parent.CloseDialog();
        _parent.OpenKickDialog(Player);
    }

    [RelayCommand]
    private void Ban()
    {
        _parent.CloseDialog();
        _parent.OpenBanDialog(Player);
    }

    [RelayCommand]
    private void Close() => _parent.CloseDialog();
}