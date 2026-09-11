using System;
using System.Diagnostics;
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

        var start = Stopwatch.GetTimestamp();
        var sanitized = AppLogger.SanitizeSensitiveData(text);
        AppLogger.Debug($"[DatabasePlayerDetail:Clipboard] Copying value ({text.Length} chars): '{sanitized}'");

        try
        {
            bool success = await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            if (success)
            {
                AppLogger.Trace($"[DatabasePlayerDetail:Clipboard] Copied in {elapsedMs:F2}ms.");
                ToastNotificationService.Instance.ShowToast("Copied", $"Copied: {sanitized}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DatabasePlayerDetail:Clipboard] Failed copying value: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Clipboard Error", "Unable to copy field.");
        }
    }

    [RelayCommand]
    private void EditComment()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DatabasePlayerDetail:Comment] Opening comment editor for '{Player.Name}' (UID: {Player.Uid}).");
            _parent.OpenSetComment(Player);
        });
    }

    [RelayCommand]
    private void OfflineBan()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info($"[DatabasePlayerDetail:OfflineBan] Opening offline ban dialog for '{Player.Name}' (UID: {Player.Uid}).");
            _parent.CloseDialog();
            _parent.OpenOfflineBan(Player);
        });
    }

    [RelayCommand]
    private void Close()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DatabasePlayerDetail:Close] Dialog closed for '{Player.Name}'.");
            _parent.CloseDialog();
        });
    }
}