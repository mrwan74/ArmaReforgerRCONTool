using System;
using System.Collections.Generic;
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
            AppLogger.Warn("[GlobalMessageDialog:Send] Global message broadcast rejected: text is empty.");
            ToastNotificationService.Instance.ShowWarning("Empty Message", "Please enter message text before sending.");
            return;
        }

        var cleanMessage = Message.Trim();
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["message_length"] = cleanMessage.Length,
            ["protocol"] = _rconService.CurrentProtocol.ToString(),
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[GlobalMessageDialog:Send] Sending global server chat message ({cleanMessage.Length} chars)...", context);
        IsSending = true;

        try
        {
            await _rconService.SendGlobalMessageAsync(cleanMessage).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["elapsed_ms"] = elapsedMs;

            AppLogger.TrackEvent("global_broadcast_dispatched", new Dictionary<string, object>
            {
                ["protocol"] = _rconService.CurrentProtocol.ToString(),
                ["type"] = "Chat",
                ["char_length"] = cleanMessage.Length
            });

            AppLogger.Info($"[GlobalMessageDialog:Send] Global message sent in {elapsedMs:F2}ms.", context);
            ToastNotificationService.Instance.ShowSuccess("Broadcast Sent", cleanMessage, $"#say -1 {cleanMessage}");
            _parent.CloseDialog();
        }
        catch (Exception ex)
        {
            context["error"] = ex.Message;
            AppLogger.Error($"[GlobalMessageDialog:Send] Failed sending global message: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Broadcast Failed", $"Unable to send global chat: {ex.Message}");
        }
        finally
        {
            IsSending = false;
        }
    }, "Failed to dispatch global chat message.");

    [RelayCommand]
    private void Close()
    {
        if (IsSending) return;
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[GlobalMessageDialog:Close] Global message dialog closed.");
            _parent.CloseDialog();
        });
    }
}