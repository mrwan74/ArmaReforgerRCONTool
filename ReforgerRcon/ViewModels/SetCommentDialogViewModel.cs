using System;
using System.Collections.Generic;
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
    private readonly string _uid = uid ?? string.Empty;
    private readonly IRconService _rconService = rconService;
    private readonly DashboardViewModel _dashboard = dashboard;

    [ObservableProperty] public partial string PlayerName { get; set; } = playerName ?? "Unknown";
    [ObservableProperty] public partial string Comment { get; set; } = initialComment ?? string.Empty;
    [ObservableProperty] public partial bool IsSaving { get; set; }

    [RelayCommand]
    private Task<bool> SaveAsync() => ExecuteSafeAsync(async () =>
    {
        if (IsSaving)
        {
            AppLogger.Warn($"[SetCommentDialog:Save] Save ignored: already in progress for '{PlayerName}' ({_uid}).");
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var cleanComment = Comment?.Trim() ?? string.Empty;
        var context = new Dictionary<string, object?>
        {
            ["player_name"] = PlayerName,
            ["uid"] = _uid,
            ["comment_length"] = cleanComment.Length,
            ["protocol"] = _rconService.CurrentProtocol.ToString(),
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Info($"[SetCommentDialog:Save] Persisting player comment for '{PlayerName}' (UID: {_uid}, Length={cleanComment.Length})...", context);
        IsSaving = true;

        try
        {
            if (_dashboard.PlayersTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } livePlayer)
            {
                livePlayer.Comment = cleanComment;
                AppLogger.Debug($"[SetCommentDialog:Save] Updated active player instance in live table for '{PlayerName}'.");
            }

            if (_dashboard.DatabaseTab.Players.FirstOrDefault(p => p.Uid == _uid || p.Guid == _uid) is { } dbPlayer)
            {
                dbPlayer.Comment = cleanComment;
                AppLogger.Debug($"[SetCommentDialog:Save] Updated historical player instance in database table for '{PlayerName}'.");
            }

            await _rconService.UpdatePlayerCommentAsync(_uid, cleanComment).ConfigureAwait(false);

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["elapsed_ms"] = elapsedMs;

            AppLogger.TrackEvent("player_comment_saved", new Dictionary<string, object>
            {
                ["protocol"] = _rconService.CurrentProtocol.ToString(),
                ["comment_length"] = cleanComment.Length,
                ["has_comment"] = !string.IsNullOrEmpty(cleanComment)
            });

            AppLogger.Info($"[SetCommentDialog:Save] Comment successfully saved in {elapsedMs:F2}ms for '{PlayerName}' ({_uid}).", context);
            ToastNotificationService.Instance.ShowSuccess("Comment Saved", $"Updated administrator note for {PlayerName}.");
            _dashboard.CloseDialog();
        }
        catch (Exception ex)
        {
            context["error"] = ex.Message;
            AppLogger.Error($"[SetCommentDialog:Save] Failed persisting comment for '{PlayerName}': {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Save Failed", $"Unable to save player comment: {ex.Message}");
        }
        finally
        {
            IsSaving = false;
        }
    }, "Failed saving player comment to database.");

    [RelayCommand]
    private void Close()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[SetCommentDialog:Close] Closed comment editor for '{PlayerName}' ({_uid}).");
            _dashboard.CloseDialog();
        });
    }
}