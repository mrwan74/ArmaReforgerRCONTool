using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public static class ClipboardService
{
    public static async Task<bool> SetTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            AppLogger.Debug("[ClipboardService:Copy] SetTextAsync skipped for empty text.");
            return false;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text).ConfigureAwait(false);
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Debug($"[ClipboardService:Copy] Copied {text.Length} char(s) to clipboard in {elapsedMs:F2}ms.");
                return true;
            }

            var elapsedFailMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Warn($"[ClipboardService:Copy] Platform clipboard unavailable after {elapsedFailMs:F2}ms.");
            ToastNotificationService.Instance.ShowWarning("Clipboard Unavailable", "Unable to access clipboard from current window.");
            return false;
        }
        catch (Win32Exception winEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Error($"[ClipboardService:Copy] Win32 clipboard error in {elapsedMs:F2}ms (Code: {winEx.NativeErrorCode}): {winEx.Message}", winEx);
            ToastNotificationService.Instance.ShowError("Clipboard Locked", "Another process is locking the clipboard.");
            return false;
        }
        catch (TimeoutException timeEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Warn($"[ClipboardService:Copy] Clipboard wait timed out after {elapsedMs:F2}ms: {timeEx.Message}");
            ToastNotificationService.Instance.ShowWarning("Clipboard Timeout", "Timed out waiting for clipboard lock.");
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Warn($"[ClipboardService:Copy] Clipboard operation invalid: {invEx.Message}");
            ToastNotificationService.Instance.ShowWarning("Clipboard Notice", "Unable to copy text in current state.");
            return false;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Error($"[ClipboardService:Copy] Error setting clipboard in {elapsedMs:F2}ms: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Clipboard Error", "Failed copying to clipboard: " + ex.Message);
            return false;
        }
    }
}