using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class DatabasePlayerDetailViewModel(DatabasePlayerModel player, DatabaseViewModel parent) : ViewModelBase
{
    private readonly DatabaseViewModel _parent = parent;

    [ObservableProperty] public partial DatabasePlayerModel Player { get; set; } = player;

    public bool IsReforgerProtocol => _parent.IsReforgerProtocol;
    public bool IsBattlEyeProtocol => _parent.IsBattlEyeProtocol;

    [RelayCommand]
    private static async Task CopyFieldAsync(object? value)
    {
        var text = value?.ToString() ?? string.Empty;
        if (string.IsNullOrEmpty(text)) return;
        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied: {text}");
    }

    [RelayCommand]
    private void EditComment() => _parent.OpenSetComment(Player);

    [RelayCommand]
    private void OfflineBan()
    {
        _parent.CloseDialog();
        _parent.OpenOfflineBan(Player);
    }

    [RelayCommand]
    private void Close() => _parent.CloseDialog();
}