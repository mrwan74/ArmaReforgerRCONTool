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
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure($"SetCommentDialogViewModel.SaveAsync('{_uid}')");
        var cleanComment = Comment?.Trim() ?? string.Empty;
        AppLogger.Info($"[SetCommentDialog:Save] Saving comment for '{PlayerName}' (UID: {_uid}): '{cleanComment}'...");

        if (_dashboard.PlayersTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } livePlayer)
        {
            livePlayer.Comment = cleanComment;
            AppLogger.Debug($"[SetCommentDialog:Save] Live player row updated for '{PlayerName}'.");
        }

        if (_dashboard.DatabaseTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } dbPlayer)
        {
            dbPlayer.Comment = cleanComment;
            AppLogger.Debug($"[SetCommentDialog:Save] Historical player row updated for '{PlayerName}'.");
        }

        await _rconService.UpdatePlayerCommentAsync(_uid, cleanComment).ConfigureAwait(false);

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[SetCommentDialog:Save] Comment persisted in {elapsedMs:F2}ms for {PlayerName} ({_uid}).");
        ToastNotificationService.Instance.ShowToast("Comment Saved", $"Comment updated for {PlayerName}");
        _dashboard.CloseDialog();
    }

    [RelayCommand]
    private void Close()
    {
        AppLogger.Debug("[SetCommentDialog:Close] Dialog closed.");
        _dashboard.CloseDialog();
    }
}