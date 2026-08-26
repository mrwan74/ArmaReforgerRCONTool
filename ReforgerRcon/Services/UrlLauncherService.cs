using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public static class UrlLauncherService
{
    private static readonly string[] UnixStandardBinDirectories = ["/usr/bin", "/bin", "/usr/local/bin"];

    public static async Task<bool> OpenUrlAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            AppLogger.Warn("[UrlLauncherService] OpenUrlAsync aborted for empty URL.");
            return false;
        }

        var trimmedUrl = url.Trim();
        AppLogger.Info($"[UrlLauncherService] Launching external browser for: '{trimmedUrl}'");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = trimmedUrl,
                    UseShellExecute = true
                });
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                var macOpenPath = ResolveUnixExecutable("open", "/usr/bin/open");
                Process.Start(new ProcessStartInfo
                {
                    FileName = macOpenPath,
                    Arguments = $"\"{trimmedUrl}\"",
                    UseShellExecute = false
                });
                return true;
            }

            var xdgOpenPath = ResolveUnixExecutable("xdg-open", "/usr/bin/xdg-open");
            Process.Start(new ProcessStartInfo
            {
                FileName = xdgOpenPath,
                Arguments = $"\"{trimmedUrl}\"",
                UseShellExecute = false
            });
            return true;
        }
        catch (Win32Exception winEx)
        {
            AppLogger.Error($"[UrlLauncherService] Win32 shell execution error for '{trimmedUrl}': {winEx.Message}", winEx);
            await FallbackCopyToClipboardAsync(trimmedUrl);
            return false;
        }
        catch (FileNotFoundException fnfEx)
        {
            AppLogger.Error($"[UrlLauncherService] Browser launcher binary not found: {fnfEx.Message}", fnfEx);
            await FallbackCopyToClipboardAsync(trimmedUrl);
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Error($"[UrlLauncherService] Process launch invalid in current state: {invEx.Message}", invEx);
            await FallbackCopyToClipboardAsync(trimmedUrl);
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UrlLauncherService] Unexpected error launching web browser: {ex.Message}", ex);
            await FallbackCopyToClipboardAsync(trimmedUrl);
            return false;
        }
    }

    private static async Task FallbackCopyToClipboardAsync(string url)
    {
        var clipboardSuccess = await ClipboardService.SetTextAsync(url);
        if (clipboardSuccess)
        {
            ToastNotificationService.Instance.ShowToast(
                "Link Copied to Clipboard",
                $"Unable to open browser automatically. Copied URL to clipboard: {url}",
                "URL_FALLBACK_CLIPBOARD"
            );
        }
    }

    private static string ResolveUnixExecutable(string binaryName, string defaultFallback)
    {
        foreach (var directory in UnixStandardBinDirectories)
        {
            var candidatePath = Path.Combine(directory, binaryName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return defaultFallback;
    }
}