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
            AppLogger.Debug("[ClipboardService] SetTextAsync called with empty string.");
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
                AppLogger.Debug($"[ClipboardService] Text copied to clipboard in {sw.ElapsedMilliseconds} ms ({text.Length} chars).");
                return true;
            }
            sw.Stop();
            AppLogger.Warn("[ClipboardService] Clipboard unavailable: MainWindow or platform Clipboard is null.");
            ToastNotificationService.Instance.ShowWarning("Clipboard Unavailable", "Unable to access system clipboard from current window.");
            return false;
        }
        catch (Win32Exception winEx)
        {
            sw.Stop();
            AppLogger.Error($"[ClipboardService] Win32 error accessing clipboard: {winEx.Message} (Error code: {winEx.NativeErrorCode})", winEx);
            ToastNotificationService.Instance.ShowError("Clipboard Lock Error", "Another application has locked the system clipboard.");
            return false;
        }
        catch (TimeoutException timeEx)
        {
            sw.Stop();
            AppLogger.Warn($"[ClipboardService] Clipboard lock acquisition timed out after {sw.ElapsedMilliseconds} ms: {timeEx.Message}");
            ToastNotificationService.Instance.ShowWarning("Clipboard Timed Out", "Timed out waiting for system clipboard lock.");
            return false;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Error($"[ClipboardService] Failed setting clipboard text: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Clipboard Error", "Failed copying text to clipboard: " + ex.Message);
            return false;
        }
    }
}