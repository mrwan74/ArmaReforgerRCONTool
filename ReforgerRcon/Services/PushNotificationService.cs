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
    private const string AppIconResourceUri = "avares://ARRT/Assets/app.ico";

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
                    AppLogger.Info($"[PushNotificationService:Init] Attached event listener to NativeNotificationManager.Current (Active={manager.ActiveNotifications.Count}).");
                }
                else
                {
                    AppLogger.Warn("[PushNotificationService:Init] NativeNotificationManager.Current is null.");
                }
            }
            catch (InvalidOperationException invEx)
            {
                AppLogger.Error($"[PushNotificationService:Init] InvalidOperationException: {invEx.Message}", invEx);
            }
            catch (COMException comEx)
            {
                AppLogger.Error($"[PushNotificationService:Init] COM failure (0x{comEx.HResult:X8}): {comEx.Message}", comEx);
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
                AppLogger.Trace($"[PushNotificationService:Icon] Embedded icon notice: {ex.Message}");
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
                    AppLogger.Trace($"[PushNotificationService:Icon] Disk icon notice for '{path}': {ex.Message}");
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

            AppLogger.Info($"[PushNotificationService:Event] Notification complete: Id={e.NotificationId}, Activated={e.IsActivated}, Cancelled={e.IsCancelled}, ActionTag='{e.ActionTag ?? "none"}'", context);

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
            AppLogger.Error($"[PushNotificationService:Event] InvalidOperationException: {invEx.Message}", invEx);
        }
        catch (ArgumentException argEx)
        {
            AppLogger.Error($"[PushNotificationService:Event] ArgumentException: {argEx.Message}", argEx);
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

                AppLogger.Debug($"[PushNotificationService:Dispatch] Preparing notification: Title='{cleanTitle}', Category='{category}', Length={cleanMessage.Length} chars...");

                var manager = NativeNotificationManager.Current;
                if (manager == null)
                {
                    AppLogger.Warn("[PushNotificationService:Dispatch] NativeNotificationManager.Current is null.");
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

                    notification.Show();
                    sw.Stop();

                    AppLogger.Info($"[PushNotificationService:Dispatch] Notification displayed in {sw.ElapsedMilliseconds}ms: '{cleanTitle}' (Id: #{notification.Id}, Category: [{category}])");

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
                    AppLogger.Warn($"[PushNotificationService:Dispatch] Failed creating notification for category '{category}' after {sw.ElapsedMilliseconds}ms.");
                }
            }
            catch (COMException comEx)
            {
                sw.Stop();
                AppLogger.Error($"[PushNotificationService:Dispatch] Windows COM error (0x{comEx.HResult:X8}): {comEx.Message}", comEx, new Dictionary<string, object?>
                {
                    ["title"] = title,
                    ["category"] = category,
                    ["hresult"] = comEx.HResult
                });
                ToastNotificationService.Instance.ShowWarning("Native Notification Warning", "Windows notification service could not display the push notification.");
            }
            catch (UnauthorizedAccessException authEx)
            {
                sw.Stop();
                AppLogger.Warn($"[PushNotificationService:Dispatch] Access denied: {authEx.Message}", authEx);
            }
            catch (InvalidOperationException invEx)
            {
                sw.Stop();
                AppLogger.Warn($"[PushNotificationService:Dispatch] Invalid operation: {invEx.Message}", invEx);
            }
            catch (TimeoutException timeEx)
            {
                sw.Stop();
                AppLogger.Warn($"[PushNotificationService:Dispatch] Dispatch timed out: {timeEx.Message}", timeEx);
            }
            catch (OperationCanceledException opEx)
            {
                sw.Stop();
                AppLogger.Trace($"[PushNotificationService:Dispatch] Dispatch canceled: {opEx.Message}");
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