using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public static class ClipboardService
{
    public static async Task<bool> SetTextAsync(string text)
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(text);
                AppLogger.Debug($"Text copied to clipboard ({text.Length} chars).");
                return true;
            }
            AppLogger.Warn("Clipboard access unavailable: MainWindow or Clipboard instance is null.");
            return false;
        }
        catch (Win32Exception winEx)
        {
            AppLogger.Error($"Win32 clipboard error: {winEx.Message}", winEx);
            return false;
        }
        catch (TimeoutException timeEx)
        {
            AppLogger.Warn($"Clipboard lock acquisition timed out: {timeEx.Message}");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed setting clipboard text: {ex.Message}", ex);
            return false;
        }
    }
}