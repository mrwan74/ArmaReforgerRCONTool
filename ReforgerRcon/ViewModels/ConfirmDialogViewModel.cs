using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class ConfirmDialogViewModel(
    string title,
    string message,
    string confirmButtonText,
    bool isDanger,
    Func<Task> onConfirmed,
    Action onClose) : ViewModelBase
{
    private readonly Func<Task> _onConfirmed = onConfirmed;
    private readonly Action _onClose = onClose;

    [ObservableProperty] public partial string Title { get; set; } = title;
    [ObservableProperty] public partial string Message { get; set; } = message;
    [ObservableProperty] public partial string ConfirmButtonText { get; set; } = confirmButtonText;
    [ObservableProperty] public partial bool IsDanger { get; set; } = isDanger;
    [ObservableProperty] public partial bool IsExecuting { get; set; }

    [RelayCommand]
    private Task<bool> ConfirmAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsExecuting) return;
        IsExecuting = true;
        var sw = Stopwatch.StartNew();

        try
        {
            AppLogger.Info($"[ConfirmDialog] Confirmed action: '{Title}'. Executing callback...");
            await _onConfirmed();
            sw.Stop();
            AppLogger.Info($"[ConfirmDialog] Action '{Title}' completed in {sw.ElapsedMilliseconds} ms.");
            _onClose();
        }
        finally
        {
            IsExecuting = false;
        }
    }, $"Operation '{Title}' failed.");

    [RelayCommand]
    private void Close()
    {
        if (IsExecuting) return;
        AppLogger.Debug($"[ConfirmDialog] Cancelled action: '{Title}'.");
        _onClose();
    }
}