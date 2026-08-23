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
            AppLogger.Warn("[GlobalMessageDialog] Global broadcast cancelled: Message text is empty.");
            return;
        }

        var cleanMessage = Message.Trim();
        var sw = Stopwatch.StartNew();

        IsSending = true;
        try
        {
            AppLogger.Info($"[GlobalMessageDialog] Dispatching global broadcast message ({cleanMessage.Length} chars): '{cleanMessage}'...");
            await _rconService.SendGlobalMessageAsync(cleanMessage);
            sw.Stop();

            AppLogger.Info($"[GlobalMessageDialog] Global broadcast message delivered in {sw.ElapsedMilliseconds} ms.");
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
        AppLogger.Debug("[GlobalMessageDialog] Operator closed global message dialog.");
        _parent.CloseDialog();
    }
}