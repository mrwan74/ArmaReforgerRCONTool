using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public class ToastNotificationService
{
    public static ToastNotificationService Instance { get; } = new();

    public ObservableCollection<ToastNotificationModel> ActiveToasts { get; } = [];

    public void ShowToast(string title, string message, string? commandExecuted = null, Func<Task>? undoAction = null, ToastType type = ToastType.Info)
    {
        AppLogger.Info($"[ToastNotificationService:Show] [{type}] {title}: {message} [Command: {commandExecuted ?? "N/A"}] (HasUndo: {undoAction != null})");

        if (type is ToastType.Error or ToastType.Warning)
        {
            SoundNotificationService.PlayAlert(type == ToastType.Error ? SoundAlertType.CriticalError : SoundAlertType.WarningAlert);
        }

        var toast = new ToastNotificationModel
        {
            Title = title,
            Message = message,
            CommandExecuted = commandExecuted,
            Type = type
        };

        if (undoAction != null)
        {
            toast.UndoCommand = new AsyncRelayCommand(async () =>
            {
                var sw = Stopwatch.StartNew();
                var secondsSinceAction = Math.Round((DateTime.UtcNow - toast.CreatedAt).TotalSeconds, 1);

                AppLogger.TrackEvent("moderation_undo_clicked", new Dictionary<string, object>
                {
                    ["action_title"] = title,
                    ["command_executed"] = commandExecuted ?? "Unknown",
                    ["seconds_since_action"] = secondsSinceAction
                });

                try
                {
                    AppLogger.Info($"[ToastNotificationService:Undo] Executing undo action for '{title}' (Age={secondsSinceAction}s)...");
                    await Dispatcher.UIThread.InvokeAsync(() => ActiveToasts.Remove(toast));
                    await undoAction().ConfigureAwait(false);
                    sw.Stop();
                    AppLogger.Info($"[ToastNotificationService:Undo] Undo for '{title}' complete in {sw.ElapsedMilliseconds}ms.");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    AppLogger.Error($"[ToastNotificationService:Undo] Error executing undo: {ex.Message}", ex);
                    ShowError("Undo Failed", $"Could not revert action: {ex.Message}");
                }
            });
        }

        Dispatcher.UIThread.Post(() =>
        {
            ActiveToasts.Add(toast);
            AppLogger.Trace($"[ToastNotificationService:Queue] Added '{title}' (ActiveCount={ActiveToasts.Count}).");
        });

        _ = Task.Run(async () =>
        {
            await Task.Delay(5000).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (ActiveToasts.Remove(toast))
                {
                    AppLogger.Trace($"[ToastNotificationService:Queue] Auto-dismissed '{title}'.");
                }
            });
        });
    }

    public void ShowSuccess(string title, string message, string? commandExecuted = null, Func<Task>? undoAction = null)
        => ShowToast(title, message, commandExecuted, undoAction, ToastType.Success);

    public void ShowWarning(string title, string message, string? commandExecuted = null)
        => ShowToast(title, message, commandExecuted, null, ToastType.Warning);

    public void ShowError(string title, string message, string? commandExecuted = null)
        => ShowToast(title, message, commandExecuted, null, ToastType.Error);

    public void Dismiss(ToastNotificationModel toast)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ActiveToasts.Remove(toast))
            {
                AppLogger.Trace($"[ToastNotificationService:Queue] Manually dismissed '{toast.Title}'.");
            }
        });
    }
}