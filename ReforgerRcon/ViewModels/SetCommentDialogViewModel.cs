using System.Diagnostics;
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
        using var timing = AppLogger.Measure($"SetCommentDialogViewModel.SaveAsync('{_uid}')");
        var cleanComment = Comment?.Trim() ?? string.Empty;
        AppLogger.Info($"[SetCommentDialog] Saving updated comment for '{PlayerName}' (UID: {_uid}): '{cleanComment}'...");

        if (_dashboard.PlayersTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } livePlayer)
        {
            livePlayer.Comment = cleanComment;
            AppLogger.Debug($"[SetCommentDialog] Live player row updated for '{PlayerName}'.");
        }

        if (_dashboard.DatabaseTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } dbPlayer)
        {
            dbPlayer.Comment = cleanComment;
            AppLogger.Debug($"[SetCommentDialog] Historical player row updated for '{PlayerName}'.");
        }

        await _rconService.UpdatePlayerCommentAsync(_uid, cleanComment);

        AppLogger.Info($"[SetCommentDialog] Comment successfully persisted to SQLite for {PlayerName} ({_uid}).");
        ToastNotificationService.Instance.ShowToast("Comment Saved", $"Comment updated for {PlayerName}");
        _dashboard.CloseDialog();
    }

    [RelayCommand]
    private void Close()
    {
        AppLogger.Debug("[SetCommentDialog] Operator closed comment dialog.");
        _dashboard.CloseDialog();
    }
}