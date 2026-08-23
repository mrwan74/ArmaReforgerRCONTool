using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class AnnouncementDialogViewModel(IRconService rconService, PlayersViewModel parent) : ViewModelBase
{
    private readonly IRconService _rconService = rconService;
    private readonly PlayersViewModel _parent = parent;

    [ObservableProperty] public partial string Title { get; set; } = "Server Announcement";
    [ObservableProperty] public partial string Message { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsSending { get; set; }

    [RelayCommand]
    private Task<bool> SendAsync() => ExecuteSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(Message))
        {
            AppLogger.Warn("[AnnouncementDialog] Broadcast cancelled: Announcement message is empty.");
            return;
        }

        var cleanTitle = string.IsNullOrWhiteSpace(Title) ? "Server Announcement" : Title.Trim();
        var cleanMessage = Message.Trim();
        var sw = Stopwatch.StartNew();

        IsSending = true;
        try
        {
            AppLogger.Info($"[AnnouncementDialog] Dispatching server announcement: Title='{cleanTitle}', Length={cleanMessage.Length} chars...");
            await _rconService.SendAnnouncementAsync(cleanTitle, cleanMessage);
            sw.Stop();

            AppLogger.Info($"[AnnouncementDialog] Announcement broadcast dispatched successfully in {sw.ElapsedMilliseconds} ms.");
            ToastNotificationService.Instance.ShowToast("Announcement", "Broadcast dispatched.", $"#say -1 [{cleanTitle}] {cleanMessage}");
            _parent.CloseDialog();
        }
        finally
        {
            IsSending = false;
        }
    }, "Failed to dispatch server announcement.");

    [RelayCommand]
    private void Close()
    {
        AppLogger.Debug("[AnnouncementDialog] Operator closed announcement dialog.");
        _parent.CloseDialog();
    }
}