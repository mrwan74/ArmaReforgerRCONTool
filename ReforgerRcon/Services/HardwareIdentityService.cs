using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ReforgerRcon.Services;

public static class HardwareIdentityService
{
    private const string AppIdentitySalt = "ARRT_HARDWARE_IDENTITY_V1_79A5B3E8";
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string FallbackSeedPath = Path.Combine(StorageDirectory, "device_id.dat");
    private static readonly Lock SyncLock = new();
    private static string? _cachedHardwareId;

    public static string GetOrCreateHardwareId()
    {
        lock (SyncLock)
        {
            if (!string.IsNullOrEmpty(_cachedHardwareId))
            {
                return _cachedHardwareId;
            }

            var hwFingerprint = GenerateCompositeFingerprint();
            if (!string.IsNullOrWhiteSpace(hwFingerprint))
            {
                _cachedHardwareId = hwFingerprint;
                PersistSeedFallback(hwFingerprint);
                return hwFingerprint;
            }

            var cachedSeed = LoadPersistedSeed();
            if (!string.IsNullOrWhiteSpace(cachedSeed))
            {
                _cachedHardwareId = cachedSeed;
                return cachedSeed;
            }

            var freshSeed = $"arrt_hw_{Guid.NewGuid():N}";
            _cachedHardwareId = freshSeed;
            PersistSeedFallback(freshSeed);
            return freshSeed;
        }
    }

    private static string GenerateCompositeFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(AppIdentitySalt).Append('|');
        sb.Append(RuntimeInformation.OSArchitecture).Append('|');
        sb.Append(RuntimeInformation.ProcessArchitecture).Append('|');

        try
        {
            if (OperatingSystem.IsWindows())
            {
                AppendWindowsIdentifiers(sb);
            }
            else if (OperatingSystem.IsLinux())
            {
                AppendLinuxIdentifiers(sb);
            }
            else if (OperatingSystem.IsMacOS())
            {
                AppendMacIdentifiers(sb);
            }
            else
            {
                sb.Append(Environment.MachineName).Append('|');
                sb.Append(Environment.ProcessorCount).Append('|');
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug($"[HardwareIdentity] Hardware query notice: {ex.Message}");
        }

        var rawString = sb.ToString();
        if (rawString.Length <= AppIdentitySalt.Length + 10)
        {
            return string.Empty;
        }

        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawString));
        var hexHash = Convert.ToHexString(hashBytes).ToLowerInvariant();

        return $"arrt_hw_{hexHash[..32]}";
    }

    [SupportedOSPlatform("windows")]
    private static void AppendWindowsIdentifiers(StringBuilder sb)
    {
        try
        {
            using var regKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");

            if (regKey != null)
            {
                var machineGuid = regKey.GetValue("MachineGuid")?.ToString();
                if (!string.IsNullOrWhiteSpace(machineGuid))
                {
                    sb.Append("WinGuid:").Append(machineGuid.Trim()).Append('|');
                }
            }
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Trace($"[HardwareIdentity] Access denied reading Windows MachineGuid registry: {authEx.Message}");
        }
        catch (IOException ioEx)
        {
            AppLogger.Trace($"[HardwareIdentity] IO error accessing Windows registry: {ioEx.Message}");
        }

        try
        {
            var procIdentifier = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (!string.IsNullOrWhiteSpace(procIdentifier))
            {
                sb.Append("CPU:").Append(procIdentifier.Trim()).Append('|');
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[HardwareIdentity] Environment CPU identifier notice: {ex.Message}");
        }

        try
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
            if (!string.IsNullOrEmpty(systemDrive) && Directory.Exists(systemDrive))
            {
                var driveInfo = new DriveInfo(systemDrive);
                sb.Append("DriveType:").Append(driveInfo.DriveType).Append('|');
                sb.Append("DriveFormat:").Append(driveInfo.DriveFormat).Append('|');
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[HardwareIdentity] System drive inspection notice: {ex.Message}");
        }
    }

    private static void AppendLinuxIdentifiers(StringBuilder sb)
    {
        var machineIdPaths = new[] { "/etc/machine-id", "/var/lib/dbus/machine-id", "/sys/class/dmi/id/product_uuid" };

        foreach (var path in machineIdPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var id = File.ReadAllText(path).Trim();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        sb.Append("LinuxId:").Append(id).Append('|');
                        break;
                    }
                }
            }
            catch (UnauthorizedAccessException authEx)
            {
                AppLogger.Trace($"[HardwareIdentity] Access denied reading Linux machine ID from '{path}': {authEx.Message}");
            }
            catch (IOException ioEx)
            {
                AppLogger.Trace($"[HardwareIdentity] IO error reading Linux machine ID from '{path}': {ioEx.Message}");
            }
        }

        try
        {
            sb.Append("Cores:").Append(Environment.ProcessorCount).Append('|');
            sb.Append("OSDesc:").Append(RuntimeInformation.OSDescription).Append('|');
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[HardwareIdentity] Linux environment fallback notice: {ex.Message}");
        }
    }

    private static void AppendMacIdentifiers(StringBuilder sb)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/ioreg",
                    Arguments = "-rd1 -c IOPlatformExpertDevice",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (process.Start())
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1000);

                const string marker = "\"IOPlatformUUID\" = \"";
                var idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    var start = idx + marker.Length;
                    var end = output.IndexOf('"', start);
                    if (end > start)
                    {
                        var uuid = output[start..end];
                        sb.Append("MacUUID:").Append(uuid).Append('|');
                    }
                }
            }
        }
        catch (Win32Exception winEx)
        {
            AppLogger.Trace($"[HardwareIdentity] ioreg process execution error on macOS: {winEx.Message}");
        }
        catch (FileNotFoundException fnfEx)
        {
            AppLogger.Trace($"[HardwareIdentity] ioreg binary not found on macOS: {fnfEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[HardwareIdentity] macOS hardware query notice: {ex.Message}");
        }
    }

    private static string LoadPersistedSeed()
    {
        try
        {
            if (File.Exists(FallbackSeedPath))
            {
                var seed = File.ReadAllText(FallbackSeedPath).Trim();
                if (!string.IsNullOrWhiteSpace(seed))
                {
                    return seed;
                }
            }
        }
        catch (IOException ioEx)
        {
            AppLogger.Trace($"[HardwareIdentity] IO error reading fallback seed: {ioEx.Message}");
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Trace($"[HardwareIdentity] Access denied reading fallback seed: {authEx.Message}");
        }

        return string.Empty;
    }

    private static void PersistSeedFallback(string seed)
    {
        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
            }

            File.WriteAllText(FallbackSeedPath, seed, Encoding.UTF8);
        }
        catch (IOException ioEx)
        {
            AppLogger.Trace($"[HardwareIdentity] Failed writing fallback seed to disk: {ioEx.Message}");
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Trace($"[HardwareIdentity] Permission denied saving fallback seed: {authEx.Message}");
        }
    }
}