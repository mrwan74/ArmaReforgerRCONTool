using System;
using System.Collections.Generic;
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

        var start = Stopwatch.GetTimestamp();
        var sanitized = AppLogger.SanitizeSensitiveData(text);
        AppLogger.Debug($"[PlayerDetailViewModel:Clipboard] Copying attribute to clipboard: '{sanitized}' ({text.Length} chars)...");

        try
        {
            bool success = await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            if (success)
            {
                AppLogger.Trace($"[PlayerDetailViewModel:Clipboard] Field copied in {elapsedMs:F2}ms.");
                ToastNotificationService.Instance.ShowToast("Copied", $"Copied: {sanitized}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[PlayerDetailViewModel:Clipboard] Failed copying field value: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Clipboard Error", "Failed copying value to clipboard.");
        }
    }

    [RelayCommand]
    private void EditComment()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[PlayerDetailViewModel:Comment] Opening comment editor for '{Player.Name}' (UID: {Player.Uid}).");
            _parent.OpenSetComment(Player);
        });
    }

    [RelayCommand]
    private void Kick()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info($"[PlayerDetailViewModel:Kick] Triggering kick dialog from detail overlay for '{Player.Name}' (ID: #{Player.Id}).");
            _parent.CloseDialog();
            _parent.OpenKickDialog(Player);
        });
    }

    [RelayCommand]
    private void Ban()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info($"[PlayerDetailViewModel:Ban] Triggering ban dialog from detail overlay for '{Player.Name}' (ID: #{Player.Id}).");
            _parent.CloseDialog();
            _parent.OpenBanDialog(Player);
        });
    }

    [RelayCommand]
    private void Close()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[PlayerDetailViewModel:Close] Closed detail dialog for '{Player.Name}'.");
            _parent.CloseDialog();
        });
    }
}