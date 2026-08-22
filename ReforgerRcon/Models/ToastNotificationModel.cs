using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Material.Icons;
using ReforgerRcon.Services;

namespace ReforgerRcon.Models;

public enum ToastType
{
    Info,
    Success,
    Warning,
    Error
}

public class ToastNotificationModel
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? CommandExecuted { get; set; }
    public ToastType Type { get; set; } = ToastType.Info;
    public IRelayCommand? UndoCommand { get; set; }
    public bool HasUndo => UndoCommand != null;
    public bool HasCommand => !string.IsNullOrWhiteSpace(CommandExecuted) && !CommandExecuted.Equals("N/A", StringComparison.OrdinalIgnoreCase);
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public MaterialIconKind IconKind => Type switch
    {
        ToastType.Success => MaterialIconKind.CheckCircleOutline,
        ToastType.Warning => MaterialIconKind.AlertOutline,
        ToastType.Error => MaterialIconKind.AlertOctagramOutline,
        _ => MaterialIconKind.InformationOutline
    };

    public IBrush AccentBrush => Type switch
    {
        ToastType.Success => Brush.Parse("#10B981"),
        ToastType.Warning => Brush.Parse("#F59E0B"),
        ToastType.Error => Brush.Parse("#EF4444"),
        _ => Brush.Parse("#3B82F6")
    };

    public IRelayCommand DismissCommand => new RelayCommand(() => ToastNotificationService.Instance.Dismiss(this));
}