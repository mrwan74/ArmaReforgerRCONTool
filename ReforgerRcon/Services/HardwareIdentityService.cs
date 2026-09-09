using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace ReforgerRcon.Services;

public static class HardwareIdentityService
{
    private const string AppIdentitySalt = "ARRT_HARDWARE_IDENTITY_V1_79A5B3E8";
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string FallbackSeedPath = Path.Combine(StorageDirectory, "device_id.dat");
    private static readonly Lock SyncLock = new();
    private static volatile string? _cachedHardwareId;

    public static string GetOrCreateHardwareId()
    {
        var current = _cachedHardwareId;
        if (!string.IsNullOrEmpty(current))
        {
            return current;
        }

        lock (SyncLock)
        {
            if (!string.IsNullOrEmpty(_cachedHardwareId))
            {
                return _cachedHardwareId;
            }

            var cachedSeed = LoadPersistedSeed();
            if (!string.IsNullOrWhiteSpace(cachedSeed))
            {
                _cachedHardwareId = cachedSeed;
                return cachedSeed;
            }

            var hwFingerprint = GenerateCompositeFingerprint();
            if (!string.IsNullOrWhiteSpace(hwFingerprint))
            {
                _cachedHardwareId = hwFingerprint;
                _ = Task.Run(() => PersistSeedFallback(hwFingerprint), CancellationToken.None);
                return hwFingerprint;
            }

            var freshSeed = $"arrt_hw_{Guid.NewGuid():N}";
            _cachedHardwareId = freshSeed;
            _ = Task.Run(() => PersistSeedFallback(freshSeed), CancellationToken.None);
            return freshSeed;
        }
    }

    private static string GenerateCompositeFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(AppIdentitySalt).Append('|');
        sb.Append(Environment.MachineName).Append('|');
        sb.Append(Environment.ProcessorCount).Append('|');
        sb.Append(RuntimeInformation.ProcessArchitecture);

        try
        {
            if (OperatingSystem.IsWindows())
            {
                AppendWindowsIdentifiers(sb);
            }
        }
        catch
        {
            // Fallback gracefully
        }

        var rawString = sb.ToString();
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
                    sb.Append('|').Append(machineGuid.Trim());
                }
            }
        }
        catch
        {
            // Registry fallback
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
        catch
        {
            // Suppress fallback read errors
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
        catch
        {
            // Suppress fallback write errors
        }
    }
}