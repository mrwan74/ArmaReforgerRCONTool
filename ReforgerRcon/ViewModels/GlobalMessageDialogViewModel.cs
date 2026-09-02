using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class GlobalMessageDialogViewModel(IRconService rconService, PlayersViewModel parent) : ViewModelBase
{
    private readonly IRconService _rconService = rconService;
    private readonly PlayersViewModel _parent = parent;

    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSending { get; set; }

    [RelayCommand]
    private Task<bool> SendAsync() => ExecuteSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Message))
        {
            AppLogger.Warn("[GlobalMessageDialog:Send] Message broadcast canceled: text is empty.");
            return;
        }

        var cleanMessage = Message.Trim();
        var start = Stopwatch.GetTimestamp();

        IsSending = true;
        try
        {
            AppLogger.Info($"[GlobalMessageDialog:Send] Sending global message ({cleanMessage.Length} chars): '{cleanMessage}'...");
            await _rconService.SendGlobalMessageAsync(cleanMessage).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            AppLogger.Info($"[GlobalMessageDialog:Send] Message delivered in {elapsedMs:F2}ms.");
            ToastNotificationService.Instance.ShowToast("Broadcast", "Global broadcast sent.", $"#say -1 {cleanMessage}");
            _parent.CloseDialog();
        }
        finally
        {
            IsSending = false;
        }
    }, "Failed to dispatch global message.");

    [RelayCommand]
    private void Close()
    {
        AppLogger.Debug("[GlobalMessageDialog:Close] Dialog closed.");
        _parent.CloseDialog();
    }
}