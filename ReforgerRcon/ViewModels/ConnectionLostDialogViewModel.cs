using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class ConnectionLostDialogViewModel(
    ServerProfile profile,
    string reason,
    IRconService rconService,
    Action onReconnected,
    Action onReturnToLogin,
    Action onDismiss) : ViewModelBase
{
    private readonly ServerProfile _profile = profile;
    private readonly IRconService _rconService = rconService;
    private readonly Action _onReconnected = onReconnected;
    private readonly Action _onReturnToLogin = onReturnToLogin;
    private readonly Action _onDismiss = onDismiss;

    [ObservableProperty] public partial string ServerEndpoint { get; set; } = $"{profile.ServerIp}:{profile.Port}";
    [ObservableProperty] public partial string ProtocolText { get; set; } = profile.Protocol == RconProtocol.ReforgerBuiltIn ? "Reforger Built-in RCON" : "BattlEye RCON";
    [ObservableProperty] public partial string ReasonMessage { get; set; } = string.IsNullOrWhiteSpace(reason) ? "Connection timed out (No response received from server)." : reason;
    [ObservableProperty] public partial bool IsReconnecting { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;

    [RelayCommand]
    private async Task ReconnectAsync()
    {
        await ExecuteSafeAsync(async () =>
        {
            IsReconnecting = true;
            ErrorMessage = string.Empty;
            AppLogger.Info($"[ConnectionLostDialog] Reconnection attempt initiated to {_profile.ServerIp}:{_profile.Port}...");

            var success = await _rconService.ConnectAsync(_profile);
            if (success)
            {
                AppLogger.Info("[ConnectionLostDialog] Reconnected successfully to server.");
                ToastNotificationService.Instance.ShowToast("Reconnected", $"Re-established connection to {_profile.ServerIp}:{_profile.Port}");
                _onReconnected();
            }
            else
            {
                ErrorMessage = "Failed to reconnect. The game server is still offline or unreachable.";
                AppLogger.Warn($"[ConnectionLostDialog] Reconnection to {_profile.ServerIp}:{_profile.Port} failed.");
            }
        });
        IsReconnecting = false;
    }

    [RelayCommand]
    private Task<bool> ReturnToLoginAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Info("[ConnectionLostDialog] Operator selected 'Return to Login'. Executing complete session teardown...");
        await _rconService.DisconnectAsync();
        _onReturnToLogin();
    });

    [RelayCommand]
    private void Dismiss()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[ConnectionLostDialog] Operator closed disconnection notice.");
            _onDismiss();
        });
    }
}