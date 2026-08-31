using Avalonia.Labs.Notifications;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Collections.Generic;
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
    private const string AppIconFileName = "app.ico";
    private const string AppIconResourceUri = "avares://ReforgerRcon/Assets/app.ico";

    private static bool _isHooked;
    private static readonly Lock InitLock = new();
    private static Bitmap? _cachedAppIcon;
    private static readonly Lock IconLock = new();

    public static void Initialize()
    {
        lock (InitLock)
        {
            if (_isHooked) return;

            try
            {
                var manager = NativeNotificationManager.Current;
                if (manager != null)
                {
                    manager.NotificationCompleted += OnNotificationCompleted;
                    _isHooked = true;
                    AppLogger.Info($"[PushNotificationService] Attached lifecycle event listener to NativeNotificationManager.Current (Active Notifications: {manager.ActiveNotifications.Count}).");
                }
                else
                {
                    AppLogger.Warn("[PushNotificationService] NativeNotificationManager.Current is null. Native notification manager not yet instantiated.");
                }
            }
            catch (InvalidOperationException invEx)
            {
                AppLogger.Error($"[PushNotificationService] Invalid operation attaching notification event listener: {invEx.Message}", invEx);
            }
            catch (COMException comEx)
            {
                AppLogger.Error($"[PushNotificationService] COM failure attaching notification event listener (HRESULT: 0x{comEx.HResult:X8}): {comEx.Message}", comEx);
            }
        }
    }

    private static Bitmap? GetAppIconBitmap()
    {
        if (_cachedAppIcon != null) return _cachedAppIcon;

        lock (IconLock)
        {
            if (_cachedAppIcon != null) return _cachedAppIcon;

            try
            {
                var avaresUri = new Uri(AppIconResourceUri);
                if (AssetLoader.Exists(avaresUri))
                {
                    using var stream = AssetLoader.Open(avaresUri);
                    _cachedAppIcon = new Bitmap(stream);
                    return _cachedAppIcon;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[PushNotificationService] Embedded app icon bitmap load notice: {ex.Message}");
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
                    using var fileStream = File.OpenRead(path);
                    _cachedAppIcon = new Bitmap(fileStream);
                    return _cachedAppIcon;
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[PushNotificationService] Disk icon bitmap load notice for '{path}': {ex.Message}");
                }
            }
        }

        return _cachedAppIcon;
    }

    private static void OnNotificationCompleted(object? sender, NativeNotificationCompletedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        try
        {
            var context = new Dictionary<string, object?>
            {
                ["notification_id"] = e.NotificationId,
                ["is_activated"] = e.IsActivated,
                ["is_cancelled"] = e.IsCancelled,
                ["action_tag"] = e.ActionTag,
                ["user_data"] = e.UserData?.ToString()
            };

            AppLogger.Info($"[PushNotificationService:Event] Notification completed: Id={e.NotificationId}, Activated={e.IsActivated}, Cancelled={e.IsCancelled}, ActionTag='{e.ActionTag ?? "none"}', UserData='{e.UserData ?? "none"}'", context);

            AppLogger.TrackEvent("native_notification_completed", new Dictionary<string, object>
            {
                ["notification_id"] = e.NotificationId?.ToString(CultureInfo.InvariantCulture) ?? "0",
                ["is_activated"] = e.IsActivated,
                ["is_cancelled"] = e.IsCancelled,
                ["action_tag"] = e.ActionTag ?? "none"
            });
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Error($"[PushNotificationService] InvalidOperationException in OnNotificationCompleted: {invEx.Message}", invEx);
        }
        catch (ArgumentException argEx)
        {
            AppLogger.Error($"[PushNotificationService] ArgumentException in OnNotificationCompleted: {argEx.Message}", argEx);
        }
    }

    public static void SendNotification(string title, string message, string category = "default")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        _ = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            using var timing = AppLogger.Measure($"PushNotificationService.SendNotification('{title}', Category: '{category}')");

            try
            {
                Initialize();

                var cleanTitle = AppLogger.SanitizeSensitiveData(title.Trim());
                var cleanMessage = AppLogger.SanitizeSensitiveData(message?.Trim() ?? string.Empty);

                AppLogger.Debug($"[PushNotificationService] Preparing native OS push notification: Title='{cleanTitle}', Category='{category}', MessageLength={cleanMessage.Length} chars on {RuntimeInformation.OSDescription}...");

                var manager = NativeNotificationManager.Current;
                if (manager == null)
                {
                    AppLogger.Warn("[PushNotificationService] NativeNotificationManager.Current is null. Native notification subsystem is unavailable on this platform.");
                    return;
                }

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

                    AppLogger.Debug($"[PushNotificationService] Created notification instance: Id={notification.Id}, Category='{notification.Category}', ActionsCount={notification.Actions?.Count ?? 0}, HasIcon={notification.Icon != null}. Invoking .Show()...");

                    notification.Show();
                    sw.Stop();

                    AppLogger.Info($"[PushNotificationService] Native push notification successfully dispatched in {sw.ElapsedMilliseconds} ms: '{cleanTitle}' (Id: #{notification.Id}, Category: [{category}])");

                    AppLogger.TrackEvent("push_notification_dispatched", new Dictionary<string, object>
                    {
                        ["title"] = cleanTitle,
                        ["category"] = category,
                        ["notification_id"] = notification.Id,
                        ["duration_ms"] = sw.ElapsedMilliseconds,
                        ["os"] = RuntimeInformation.OSDescription
                    });
                }
                else
                {
                    sw.Stop();
                    AppLogger.Warn($"[PushNotificationService] Failed creating native notification instance for category '{category}' after {sw.ElapsedMilliseconds} ms.");
                }
            }
            catch (COMException comEx)
            {
                sw.Stop();
                AppLogger.Error($"[PushNotificationService] Windows COM error delivering native notification (HRESULT: 0x{comEx.HResult:X8}): {comEx.Message}", comEx, new Dictionary<string, object?>
                {
                    ["title"] = title,
                    ["category"] = category,
                    ["hresult"] = comEx.HResult
                });
                ToastNotificationService.Instance.ShowWarning("Native Notification Warning", "Windows notification service was unable to display the system push notification.");
            }
            catch (UnauthorizedAccessException authEx)
            {
                sw.Stop();
                AppLogger.Warn($"[PushNotificationService] Access denied creating notification: {authEx.Message}", authEx);
            }
            catch (InvalidOperationException invEx)
            {
                sw.Stop();
                AppLogger.Warn($"[PushNotificationService] Notification operation invalid in current state: {invEx.Message}", invEx);
            }
            catch (TimeoutException timeEx)
            {
                sw.Stop();
                AppLogger.Warn($"[PushNotificationService] Native notification dispatch timed out: {timeEx.Message}", timeEx);
            }
            catch (OperationCanceledException opEx)
            {
                sw.Stop();
                AppLogger.Trace($"[PushNotificationService] Notification dispatch canceled: {opEx.Message}");
            }
        });
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