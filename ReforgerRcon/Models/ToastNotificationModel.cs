using System;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.Models;

public class ToastNotificationModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? CommandExecuted { get; set; }
    public IRelayCommand? UndoCommand { get; set; }
    public bool HasUndo => UndoCommand != null;
    public bool HasCommand => !string.IsNullOrWhiteSpace(CommandExecuted) && !CommandExecuted.Equals("N/A", StringComparison.OrdinalIgnoreCase);
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public IRelayCommand DismissCommand => new RelayCommand(() => ToastNotificationService.Instance.Dismiss(this));
}