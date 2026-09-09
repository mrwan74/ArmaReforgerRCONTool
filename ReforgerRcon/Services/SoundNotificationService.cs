using Avalonia.Platform;
using NetCoreAudio;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace ReforgerRcon.Services;

public enum SoundAlertType
{
    DefaultNotification,
    PlayerJoined,
    PlayerLeft,
    WatchlistAlert,
    WarningAlert,
    CriticalError
}

public static partial class SoundNotificationService
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private const uint MbIconWarning = 0x00000030;
    private const uint MbIconInformation = 0x00000040;

    private static readonly SemaphoreSlim AudioLock = new(1, 1);
    private static readonly Player AudioPlayer = new();
    private static readonly ConcurrentDictionary<SoundAlertType, long> LastPlayedTimestamp = new();

    [LibraryImport("user32.dll", EntryPoint = "MessageBeep")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint uType);

    public static void PlayAlert(SoundAlertType alertType)
    {
        var now = Stopwatch.GetTimestamp();
        if (LastPlayedTimestamp.TryGetValue(alertType, out var lastTicks))
        {
            var elapsedSec = Stopwatch.GetElapsedTime(lastTicks).TotalSeconds;
            if (elapsedSec < 1.0)
            {
                AppLogger.Trace($"[SoundNotificationService:Play] Debounced rapid duplicate alert: {alertType} (Elapsed: {elapsedSec:F2}s).");
                return;
            }
        }
        LastPlayedTimestamp[alertType] = now;

        _ = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            using var timing = AppLogger.Measure($"SoundNotificationService.PlayAlert({alertType})");

            try
            {
                await AudioLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    AppLogger.Debug($"[SoundNotificationService:Play] Playing audio alert: {alertType} on OS: {RuntimeInformation.OSDescription}");

                    if (await TryPlayCustomAudioFileAsync(alertType).ConfigureAwait(false))
                    {
                        sw.Stop();
                        AppLogger.Info($"[SoundNotificationService:Play] Custom audio played for {alertType} in {sw.ElapsedMilliseconds}ms via NetCoreAudio.");
                        return;
                    }

                    AppLogger.Debug($"[SoundNotificationService:Play] No custom audio asset for {alertType}. Using platform fallback.");

                    if (OperatingSystem.IsWindows())
                    {
                        PlayWindowsFallbackSound(alertType);
                    }
                    else
                    {
                        AppLogger.Trace($"[SoundNotificationService:Play] Emitting terminal beep for {alertType}.");
                        Console.Beep();
                    }
                }
                finally
                {
                    AudioLock.Release();
                }

                sw.Stop();
                AppLogger.Debug($"[SoundNotificationService:Play] Platform sound fallback complete for {alertType} in {sw.ElapsedMilliseconds}ms.");
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[SoundNotificationService:Play] Non-fatal audio notice for {alertType}: {ex.Message}");
            }
        });
    }

    private static async Task<bool> TryPlayCustomAudioFileAsync(SoundAlertType alertType)
    {
        var candidateNames = GetCandidateFileNames(alertType);

        foreach (var fileName in candidateNames)
        {
            var avaresUri = new Uri($"avares://ReforgerRcon/Assets/audio/{fileName}");
            if (AssetLoader.Exists(avaresUri))
            {
                try
                {
                    AppLogger.Trace($"[SoundNotificationService:Asset] Opening asset stream: {avaresUri}");
                    await using var stream = AssetLoader.Open(avaresUri);
                    if (await PlayAudioStreamAsync(stream, fileName).ConfigureAwait(false))
                    {
                        AppLogger.Info($"[SoundNotificationService:Asset] Played asset sound: '{fileName}' ({avaresUri})");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[SoundNotificationService:Asset] Asset notice for '{avaresUri}': {ex.Message}");
                }
            }

            var rootAvaresUri = new Uri($"avares://ReforgerRcon/Assets/{fileName}");
            if (AssetLoader.Exists(rootAvaresUri))
            {
                try
                {
                    AppLogger.Trace($"[SoundNotificationService:Asset] Opening root asset stream: {rootAvaresUri}");
                    await using var stream = AssetLoader.Open(rootAvaresUri);
                    if (await PlayAudioStreamAsync(stream, fileName).ConfigureAwait(false))
                    {
                        AppLogger.Info($"[SoundNotificationService:Asset] Played root asset sound: '{fileName}' ({rootAvaresUri})");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[SoundNotificationService:Asset] Root asset notice for '{rootAvaresUri}': {ex.Message}");
                }
            }

            var diskPaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Assets", "audio", fileName),
                Path.Combine(AppContext.BaseDirectory, "assets", "audio", fileName),
                Path.Combine(AppContext.BaseDirectory, "audio", fileName),
                Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
                Path.Combine(AppContext.BaseDirectory, fileName)
            };

            foreach (var diskPath in diskPaths.Where(File.Exists))
            {
                try
                {
                    AppLogger.Trace($"[SoundNotificationService:Disk] Found audio file on disk: '{diskPath}'. Playing...");
                    await AudioPlayer.Play(diskPath).ConfigureAwait(false);
                    AppLogger.Info($"[SoundNotificationService:Disk] Playback started for '{diskPath}'");
                    return true;
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[SoundNotificationService:Disk] Play notice for '{diskPath}': {ex.Message}");
                }
            }
        }

        return false;
    }

    private static async Task<bool> PlayAudioStreamAsync(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string? tempFile = null;
        try
        {
            tempFile = Path.Combine(Path.GetTempPath(), $"arrt_audio_{Guid.NewGuid():N}_{fileName}");
            await using (var fileStream = File.Create(tempFile))
            {
                await stream.CopyToAsync(fileStream).ConfigureAwait(false);
            }

            AppLogger.Debug($"[SoundNotificationService:Stream] Extracted audio to '{tempFile}'. Calling Play()...");
            try
            {
                await AudioPlayer.Play(tempFile).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[SoundNotificationService:Stream] Play notice: {ex.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[SoundNotificationService:Stream] Stream copy notice: {ex.Message}");
            return false;
        }
        finally
        {
            if (tempFile != null)
            {
                var fileToDelete = tempFile;
                _ = Task.Delay(15000).ContinueWith(_ =>
                {
                    try
                    {
                        if (File.Exists(fileToDelete))
                        {
                            File.Delete(fileToDelete);
                            AppLogger.Trace($"[SoundNotificationService:Stream] Cleaned up temporary audio: {fileToDelete}");
                        }
                    }
                    catch (Exception cleanupEx)
                    {
                        // Temporary audio file may still be locked by Windows media player; ignore deletion failure
                        AppLogger.Trace($"[SoundNotificationService:Cleanup] Delayed audio cleanup notice for '{fileToDelete}': {cleanupEx.Message}");
                    }
                }, TaskScheduler.Default);
            }
        }
    }

    private static string[] GetCandidateFileNames(SoundAlertType alertType) => alertType switch
    {
        SoundAlertType.PlayerJoined => [
            "join.mp3", "join.wav",
            "player_joined.mp3", "player_joined.wav",
            "connect.mp3", "connect.wav",
            "joined.mp3", "joined.wav"
        ],
        SoundAlertType.PlayerLeft => [
            "leave.mp3", "leave.wav",
            "player_left.mp3", "player_left.wav",
            "disconnect.mp3", "disconnect.wav",
            "left.mp3", "left.wav"
        ],
        SoundAlertType.WatchlistAlert => [
            "watchlist.mp3", "watchlist.wav",
            "alert.mp3", "alert.wav",
            "watchlist_alert.mp3", "watchlist_alert.wav",
            "ping.mp3", "ping.wav"
        ],
        SoundAlertType.WarningAlert => [
            "warning.mp3", "warning.wav",
            "warn.mp3", "warn.wav",
            "alert.mp3", "alert.wav"
        ],
        SoundAlertType.CriticalError => [
            "error.mp3", "error.wav",
            "critical.mp3", "critical.wav",
            "fault.mp3", "fault.wav"
        ],
        _ => [
            "notification.mp3", "notification.wav",
            "bell.mp3", "bell.wav",
            "notify.mp3", "notify.wav",
            "default.mp3", "default.wav"
        ]
    };

    [SupportedOSPlatform("windows")]
    private static void PlayWindowsFallbackSound(SoundAlertType alertType)
    {
        try
        {
            uint soundType = alertType switch
            {
                SoundAlertType.CriticalError => MbIconError,
                SoundAlertType.WatchlistAlert or SoundAlertType.WarningAlert => MbIconWarning,
                SoundAlertType.PlayerJoined or SoundAlertType.PlayerLeft => MbIconInformation,
                _ => MbOk
            };

            bool success = MessageBeep(soundType);
            AppLogger.Debug($"[SoundNotificationService:Windows] MessageBeep for {alertType} (Type: 0x{soundType:X8}, Success={success}).");
        }
        catch (Win32Exception winEx)
        {
            AppLogger.Trace($"[SoundNotificationService:Windows] Win32 MessageBeep notice: {winEx.Message}");
        }
    }
}