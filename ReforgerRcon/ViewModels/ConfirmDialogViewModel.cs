using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        await _onConfirmed();
        _onClose();
    }

    [RelayCommand]
    private void Close() => _onClose();
}