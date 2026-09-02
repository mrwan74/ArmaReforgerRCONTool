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
            AppLogger.Warn("[UrlLauncherService:Launch] OpenUrlAsync aborted for empty URL.");
            return false;
        }

        var trimmedUrl = url.Trim();
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info($"[UrlLauncherService:Launch] Launching external browser for: '{trimmedUrl}'");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = trimmedUrl,
                    UseShellExecute = true
                });
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Info($"[UrlLauncherService:Launch] Windows shell launch complete in {elapsedMs:F2}ms.");
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
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                AppLogger.Info($"[UrlLauncherService:Launch] macOS open launch complete in {elapsedMs:F2}ms.");
                return true;
            }

            var xdgOpenPath = ResolveUnixExecutable("xdg-open", "/usr/bin/xdg-open");
            Process.Start(new ProcessStartInfo
            {
                FileName = xdgOpenPath,
                Arguments = $"\"{trimmedUrl}\"",
                UseShellExecute = false
            });
            var linuxElapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[UrlLauncherService:Launch] Linux xdg-open launch complete in {linuxElapsedMs:F2}ms.");
            return true;
        }
        catch (Win32Exception winEx)
        {
            AppLogger.Error($"[UrlLauncherService:Launch] Win32 error for '{trimmedUrl}': {winEx.Message}", winEx);
            await FallbackCopyToClipboardAsync(trimmedUrl);
            return false;
        }
        catch (FileNotFoundException fnfEx)
        {
            AppLogger.Error($"[UrlLauncherService:Launch] Browser launcher not found: {fnfEx.Message}", fnfEx);
            await FallbackCopyToClipboardAsync(trimmedUrl);
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Error($"[UrlLauncherService:Launch] Process launch invalid: {invEx.Message}", invEx);
            await FallbackCopyToClipboardAsync(trimmedUrl);
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UrlLauncherService:Launch] Unexpected error opening browser: {ex.Message}", ex);
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