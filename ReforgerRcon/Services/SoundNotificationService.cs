using System;
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

    [LibraryImport("user32.dll", EntryPoint = "MessageBeep")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint uType);

    public static void PlayAlert(SoundAlertType alertType)
    {
        Task.Run(() =>
        {
            try
            {
                lock (PlayLock)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        PlayWindowsSound(alertType);
                    }
                    else
                    {
                        AppLogger.Trace($"[SoundNotificationService] Terminal audio bell emitted for alert: {alertType}");
                        Console.Beep();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[SoundNotificationService] Failed playing sound alert ({alertType}): {ex.Message}");
            }
        });
    }

    [SupportedOSPlatform("windows")]
    private static void PlayWindowsSound(SoundAlertType alertType)
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

            MessageBeep(soundType);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[SoundNotificationService] Win32 MessageBeep notice: {ex.Message}");
        }
    }
}