using System;
using System.Collections.ObjectModel;
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
        AppLogger.Info($"[TOAST:{type}] {title}: {message} [Command: {commandExecuted ?? "N/A"}]");

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
                try
                {
                    AppLogger.Info($"[TOAST UNDO] Executing undo action for toast: {title}");
                    ActiveToasts.Remove(toast);
                    await undoAction();
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Error executing toast undo action: {ex.Message}", ex);
                    ShowError("Undo Failed", $"Could not revert action: {ex.Message}");
                }
            });
        }

        Dispatcher.UIThread.Post(() => ActiveToasts.Add(toast));

        _ = Task.Run(async () =>
        {
            await Task.Delay(5000);
            Dispatcher.UIThread.Post(() => ActiveToasts.Remove(toast));
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
        Dispatcher.UIThread.Post(() => ActiveToasts.Remove(toast));
    }
}