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

    private static readonly Lock PlayLock = new();
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
                lock (PlayLock)
                {
                    AppLogger.Debug($"[SoundNotificationService] Initiating audio playback for alert: {alertType} on OS: {RuntimeInformation.OSDescription}");
                }

                if (await TryPlayCustomAudioFileAsync(alertType).ConfigureAwait(false))
                {
                    sw.Stop();
                    AppLogger.Info($"[SoundNotificationService] Custom audio asset successfully played for {alertType} in {sw.ElapsedMilliseconds} ms via NetCoreAudio.");
                    return;
                }

                AppLogger.Debug($"[SoundNotificationService] No custom asset sound found for {alertType}. Executing platform system sound fallback.");

                lock (PlayLock)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        PlayWindowsFallbackSound(alertType);
                    }
                    else
                    {
                        AppLogger.Trace($"[SoundNotificationService] Emitting terminal audio bell for alert: {alertType}");
                        Console.Beep();
                    }
                }

                sw.Stop();
                AppLogger.Debug($"[SoundNotificationService] Platform system sound fallback completed for {alertType} in {sw.ElapsedMilliseconds} ms.");
            }
            catch (Win32Exception winEx)
            {
                sw.Stop();
                AppLogger.Error($"[SoundNotificationService] Native Win32 audio error for {alertType} (Error Code: {winEx.NativeErrorCode}): {winEx.Message}", winEx, new Dictionary<string, object?>
                {
                    ["alert_type"] = alertType.ToString(),
                    ["win32_code"] = winEx.NativeErrorCode,
                    ["os"] = RuntimeInformation.OSDescription
                });
            }
            catch (FileNotFoundException fnfEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService] Audio file not found during {alertType} playback: {fnfEx.FileName}", fnfEx);
            }
            catch (DirectoryNotFoundException dnfEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService] Audio directory path missing: {dnfEx.Message}", dnfEx);
            }
            catch (UnauthorizedAccessException authEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService] Access denied opening audio file for {alertType}: {authEx.Message}", authEx);
            }
            catch (IOException ioEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService] Audio I/O stream failure for {alertType}: {ioEx.Message}", ioEx);
            }
            catch (InvalidOperationException invEx)
            {
                sw.Stop();
                AppLogger.Warn($"[SoundNotificationService] Invalid operation in NetCoreAudio pipeline: {invEx.Message}", invEx);
            }
            catch (OperationCanceledException opEx)
            {
                sw.Stop();
                AppLogger.Trace($"[SoundNotificationService] Audio alert playback canceled for {alertType}: {opEx.Message}");
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
                    AppLogger.Trace($"[SoundNotificationService] Opening embedded asset audio stream: {avaresUri}");
                    await using var stream = AssetLoader.Open(avaresUri);
                    if (await PlayAudioStreamAsync(stream, fileName).ConfigureAwait(false))
                    {
                        AppLogger.Info($"[SoundNotificationService] Played embedded asset sound: '{fileName}' ({avaresUri})");
                        return true;
                    }
                }
                catch (FileNotFoundException fnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService] Embedded asset file not found '{avaresUri}': {fnfEx.Message}");
                }
                catch (IOException ioEx)
                {
                    AppLogger.Warn($"[SoundNotificationService] I/O error reading asset audio '{avaresUri}': {ioEx.Message}", ioEx);
                }
                catch (UnauthorizedAccessException authEx)
                {
                    AppLogger.Warn($"[SoundNotificationService] Access denied reading asset audio '{avaresUri}': {authEx.Message}", authEx);
                }
            }

            var rootAvaresUri = new Uri($"avares://ReforgerRcon/Assets/{fileName}");
            if (AssetLoader.Exists(rootAvaresUri))
            {
                try
                {
                    AppLogger.Trace($"[SoundNotificationService] Opening root asset audio stream: {rootAvaresUri}");
                    await using var stream = AssetLoader.Open(rootAvaresUri);
                    if (await PlayAudioStreamAsync(stream, fileName).ConfigureAwait(false))
                    {
                        AppLogger.Info($"[SoundNotificationService] Played root asset sound: '{fileName}' ({rootAvaresUri})");
                        return true;
                    }
                }
                catch (FileNotFoundException fnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService] Root asset file not found '{rootAvaresUri}': {fnfEx.Message}");
                }
                catch (IOException ioEx)
                {
                    AppLogger.Warn($"[SoundNotificationService] I/O error reading root asset audio '{rootAvaresUri}': {ioEx.Message}", ioEx);
                }
                catch (UnauthorizedAccessException authEx)
                {
                    AppLogger.Warn($"[SoundNotificationService] Access denied reading root asset audio '{rootAvaresUri}': {authEx.Message}", authEx);
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
                    AppLogger.Trace($"[SoundNotificationService] Located audio file on disk: '{diskPath}'. Dispatching to NetCoreAudio...");
                    await AudioPlayer.Play(diskPath).ConfigureAwait(false);
                    AppLogger.Info($"[SoundNotificationService] NetCoreAudio started playback for disk audio: '{diskPath}'");
                    return true;
                }
                catch (FileNotFoundException fnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService] Disk audio file missing '{diskPath}': {fnfEx.Message}");
                }
                catch (DirectoryNotFoundException dnfEx)
                {
                    AppLogger.Trace($"[SoundNotificationService] Disk audio directory missing '{diskPath}': {dnfEx.Message}");
                }
                catch (IOException ioEx)
                {
                    AppLogger.Warn($"[SoundNotificationService] Disk audio I/O error for '{diskPath}': {ioEx.Message}", ioEx);
                }
                catch (UnauthorizedAccessException authEx)
                {
                    AppLogger.Warn($"[SoundNotificationService] Disk audio access denied for '{diskPath}': {authEx.Message}", authEx);
                }
                catch (InvalidOperationException invEx)
                {
                    AppLogger.Warn($"[SoundNotificationService] NetCoreAudio error playing '{diskPath}': {invEx.Message}", invEx);
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

            AppLogger.Debug($"[SoundNotificationService] Extracted embedded audio to temp file '{tempFile}'. Calling NetCoreAudio.Play()...");
            await AudioPlayer.Play(tempFile).ConfigureAwait(false);
            return true;
        }
        catch (IOException ioEx)
        {
            AppLogger.Error($"[SoundNotificationService] I/O error extracting audio stream to temp file: {ioEx.Message}", ioEx);
            return false;
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Error($"[SoundNotificationService] Permission denied writing temp audio file: {authEx.Message}", authEx);
            return false;
        }
        catch (InvalidOperationException invEx)
        {
            AppLogger.Error($"[SoundNotificationService] NetCoreAudio playback failure: {invEx.Message}", invEx);
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
                            AppLogger.Trace($"[SoundNotificationService] Cleaned up temporary sound file: {fileToDelete}");
                        }
                    }
                    catch (IOException ioEx)
                    {
                        AppLogger.Trace($"[SoundNotificationService] Deferred temp audio file deletion lock: {ioEx.Message}");
                    }
                    catch (UnauthorizedAccessException authEx)
                    {
                        AppLogger.Trace($"[SoundNotificationService] Deferred temp audio file deletion permission: {authEx.Message}");
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
            AppLogger.Debug($"[SoundNotificationService] Windows MessageBeep dispatched for {alertType} (Type: 0x{soundType:X8}, Success: {success}).");
        }
        catch (Win32Exception winEx)
        {
            AppLogger.Error($"[SoundNotificationService] Win32 MessageBeep exception: {winEx.Message}", winEx);
        }
    }
}