using System;
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
            AppLogger.Warn("[UrlLauncherService] Attempted to open an empty or null URL string.");
            return false;
        }

        var trimmedUrl = url.Trim();
        AppLogger.Info($"[UrlLauncherService] Requesting external web browser dispatch for target: {trimmedUrl}");

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = trimmedUrl,
                    UseShellExecute = true
                });
                AppLogger.Debug($"[UrlLauncherService] ShellExecute process dispatched on Windows for: {trimmedUrl}");
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
                AppLogger.Debug($"[UrlLauncherService] MacOS open process dispatched for: {trimmedUrl}");
                return true;
            }

            var xdgOpenPath = ResolveUnixExecutable("xdg-open", "/usr/bin/xdg-open");
            Process.Start(new ProcessStartInfo
            {
                FileName = xdgOpenPath,
                Arguments = $"\"{trimmedUrl}\"",
                UseShellExecute = false
            });
            AppLogger.Debug($"[UrlLauncherService] Linux xdg-open process dispatched for: {trimmedUrl}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[UrlLauncherService] Failed to launch external browser for URL '{trimmedUrl}': {ex.Message}", ex);

            var clipboardSuccess = await ClipboardService.SetTextAsync(trimmedUrl);
            if (clipboardSuccess)
            {
                ToastNotificationService.Instance.ShowToast(
                    "Link Copied to Clipboard",
                    $"Unable to launch default browser. Copied URL to clipboard: {trimmedUrl}",
                    "URL_FALLBACK_CLIPBOARD"
                );
            }
            return false;
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