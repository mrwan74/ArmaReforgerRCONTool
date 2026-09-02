using Avalonia.Platform;
using NetCoreAudio;
using System;
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

    [LibraryImport("user32.dll", EntryPoint = "MessageBeep")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint uType);

    public static void PlayAlert(SoundAlertType alertType)
    {
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
                }
                finally
                {
                    AudioLock.Release();
                }

                if (await TryPlayCustomAudioFileAsync(alertType).ConfigureAwait(false))
                {
                    sw.Stop();
                    AppLogger.Info($"[SoundNotificationService:Play] Custom audio played for {alertType} in {sw.ElapsedMilliseconds}ms via NetCoreAudio.");
                    return;
                }

                AppLogger.Debug($"[SoundNotificationService:Play] No custom audio asset for {alertType}. Using platform fallback.");

                await AudioLock.WaitAsync().ConfigureAwait(false);
                try
                {
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
            catch (Win32Exception winEx)
            {
                sw.Stop();
                AppLogger.Error($"[SoundNotificationService:Play] Win32 audio error for {alertType} (Code: {winEx.NativeErrorCode}): {winEx.Message}", winEx, new Dictionary<string, object?>
                {
                    ["alert_type"] = alertType.ToString(),
                    ["win32_code"] = winEx.NativeErrorCode,
                    ["os"] = RuntimeInformation.OSDescription
                });
            }
            catch (FileNotFoundException fnfEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService:Play] Audio file not found for {alertType}: {fnfEx.FileName}", fnfEx);
            }
            catch (DirectoryNotFoundException dnfEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService:Play] Directory missing: {dnfEx.Message}", dnfEx);
            }
            catch (UnauthorizedAccessException authEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService:Play] Access denied for {alertType}: {authEx.Message}", authEx);
            }
            catch (IOException ioEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService:Play] I/O error for {alertType}: {ioEx.Message}", ioEx);
            }
            catch (InvalidOperationException invEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService:Play] Invalid operation in NetCoreAudio: {invEx.Message}", invEx);
            }
            catch (OperationCanceledException opEx)
            {
                sw.Stop();
                AppLogger.Trace($"[SoundNotificationService:Play] Audio playback canceled for {alertType}: {opEx.Message}");
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
                catch (FileNotFoundException fnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService:Asset] Asset not found '{avaresUri}': {fnfEx.Message}");
                }
                catch (IOException ioEx)
                {
                    AppLogger.Warn($"[SoundNotificationService:Asset] I/O error on '{avaresUri}': {ioEx.Message}", ioEx);
                }
                catch (UnauthorizedAccessException authEx)
                {
                    AppLogger.Warn($"[SoundNotificationService:Asset] Access denied on '{avaresUri}': {authEx.Message}", authEx);
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
                catch (FileNotFoundException fnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService:Asset] Root asset not found '{rootAvaresUri}': {fnfEx.Message}");
                }
                catch (IOException ioEx)
                {
                    AppLogger.Warn($"[SoundNotificationService:Asset] I/O error on '{rootAvaresUri}': {ioEx.Message}", ioEx);
                }
                catch (UnauthorizedAccessException authEx)
                {
                    AppLogger.Warn($"[SoundNotificationService:Asset] Access denied on '{rootAvaresUri}': {authEx.Message}", authEx);
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
                catch (FileNotFoundException fnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService:Disk] File missing '{diskPath}': {fnfEx.Message}");
                }
                catch (DirectoryNotFoundException dnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService:Disk] Directory missing '{diskPath}': {dnfEx.Message}");
                }
                catch (IOException ioEx)
                {
                    AppLogger.Warn($"[SoundNotificationService:Disk] I/O error for '{diskPath}': {ioEx.Message}", ioEx);
                }
                catch (UnauthorizedAccessException authEx)
                {
                    AppLogger.Warn($"[SoundNotificationService:Disk] Access denied for '{diskPath}': {authEx.Message}", authEx);
                }
                catch (InvalidOperationException invEx)
                {
                    AppLogger.Warn($"[SoundNotificationService:Disk] NetCoreAudio error playing '{diskPath}': {invEx.Message}", invEx);
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
            await AudioPlayer.Play(tempFile).ConfigureAwait(false);
            return true;
        }
        catch (IOException ioEx)
        {
            AppLogger.Error($"[SoundNotificationService:Stream] I/O error extracting stream: {ioEx.Message}", ioEx);
            return false;
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Error($"[SoundNotificationService:Stream] Access denied writing temp audio: {authEx.Message}", authEx);
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Error($"[SoundNotificationService:Stream] NetCoreAudio failure: {invEx.Message}", invEx);
            return false;
        }
        finally
        {
            if (tempFile != null)
            {
                var fileToDelete = tempFile;
                _ = Task.Delay(12000).ContinueWith(_ =>
                {
                    try
                    {
                        if (File.Exists(fileToDelete))
                        {
                            File.Delete(fileToDelete);
                            AppLogger.Trace($"[SoundNotificationService:Stream] Cleaned up temporary audio: {fileToDelete}");
                        }
                    }
                    catch (IOException ioEx)
                    {
                        AppLogger.Trace($"[SoundNotificationService:Stream] File deletion locked: {ioEx.Message}");
                    }
                    catch (UnauthorizedAccessException authEx)
                    {
                        AppLogger.Trace($"[SoundNotificationService:Stream] Deletion permission notice: {authEx.Message}");
                    }
                });
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
            AppLogger.Error($"[SoundNotificationService:Windows] Win32 MessageBeep exception: {winEx.Message}", winEx);
        }
    }
}