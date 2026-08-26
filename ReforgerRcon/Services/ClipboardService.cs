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
            AppLogger.Debug("[ClipboardService] SetTextAsync skipped for empty text.");
            return false;
        }

        var sw = Stopwatch.StartNew();
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                sw.Stop();
                AppLogger.Debug($"[ClipboardService] Copied {text.Length} char(s) to system clipboard in {sw.ElapsedMilliseconds} ms.");
                return true;
            }

            sw.Stop();
            AppLogger.Warn("[ClipboardService] Platform clipboard is unavailable from current window state.");
            ToastNotificationService.Instance.ShowWarning("Clipboard Unavailable", "Unable to access clipboard from current window.");
            return false;
        }
        catch (Win32Exception winEx)
        {
            sw.Stop();
            AppLogger.Error($"[ClipboardService] Win32 clipboard locking error: {winEx.Message} (Code: {winEx.NativeErrorCode})", winEx);
            ToastNotificationService.Instance.ShowError("Clipboard Locked", "Another process is currently locking the system clipboard.");
            return false;
        }
        catch (TimeoutException timeEx)
        {
            sw.Stop();
            AppLogger.Warn($"[ClipboardService] Clipboard lock wait timed out after {sw.ElapsedMilliseconds} ms: {timeEx.Message}");
            ToastNotificationService.Instance.ShowWarning("Clipboard Timeout", "Timed out waiting for system clipboard lock.");
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            sw.Stop();
            AppLogger.Warn($"[ClipboardService] Clipboard operation invalid in current state: {invEx.Message}");
            ToastNotificationService.Instance.ShowWarning("Clipboard Notice", "Unable to copy text in current window state.");
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Error($"[ClipboardService] Unexpected error setting clipboard: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Clipboard Error", "Failed copying to clipboard: " + ex.Message);
            return false;
        }
    }
}