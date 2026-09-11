using System;
using System.Collections.Generic;
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
            AppLogger.Warn("[AnnouncementDialog:Send] Broadcast rejected: Message text is empty.");
            ToastNotificationService.Instance.ShowWarning("Empty Announcement", "Please enter announcement text before broadcasting.");
            return;
        }

        var cleanTitle = string.IsNullOrWhiteSpace(Title) ? "Server Announcement" : Title.Trim();
        var cleanMessage = Message.Trim();
        var start = Stopwatch.GetTimestamp();

        var context = new Dictionary<string, object?>
        {
            ["title"] = cleanTitle,
            ["message_length"] = cleanMessage.Length,
            ["protocol"] = _rconService.CurrentProtocol.ToString(),
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[AnnouncementDialog:Send] Broadcasting announcement: Header='{cleanTitle}', Length={cleanMessage.Length} chars...", context);
        IsSending = true;

        try
        {
            await _rconService.SendAnnouncementAsync(cleanTitle, cleanMessage).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["elapsed_ms"] = elapsedMs;

            AppLogger.TrackEvent("global_broadcast_dispatched", new Dictionary<string, object>
            {
                ["protocol"] = _rconService.CurrentProtocol.ToString(),
                ["type"] = "Announcement",
                ["char_length"] = cleanMessage.Length
            });

            AppLogger.Info($"[AnnouncementDialog:Send] Announcement broadcast dispatched successfully in {elapsedMs:F2}ms.", context);
            ToastNotificationService.Instance.ShowSuccess("Announcement Sent", $"Broadcasted: [{cleanTitle}] {cleanMessage}", $"#say -1 [{cleanTitle}] {cleanMessage}");
            _parent.CloseDialog();
        }
        catch (Exception ex)
        {
            context["error"] = ex.Message;
            AppLogger.Error($"[AnnouncementDialog:Send] Failed dispatching announcement: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Broadcast Failed", $"Unable to send announcement: {ex.Message}");
        }
        finally
        {
            IsSending = false;
        }
    }, "Failed to dispatch server announcement.");

    [RelayCommand]
    private void Close()
    {
        if (IsSending) return;
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[AnnouncementDialog:Close] Announcement dialog closed.");
            _parent.CloseDialog();
        });
    }
}