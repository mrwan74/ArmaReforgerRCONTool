using Avalonia.Labs.Notifications;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Avalonia internal avares resource schema paths")]
public static class PushNotificationService
{
    private const string ContextThreadId = "thread_id";
    private const string ContextProcessId = "process_id";
    private const string ContextPlatform = "platform";
    private const string ContextOsDescription = "os_description";
    private const string ContextCategory = "category";
    private const string ContextTitle = "title";
    private const string ContextMessageLength = "message_length";
    private const string ContextBinaryPath = "binary_path";
    private const string ContextExitCode = "exit_code";
    private const string ContextElapsedMs = "elapsed_ms";
    private const string ContextError = "error";
    private const string ContextNotificationId = "notification_id";

    private const string AppIconFileName = "app.ico";
    private const string AppIconResourceUri = "avares://ARRT/Assets/app.ico";

    private static readonly string[] LinuxStandardBinDirectories =
    [
        "/usr/bin",
        "/bin",
        "/usr/local/bin",
        "/snap/bin",
        "/opt/bin"
    ];

    private static readonly string[] MacStandardBinDirectories =
    [
        "/usr/bin",
        "/bin",
        "/usr/local/bin",
        "/opt/homebrew/bin"
    ];

    private static bool _isHooked;
    private static readonly Lock InitLock = new();
    private static Bitmap? _cachedAppIcon;
    private static readonly Lock IconLock = new();
    private static string? _resolvedLinuxBinaryPath;
    private static string? _resolvedMacBinaryPath;
    private static bool _hasCheckedLinuxBinary;
    private static bool _hasCheckedMacBinary;

    public static void Initialize()
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            [ContextThreadId] = Environment.CurrentManagedThreadId,
            [ContextProcessId] = Environment.ProcessId,
            [ContextPlatform] = RuntimeInformation.OSArchitecture.ToString(),
            [ContextOsDescription] = RuntimeInformation.OSDescription
        };

        AppLogger.Trace("[PushNotificationService:Init] Entering Initialize()...", context);

        if (!OperatingSystem.IsWindows())
        {
            AppLogger.Debug($"[PushNotificationService:Init] Non-Windows OS ({RuntimeInformation.OSDescription}): WinRT manager initialization bypassed.", context);
            return;
        }

        lock (InitLock)
        {
            if (_isHooked)
            {
                AppLogger.Trace("[PushNotificationService:Init] Already hooked to NativeNotificationManager.Current.", context);
                return;
            }

            try
            {
                AppLogger.Debug("[PushNotificationService:Init] Attaching event listener to NativeNotificationManager.Current...", context);
                var manager = NativeNotificationManager.Current;
                if (manager != null)
                {
                    manager.NotificationCompleted += OnNotificationCompleted;
                    _isHooked = true;
                    var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    context[ContextElapsedMs] = elapsedMs;
                    context["active_notifications_count"] = manager.ActiveNotifications.Count;
                    AppLogger.Info($"[PushNotificationService:Init] Attached to NativeNotificationManager.Current in {elapsedMs:F2}ms (Active={manager.ActiveNotifications.Count}).", context);
                }
                else
                {
                    AppLogger.Warn("[PushNotificationService:Init] NativeNotificationManager.Current is null. Windows Action Center notifications may be inactive.", null, context);
                    Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                        "Notification System Warning",
                        "Windows notification manager is unavailable. Fallback notifications will be used."));
                }
            }
            catch (COMException comEx)
            {
                context["hresult"] = string.Create(CultureInfo.InvariantCulture, $"0x{comEx.HResult:X8}");
                context[ContextError] = comEx.Message;
                AppLogger.Error($"[PushNotificationService:Init] Windows COM error attaching notification listener (0x{comEx.HResult:X8}): {comEx.Message}", comEx, context);
                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                    "Notification System Warning",
                    $"Windows Action Center error (0x{comEx.HResult:X8}): {comEx.Message}"));
            }
            catch (Exception ex)
            {
                context[ContextError] = ex.Message;
                AppLogger.Error($"[PushNotificationService:Init] Unexpected error during notification manager hook: {ex.Message}", ex, context);
                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                    "Notification System Warning",
                    "Unable to hook Windows desktop notifications: " + ex.Message));
            }
        }
    }

    private static Bitmap? GetAppIconBitmap()
    {
        if (_cachedAppIcon != null) return _cachedAppIcon;

        lock (IconLock)
        {
            if (_cachedAppIcon != null) return _cachedAppIcon;

            var start = Stopwatch.GetTimestamp();
            try
            {
                AppLogger.Trace($"[PushNotificationService:Icon] Attempting to load embedded icon from '{AppIconResourceUri}'...");
                var avaresUri = new Uri(AppIconResourceUri);
                if (AssetLoader.Exists(avaresUri))
                {
                    using var stream = AssetLoader.Open(avaresUri);
                    _cachedAppIcon = new Bitmap(stream);
                    AppLogger.Debug($"[PushNotificationService:Icon] Embedded icon loaded in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
                    return _cachedAppIcon;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[PushNotificationService:Icon] Embedded asset load notice: {ex.Message}");
            }

            var candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", AppIconFileName),
                Path.Combine(AppContext.BaseDirectory, "assets", AppIconFileName),
                Path.Combine(AppContext.BaseDirectory, "appdata", AppIconFileName),
                Path.Combine(AppContext.BaseDirectory, AppIconFileName)
            };

            foreach (var path in candidatePaths.Where(File.Exists))
            {
                try
                {
                    AppLogger.Trace($"[PushNotificationService:Icon] Loading icon from file system: '{path}'...");
                    using var fileStream = File.OpenRead(path);
                    _cachedAppIcon = new Bitmap(fileStream);
                    AppLogger.Debug($"[PushNotificationService:Icon] File icon loaded in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms from '{path}'.");
                    return _cachedAppIcon;
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[PushNotificationService:Icon] Disk icon read notice for '{path}': {ex.Message}");
                }
            }

            AppLogger.Debug("[PushNotificationService:Icon] No custom app icon found; native default will be used.");
        }

        return _cachedAppIcon;
    }

    private static void OnNotificationCompleted(object? sender, NativeNotificationCompletedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        var context = new Dictionary<string, object?>
        {
            [ContextNotificationId] = e.NotificationId,
            ["is_activated"] = e.IsActivated,
            ["is_cancelled"] = e.IsCancelled,
            ["action_tag"] = e.ActionTag,
            ["user_data"] = e.UserData?.ToString(),
            [ContextThreadId] = Environment.CurrentManagedThreadId
        };

        try
        {
            AppLogger.Info($"[PushNotificationService:Event] Notification #{e.NotificationId} completed (Activated={e.IsActivated}, Cancelled={e.IsCancelled}, ActionTag='{e.ActionTag ?? "none"}').", context);

            AppLogger.TrackEvent("native_notification_completed", new Dictionary<string, object>
            {
                [ContextNotificationId] = e.NotificationId?.ToString(CultureInfo.InvariantCulture) ?? "0",
                ["is_activated"] = e.IsActivated,
                ["is_cancelled"] = e.IsCancelled,
                ["action_tag"] = e.ActionTag ?? "none"
            });
        }
        catch (Exception ex)
        {
            context[ContextError] = ex.Message;
            AppLogger.Error($"[PushNotificationService:Event] Exception in NotificationCompleted handler: {ex.Message}", ex, context);
        }
    }

    public static void SendNotification(string title, string message, string category = "default")
    {
        var threadId = Environment.CurrentManagedThreadId;
        var processId = Environment.ProcessId;

        if (string.IsNullOrWhiteSpace(title))
        {
            AppLogger.Warn($"[PushNotificationService:Dispatch] SendNotification aborted: title is null or whitespace (Thread=T{threadId:D2}).");
            return;
        }

        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            using var timing = AppLogger.Measure($"PushNotificationService.SendNotification('{title}', Category: '{category}')");

            var cleanTitle = AppLogger.SanitizeSensitiveData(title.Trim());
            var cleanMessage = AppLogger.SanitizeSensitiveData(message?.Trim() ?? string.Empty);

            var context = new Dictionary<string, object?>
            {
                [ContextTitle] = cleanTitle,
                [ContextMessageLength] = cleanMessage.Length,
                [ContextCategory] = category,
                [ContextThreadId] = Environment.CurrentManagedThreadId,
                [ContextProcessId] = processId,
                [ContextPlatform] = RuntimeInformation.OSArchitecture.ToString(),
                [ContextOsDescription] = RuntimeInformation.OSDescription
            };

            AppLogger.Debug($"[PushNotificationService:Dispatch] Preparing notification: Title='{cleanTitle}', Category='{category}', Length={cleanMessage.Length} chars...", context);

            try
            {
                // 1. Windows: Native WinRT Notification Manager (Action Center)
                if (OperatingSystem.IsWindows())
                {
                    Initialize();
                    var manager = NativeNotificationManager.Current;
                    if (manager != null)
                    {
                        var notification = manager.CreateNotification(category);
                        if (notification != null)
                        {
                            notification.Title = cleanTitle;
                            notification.Message = cleanMessage;

                            var appIcon = GetAppIconBitmap();
                            if (appIcon != null)
                            {
                                notification.Icon = appIcon;
                            }

                            notification.Show();
                            sw.Stop();
                            context[ContextElapsedMs] = sw.ElapsedMilliseconds;
                            context[ContextNotificationId] = notification.Id;

                            AppLogger.Info($"[PushNotificationService:Dispatch] Windows Action Center notification displayed in {sw.ElapsedMilliseconds}ms: '{cleanTitle}' (Id: #{notification.Id}, Category: [{category}]).", context);

                            AppLogger.TrackEvent("push_notification_dispatched", new Dictionary<string, object>
                            {
                                [ContextTitle] = cleanTitle,
                                [ContextCategory] = category,
                                [ContextNotificationId] = notification.Id,
                                [ContextElapsedMs] = sw.ElapsedMilliseconds,
                                ["os"] = RuntimeInformation.OSDescription
                            });
                            return;
                        }

                        AppLogger.Warn($"[PushNotificationService:Dispatch] CreateNotification returned null for category '{category}'. Falling back to in-app toast.", null, context);
                    }
                    else
                    {
                        AppLogger.Warn("[PushNotificationService:Dispatch] NativeNotificationManager.Current is null. Falling back to in-app toast.", null, context);
                    }
                }
                // 2. Linux: Standard Freedesktop notify-send CLI
                else if (OperatingSystem.IsLinux() && TrySendLinuxNotification(cleanTitle, cleanMessage, category, context))
                {
                    sw.Stop();
                    context[ContextElapsedMs] = sw.ElapsedMilliseconds;
                    AppLogger.Info($"[PushNotificationService:Dispatch] Linux desktop notification displayed in {sw.ElapsedMilliseconds}ms: '{cleanTitle}'.", context);

                    AppLogger.TrackEvent("push_notification_dispatched", new Dictionary<string, object>
                    {
                        [ContextTitle] = cleanTitle,
                        [ContextCategory] = category,
                        [ContextElapsedMs] = sw.ElapsedMilliseconds,
                        ["os"] = "Linux"
                    });
                    return;
                }
                // 3. macOS: Native AppleScript osascript
                else if (OperatingSystem.IsMacOS() && TrySendMacNotification(cleanTitle, cleanMessage, context))
                {
                    sw.Stop();
                    context[ContextElapsedMs] = sw.ElapsedMilliseconds;
                    AppLogger.Info($"[PushNotificationService:Dispatch] macOS notification displayed in {sw.ElapsedMilliseconds}ms: '{cleanTitle}'.", context);

                    AppLogger.TrackEvent("push_notification_dispatched", new Dictionary<string, object>
                    {
                        [ContextTitle] = cleanTitle,
                        [ContextCategory] = category,
                        [ContextElapsedMs] = sw.ElapsedMilliseconds,
                        ["os"] = "macOS"
                    });
                    return;
                }

                // 4. Universal Fallback: In-app Toast Display
                sw.Stop();
                context[ContextElapsedMs] = sw.ElapsedMilliseconds;
                AppLogger.Info($"[PushNotificationService:Dispatch] Native notification unavailable; displayed via in-app toast in {sw.ElapsedMilliseconds}ms.", context);

                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowToast(cleanTitle, cleanMessage));
            }
            catch (COMException comEx)
            {
                sw.Stop();
                context[ContextElapsedMs] = sw.ElapsedMilliseconds;
                context["hresult"] = string.Create(CultureInfo.InvariantCulture, $"0x{comEx.HResult:X8}");
                context[ContextError] = comEx.Message;

                AppLogger.Error($"[PushNotificationService:Dispatch] Windows COM error delivering notification (HResult: 0x{comEx.HResult:X8}): {comEx.Message}", comEx, context);
                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                    "Desktop Notification Warning",
                    $"Windows notification daemon error: {comEx.Message}"));
            }
            catch (Exception ex)
            {
                sw.Stop();
                context[ContextElapsedMs] = sw.ElapsedMilliseconds;
                context[ContextError] = ex.Message;
                context["stack_trace"] = ex.StackTrace;

                AppLogger.Fatal($"[PushNotificationService:Dispatch] Critical unhandled failure delivering notification: {ex.Message}", ex, context);
                CrashReportService.HandleFatalException("PushNotificationService.SendNotification", ex, isTerminating: false);
                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowError(
                    "Notification Dispatch Error",
                    $"Failed delivering notification: {ex.Message}"));
            }
        });
    }

    private static bool TrySendLinuxNotification(string title, string message, string category, Dictionary<string, object?> context)
    {
        var start = Stopwatch.GetTimestamp();

        if (!_hasCheckedLinuxBinary)
        {
            _resolvedLinuxBinaryPath = ResolveExecutable("notify-send", LinuxStandardBinDirectories);
            _hasCheckedLinuxBinary = true;
            context[ContextBinaryPath] = _resolvedLinuxBinaryPath ?? "NOT_FOUND";

            if (string.IsNullOrEmpty(_resolvedLinuxBinaryPath))
            {
                AppLogger.Warn("[PushNotificationService:Linux] 'notify-send' is not installed in standard directories. Desktop notifications will fall back to in-app toasts.", null, context);
                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                    "Desktop Notifications Notice",
                    "The 'notify-send' tool was not found on your Linux desktop. Install 'libnotify-bin' to receive native tray alerts."));
            }
            else
            {
                AppLogger.Info($"[PushNotificationService:Linux] Resolved 'notify-send' binary at '{_resolvedLinuxBinaryPath}'.", context);
            }
        }

        if (string.IsNullOrEmpty(_resolvedLinuxBinaryPath) || !File.Exists(_resolvedLinuxBinaryPath))
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _resolvedLinuxBinaryPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);
            psi.ArgumentList.Add("-a");
            psi.ArgumentList.Add("ARMA Reforger RCON Tool");

            switch (category.ToLowerInvariant())
            {
                case "watchlist":
                case "system":
                    psi.ArgumentList.Add("-u");
                    psi.ArgumentList.Add("critical");
                    break;
                case "players":
                    psi.ArgumentList.Add("-u");
                    psi.ArgumentList.Add("normal");
                    break;
                default:
                    psi.ArgumentList.Add("-u");
                    psi.ArgumentList.Add("low");
                    break;
            }

            AppLogger.Trace($"[PushNotificationService:Linux] Spawning '{_resolvedLinuxBinaryPath}' with {psi.ArgumentList.Count} argument(s)...", context);

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                AppLogger.Error($"[PushNotificationService:Linux] Process.Start returned null for '{_resolvedLinuxBinaryPath}'.", null, context);
                return false;
            }

            bool exited = proc.WaitForExit(3000);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;

            if (!exited)
            {
                try
                {
                    proc.Kill();
                }
                catch (Exception killEx)
                {
                    AppLogger.Trace($"[PushNotificationService:Linux] Kill notice: {killEx.Message}");
                }

                AppLogger.Warn("[PushNotificationService:Linux] notify-send process timed out after 3000ms and was terminated.", null, context);
                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                    "Notification Timeout",
                    "Linux desktop notification daemon did not respond within 3 seconds."));
                return false;
            }

            if (proc.ExitCode != 0)
            {
                string stderr = string.Empty;
                try
                {
                    stderr = proc.StandardError.ReadToEnd().Trim();
                }
                catch (Exception readEx)
                {
                    AppLogger.Trace($"[PushNotificationService:Linux] Stderr read notice: {readEx.Message}");
                }

                context[ContextExitCode] = proc.ExitCode;
                context["stderr"] = stderr;

                AppLogger.Error($"[PushNotificationService:Linux] notify-send exited with error code {proc.ExitCode}: '{stderr}'.", null, context);
                Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                    "Desktop Notification Failed",
                    $"notify-send reported an error (Code {proc.ExitCode}): {stderr}"));
                return false;
            }

            AppLogger.Debug($"[PushNotificationService:Linux] notify-send succeeded in {elapsedMs:F2}ms.", context);
            return true;
        }
        catch (Win32Exception winEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context["native_error_code"] = winEx.NativeErrorCode;
            context[ContextError] = winEx.Message;

            AppLogger.Error($"[PushNotificationService:Linux] Win32 error executing notify-send (NativeCode={winEx.NativeErrorCode}): {winEx.Message}", winEx, context);
            Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                "Notification Error",
                $"Failed launching notify-send: {winEx.Message}"));
            return false;
        }
        catch (UnauthorizedAccessException authEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context[ContextError] = authEx.Message;

            AppLogger.Error($"[PushNotificationService:Linux] Permission denied executing '{_resolvedLinuxBinaryPath}': {authEx.Message}", authEx, context);
            Dispatcher.UIThread.Post(() => ToastNotificationService.Instance.ShowWarning(
                "Permission Denied",
                "System denied permission to execute notify-send."));
            return false;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context[ContextError] = ex.Message;

            AppLogger.Error($"[PushNotificationService:Linux] Unexpected exception executing notify-send: {ex.Message}", ex, context);
            return false;
        }
    }

    private static bool TrySendMacNotification(string title, string message, Dictionary<string, object?> context)
    {
        var start = Stopwatch.GetTimestamp();

        if (!_hasCheckedMacBinary)
        {
            _resolvedMacBinaryPath = ResolveExecutable("osascript", MacStandardBinDirectories);
            _hasCheckedMacBinary = true;
            context[ContextBinaryPath] = _resolvedMacBinaryPath ?? "NOT_FOUND";
        }

        if (string.IsNullOrEmpty(_resolvedMacBinaryPath) || !File.Exists(_resolvedMacBinaryPath))
        {
            AppLogger.Warn("[PushNotificationService:Mac] 'osascript' binary was not found.", null, context);
            return false;
        }

        try
        {
            var escapedTitle = title.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var escapedMessage = message.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var script = $"display notification \"{escapedMessage}\" with title \"{escapedTitle}\"";

            var psi = new ProcessStartInfo
            {
                FileName = _resolvedMacBinaryPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var proc = Process.Start(psi);
            if (proc == null) return false;

            bool exited = proc.WaitForExit(3000);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;

            if (!exited)
            {
                try { proc.Kill(); } catch { /* Bypassed */ }
                AppLogger.Warn("[PushNotificationService:Mac] osascript timed out after 3000ms.", null, context);
                return false;
            }

            if (proc.ExitCode != 0)
            {
                var stderr = proc.StandardError.ReadToEnd().Trim();
                context[ContextExitCode] = proc.ExitCode;
                context["stderr"] = stderr;
                AppLogger.Error($"[PushNotificationService:Mac] osascript exited with code {proc.ExitCode}: '{stderr}'", null, context);
                return false;
            }

            AppLogger.Debug($"[PushNotificationService:Mac] osascript succeeded in {elapsedMs:F2}ms.", context);
            return true;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context[ContextElapsedMs] = elapsedMs;
            context[ContextError] = ex.Message;
            AppLogger.Error($"[PushNotificationService:Mac] Exception executing osascript: {ex.Message}", ex, context);
            return false;
        }
    }

    private static string? ResolveExecutable(string binaryName, string[] standardDirectories)
    {
        var envPath = Environment.GetEnvironmentVariable("PATH");
        var searchDirectories = new List<string>(standardDirectories);

        if (!string.IsNullOrWhiteSpace(envPath))
        {
            var pathParts = envPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            searchDirectories.AddRange(pathParts);
        }

        foreach (var dir in searchDirectories.Distinct())
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    var fullPath = Path.Combine(dir, binaryName);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[PushNotificationService:Resolve] Path query exception for '{dir}': {ex.Message}");
            }
        }

        return null;
    }

    public static void SendPlayerJoinNotification(string playerName, string? location = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var msg = string.IsNullOrWhiteSpace(location) || location == "Unknown Region"
            ? $"{playerName} connected to the server."
            : $"{playerName} connected from {location}.";

        SendNotification("Player Joined", msg, "players");
    }

    public static void SendPlayerLeaveNotification(string playerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        SendNotification("Player Disconnected", $"{playerName} left the server.", "players");
    }

    public static void SendWatchlistNotification(string playerName, bool isJoining, string? location = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playerName);

        var action = isJoining ? "connected to" : "left";
        var locSuffix = isJoining && !string.IsNullOrWhiteSpace(location) && location != "Unknown Region"
            ? $" (Location: {location})"
            : string.Empty;

        SendNotification("⚠️ Watchlist Alert", $"Watchlisted player '{playerName}' {action} the server{locSuffix}.", "watchlist");
    }
}