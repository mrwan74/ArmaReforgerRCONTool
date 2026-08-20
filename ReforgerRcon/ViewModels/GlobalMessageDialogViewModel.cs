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

    [RelayCommand]
    private async Task SendAsync()
    {
        if (string.IsNullOrWhiteSpace(Message)) return;
        await _rconService.SendGlobalMessageAsync(Message);
        ToastNotificationService.Instance.ShowToast("Broadcast", "Global broadcast sent.", $"#say -1 {Message}");
        _parent.CloseDialog();
    }

    [RelayCommand]
    private void Close() => _parent.CloseDialog();
}