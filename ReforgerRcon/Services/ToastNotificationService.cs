using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;

namespace ReforgerRcon.Services;

public class ToastNotificationService
{
    public static ToastNotificationService Instance { get; } = new();

    public ObservableCollection<ToastNotificationModel> ActiveToasts { get; } = [];

    public void ShowToast(string title, string message, string? commandExecuted = null, Func<Task>? undoAction = null)
    {
        AppLogger.Info($"[TOAST] {title}: {message} [Command: {commandExecuted ?? "N/A"}]");

        var toast = new ToastNotificationModel
        {
            Title = title,
            Message = message,
            CommandExecuted = commandExecuted
        };

        if (undoAction != null)
        {
            toast.UndoCommand = new AsyncRelayCommand(async () =>
            {
                try
                {
                    AppLogger.Info($"[TOAST UNDO] Executing undo action for toast: {title}");
                    ActiveToasts.Remove(toast);
                    await undoAction();
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Error executing toast undo action: {ex.Message}", ex);
                }
            });
        }

        ActiveToasts.Add(toast);

        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ActiveToasts.Remove(toast));
        });
    }

    public void Dismiss(ToastNotificationModel toast)
    {
        ActiveToasts.Remove(toast);
    }
}