using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public static class ClipboardService
{
    public static async Task<bool> SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;

        if (cancellationToken.IsCancellationRequested)
        {
            AppLogger.Debug($"[ClipboardService:Copy] Operation pre-cancelled before invocation (Thread: T{threadId:D2}).");
            return false;
        }

        if (string.IsNullOrEmpty(text))
        {
            AppLogger.Debug($"[ClipboardService:Copy] SetTextAsync bypassed: payload is null or 0-length empty string (Thread: T{threadId:D2}).");
            return false;
        }

        var textLength = text.Length;
        var sanitizedPreview = AppLogger.SanitizeSensitiveData(textLength > 80 ? text[..80] + "..." : text);

        var context = new Dictionary<string, object?>
        {
            ["thread_id"] = threadId,
            ["char_count"] = textLength,
            ["preview"] = sanitizedPreview,
            ["os_platform"] = RuntimeInformation.OSDescription
        };

        AppLogger.Trace($"[ClipboardService:Copy] Initiating clipboard write ({textLength} chars, Preview='{sanitizedPreview}', Thread=T{threadId:D2})...", context);

        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var mainWindow = desktop.MainWindow;
                if (mainWindow != null)
                {
                    var clipboard = mainWindow.Clipboard;
                    if (clipboard != null)
                    {
                        await clipboard.SetTextAsync(text).ConfigureAwait(false);
                        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        AppLogger.Info($"[ClipboardService:Copy] Successfully transferred {textLength} char(s) to system clipboard in {elapsedMs:F2}ms (Thread=T{threadId:D2}, Window='{mainWindow.Title}').", context);
                        return true;
                    }

                    AppLogger.Warn($"[ClipboardService:Copy] Target window clipboard provider is null (Window='{mainWindow.Title}', IsVisible={mainWindow.IsVisible}).", null, context);
                }
                else
                {
                    AppLogger.Warn("[ClipboardService:Copy] Desktop lifetime MainWindow instance is null.", null, context);
                }
            }
            else
            {
                AppLogger.Warn($"[ClipboardService:Copy] Application.Current.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime (Current: {Application.Current?.ApplicationLifetime?.GetType().FullName ?? "null"}).", null, context);
            }

            var elapsedFailMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Warn($"[ClipboardService:Copy] Native clipboard access unavailable after {elapsedFailMs:F2}ms.", null, context);
            ToastNotificationService.Instance.ShowWarning("Clipboard Unavailable", "Unable to access system clipboard from the active application window.");
            return false;
        }
        catch (OperationCanceledException opEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[ClipboardService:Copy] Clipboard write cancelled after {elapsedMs:F2}ms: {opEx.Message}", context);
            return false;
        }
        catch (Win32Exception winEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["native_error_code"] = winEx.NativeErrorCode;
            context["win32_error_code"] = winEx.ErrorCode;
            AppLogger.Error($"[ClipboardService:Copy] Native Win32 error locking clipboard after {elapsedMs:F2}ms (NativeCode: {winEx.NativeErrorCode}, Win32Code: {winEx.ErrorCode}): {winEx.Message}", winEx, context);
            ToastNotificationService.Instance.ShowError("Clipboard Locked", $"Another process is currently locking the system clipboard (Native Error: {winEx.NativeErrorCode}).");
            return false;
        }
        catch (COMException comEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["hresult"] = string.Create(CultureInfo.InvariantCulture, $"0x{comEx.HResult:X8}");
            AppLogger.Error($"[ClipboardService:Copy] Windows COM subsystem rejected clipboard write after {elapsedMs:F2}ms (HResult: 0x{comEx.HResult:X8}): {comEx.Message}", comEx, context);
            ToastNotificationService.Instance.ShowError("Clipboard Error", $"OS COM interface rejected clipboard assignment (HResult: 0x{comEx.HResult:X8}).");
            return false;
        }
        catch (TimeoutException timeEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Warn($"[ClipboardService:Copy] Timed out waiting for system clipboard lock after {elapsedMs:F2}ms: {timeEx.Message}", null, context);
            ToastNotificationService.Instance.ShowWarning("Clipboard Timeout", "Timed out waiting to acquire system clipboard mutex lock.");
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Warn($"[ClipboardService:Copy] Invalid thread state during clipboard assignment after {elapsedMs:F2}ms: {invEx.Message}", null, context);
            ToastNotificationService.Instance.ShowWarning("Clipboard Notice", "Unable to access clipboard in current UI thread state.");
            return false;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["exception_type"] = ex.GetType().FullName;
            AppLogger.Error($"[ClipboardService:Copy] Unhandled failure writing to clipboard after {elapsedMs:F2}ms: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Clipboard Error", $"Failed copying text to clipboard: {ex.Message}");
            return false;
        }
    }
}