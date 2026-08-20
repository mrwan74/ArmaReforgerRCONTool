using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class SetCommentDialogViewModel(
    string playerName,
    string uid,
    string initialComment,
    IRconService rconService,
    DashboardViewModel dashboard) : ViewModelBase
{
    private readonly string _uid = uid;
    private readonly IRconService _rconService = rconService;
    private readonly DashboardViewModel _dashboard = dashboard;

    [ObservableProperty] public partial string PlayerName { get; set; } = playerName;
    [ObservableProperty] public partial string Comment { get; set; } = initialComment;

    [RelayCommand]
    private async Task SaveAsync()
    {
        await _rconService.UpdatePlayerCommentAsync(_uid, Comment);
        ToastNotificationService.Instance.ShowToast("Comment Saved", $"Comment updated for {PlayerName}");
        _dashboard.CloseDialog();
    }

    [RelayCommand]
    private void Close() => _dashboard.CloseDialog();
}