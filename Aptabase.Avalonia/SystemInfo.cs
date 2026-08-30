using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace Aptabase.Avalonia;

internal sealed class SystemInfo
{
    private static readonly string PkgVersion = typeof(AptabaseClient).Assembly
        .GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.4.0";

    [JsonPropertyName("isDebug")]
    public bool IsDebug { get; set; }

    [JsonPropertyName("osName")]
    public string OsName { get; set; }

    [JsonPropertyName("osVersion")]
    public string OsVersion { get; set; }

    [JsonPropertyName("deviceModel")]
    public string DeviceModel { get; set; }

    [JsonPropertyName("sdkVersion")]
    public string SdkVersion { get; set; }

    [JsonPropertyName("locale")]
    public string Locale { get; set; }

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; }

    [JsonPropertyName("appBuildNumber")]
    public string AppBuildNumber { get; set; }

    [JsonPropertyName("processArchitecture")]
    public string ProcessArchitecture { get; set; }

    [JsonPropertyName("runtimeVersion")]
    public string RuntimeVersion { get; set; }

    public SystemInfo()
    {
        OsName = GetOsName();
        OsVersion = Environment.OSVersion.Version.ToString();
        DeviceModel = $"{Environment.MachineName} ({Environment.ProcessorCount} Cores)";
        SdkVersion = $"Aptabase.Avalonia@{PkgVersion}";
        Locale = CultureInfo.CurrentCulture.Name;
        ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString();
        RuntimeVersion = RuntimeInformation.FrameworkDescription;

        var entryAssembly = Assembly.GetEntryAssembly() ?? typeof(AptabaseClient).Assembly;

        // Extract version from InformationalVersion or standard Assembly Version
        var infoVersionAttr = entryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersionAttr))
        {
            // Strip git commit hash metadata if present (e.g. "0.8.54+abc1234" -> "0.8.54")
            var plusIdx = infoVersionAttr.IndexOf('+', StringComparison.Ordinal);
            AppVersion = plusIdx > 0 ? infoVersionAttr[..plusIdx] : infoVersionAttr;
        }
        else
        {
            var version = entryAssembly.GetName().Version;
            AppVersion = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.8.54";
        }

        var asmVersion = entryAssembly.GetName().Version;
        AppBuildNumber = asmVersion?.Revision >= 0 ? asmVersion.Revision.ToString(CultureInfo.InvariantCulture) : "0";
    }

    internal static bool IsInDebugMode(Assembly? assembly)
    {
        if (assembly is null)
        {
            return false;
        }

        var attributes = assembly.GetCustomAttributes(typeof(DebuggableAttribute), false);
        return attributes.Length > 0 && attributes[0] is DebuggableAttribute debuggable &&
               (debuggable.DebuggingFlags & DebuggableAttribute.DebuggingModes.Default) == DebuggableAttribute.DebuggingModes.Default;
    }

    private static string GetOsName()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "Windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "macOS";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "Linux";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)) return "FreeBSD";

        return RuntimeInformation.OSDescription;
    }

    public override string ToString() =>
        $"OS: {OsName} {OsVersion}, Arch: {ProcessArchitecture}, AppVer: {AppVersion}, Runtime: {RuntimeVersion}, Debug: {IsDebug}";
}