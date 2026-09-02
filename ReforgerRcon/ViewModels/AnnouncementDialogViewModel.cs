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
            AppLogger.Warn("[AnnouncementDialog:Send] Broadcast canceled: Message text is empty.");
            return;
        }

        var cleanTitle = string.IsNullOrWhiteSpace(Title) ? "Server Announcement" : Title.Trim();
        var cleanMessage = Message.Trim();
        var start = Stopwatch.GetTimestamp();

        IsSending = true;
        try
        {
            AppLogger.Info($"[AnnouncementDialog:Send] Dispatching server announcement: Title='{cleanTitle}', Length={cleanMessage.Length} chars...");
            await _rconService.SendAnnouncementAsync(cleanTitle, cleanMessage).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            AppLogger.Info($"[AnnouncementDialog:Send] Announcement delivered in {elapsedMs:F2}ms.");
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
        AppLogger.Debug("[AnnouncementDialog:Close] Dialog closed.");
        _parent.CloseDialog();
    }
}