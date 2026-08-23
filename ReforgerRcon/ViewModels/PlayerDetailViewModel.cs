using System.Diagnostics;
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

        AppLogger.Debug($"[PlayerDetailViewModel] Copying field value to clipboard ({text.Length} chars): '{text}'");
        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied: {text}");
    }

    [RelayCommand]
    private void EditComment()
    {
        AppLogger.Debug($"[PlayerDetailViewModel] Opening comment editor for '{Player.Name}' (UID: {Player.Uid}).");
        _parent.OpenSetComment(Player);
    }

    [RelayCommand]
    private void Kick()
    {
        AppLogger.Info($"[PlayerDetailViewModel] Kick player dialog requested for '{Player.Name}' (ID: {Player.Id}).");
        _parent.CloseDialog();
        _parent.OpenKickDialog(Player);
    }

    [RelayCommand]
    private void Ban()
    {
        AppLogger.Info($"[PlayerDetailViewModel] Ban player dialog requested for '{Player.Name}' (ID: {Player.Id}).");
        _parent.CloseDialog();
        _parent.OpenBanDialog(Player);
    }

    [RelayCommand]
    private void Close()
    {
        AppLogger.Debug("[PlayerDetailViewModel] Operator closed player details dialog.");
        _parent.CloseDialog();
    }
}