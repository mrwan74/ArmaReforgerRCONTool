using System.Linq;
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
        var cleanComment = Comment?.Trim() ?? string.Empty;

        // 1. Instantly update live Player in Players Tab
        if (_dashboard.PlayersTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } livePlayer)
        {
            livePlayer.Comment = cleanComment;
        }

        // 2. Instantly update Player in Historical Database Tab
        if (_dashboard.DatabaseTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } dbPlayer)
        {
            dbPlayer.Comment = cleanComment;
        }

        // 3. Persist updated note to SQLite database and service cache
        await _rconService.UpdatePlayerCommentAsync(_uid, cleanComment);

        AppLogger.Info($"[SetCommentDialog] Comment updated live for {PlayerName} ({_uid}): '{cleanComment}'");
        ToastNotificationService.Instance.ShowToast("Comment Saved", $"Comment updated for {PlayerName}");
        _dashboard.CloseDialog();
    }

    [RelayCommand]
    private void Close() => _dashboard.CloseDialog();
}