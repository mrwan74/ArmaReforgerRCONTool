using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Aptabase.Avalonia;
using Avalonia.Threading;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Win32.SafeHandles;
using ReforgerRcon.Models;
using Sentry;

namespace ReforgerRcon.Services;

public static partial class CrashReportService
{
    private const uint MbIconError = 0x00000010;
    private static readonly string CrashDirectory = AppPaths.CrashReportsDirectory;
    public static event Action<ErrorReportModel>? UnhandledErrorCaptured;
    private static readonly ConcurrentQueue<ErrorReportModel> PendingReports = new();
    private static int _isHandlingCrash;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [Flags]
    private enum MiniDumpTypes : uint
    {
        None = 0x00000000,
        MiniDumpWithDataSegs = 0x00000001,
        MiniDumpWithFullMemory = 0x00000002,
        MiniDumpWithHandleData = 0x00000004,
        MiniDumpFilterMemory = 0x00000008,
        MiniDumpScanMemory = 0x00000010,
        MiniDumpWithUnloadedModules = 0x00000020,
        MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
        MiniDumpFilterModulePaths = 0x00000080,
        MiniDumpWithProcessThreadData = 0x00000100,
        MiniDumpWithPrivateReadWriteMemory = 0x00000200,
        MiniDumpWithoutOptionalData = 0x00000400,
        MiniDumpWithFullMemoryInfo = 0x00000800,
        MiniDumpWithThreadInfo = 0x00001000,
        MiniDumpWithCodeSegs = 0x00002000
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute'", Justification = "DllImport with SafeFileHandle provides deterministic unmanaged minidump generation on Windows")]
    [DllImport("dbghelp.dll", EntryPoint = "MiniDumpWriteDump", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeFileHandle hFile,
        MiniDumpTypes dumpType,
        IntPtr expParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    public static void Initialize()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (!Directory.Exists(CrashDirectory))
            {
                Directory.CreateDirectory(CrashDirectory);
                SafeLogAppInfo("[CrashReportService:Init] Created crash directory at '" + CrashDirectory + "'.");
            }
            SafeLogAppInfo("[CrashReportService:Init] Crash reporting engine initialized in " + Stopwatch.GetElapsedTime(start).TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture) + "ms.");
        }
        catch (IOException ex)
        {
            SafeLogAppError("Failed to create crash report directory.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeLogAppError("Access denied creating crash report directory.", ex);
        }
        catch (Exception ex)
        {
            SafeLogAppError("Unexpected fault during crash directory creation.", ex);
        }
    }

    public static IReadOnlyList<ErrorReportModel> GetAndClearPendingReports()
    {
        var list = new List<ErrorReportModel>();
        while (PendingReports.TryDequeue(out var report))
        {
            list.Add(report);
        }
        return list;
    }

    public static void HandleFatalException(string source, Exception ex, bool isTerminating)
    {
        var crashStart = Stopwatch.GetTimestamp();
        if (Interlocked.CompareExchange(ref _isHandlingCrash, 1, 0) != 0 && !isTerminating)
        {
            SafeLogAppWarn("[CrashReportService:Handler] Suppressed concurrent fault while report is active (" + source + ").");
            return;
        }

        try
        {
            var demystifiedEx = ex.Demystify();
            var timestamp = DateTime.UtcNow;
            var crashId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var textFileName = "crash_" + timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + crashId + ".txt";
            var textFilePath = Path.Combine(CrashDirectory, textFileName);
            var dumpFileName = "crash_" + timestamp.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_" + crashId + ".dmp";
            var dumpFilePath = Path.Combine(CrashDirectory, dumpFileName);

            var dumpStart = Stopwatch.GetTimestamp();
            bool dumpGenerated = TryWriteMemoryDump(dumpFilePath, out long dumpSize);
            var dumpElapsed = Stopwatch.GetElapsedTime(dumpStart).TotalMilliseconds;

            var breadcrumbs = SafeGetBreadcrumbs();
            var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var ramMb = Environment.WorkingSet / (1024.0 * 1024.0);

            if (AppSettings.IsCrashReportingEnabled() && AptabaseExtensions.IsInitialized)
            {
                try
                {
                    _ = AptabaseExtensions.Instance.TrackError(demystifiedEx, fatal: isTerminating);
                    AppLogger.TrackEvent("fatal_crash_captured", new Dictionary<string, object>
                    {
                        ["crash_id"] = crashId,
                        ["installation_id"] = AppLogger.InstallationId,
                        ["source"] = source,
                        ["is_terminating"] = isTerminating,
                        ["dump_generated"] = dumpGenerated,
                        ["ram_mb"] = Math.Round(ramMb, 1),
                        ["uptime_seconds"] = (int)uptime.TotalSeconds
                    });
                }
                catch (Exception aptaEx)
                {
                    SafeLogAppWarn("[CrashReportService:Aptabase] Dispatch notice: " + aptaEx.Message);
                }
            }

            try
            {
                SentrySdk.Metrics.EmitCounter("app_faults", 1,
                [
                    new KeyValuePair<string, object>("source", source),
                    new KeyValuePair<string, object>("terminating", isTerminating.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, object>("os", RuntimeInformation.OSDescription),
                    new KeyValuePair<string, object>("exception_type", demystifiedEx.GetType().Name)
                ]);

                SentrySdk.CaptureException(demystifiedEx, scope =>
                {
                    scope.Level = isTerminating ? SentryLevel.Fatal : SentryLevel.Error;
                    scope.SetTag("crash_id", crashId);
                    scope.SetTag("installation_id", AppLogger.InstallationId);
                    scope.SetTag("fault_source", source);
                    scope.SetTag("os_platform", RuntimeInformation.OSDescription);
                    scope.SetTag("is_terminating", isTerminating.ToString(CultureInfo.InvariantCulture));
                    scope.SetExtra("dump_file_name", dumpFileName);
                    scope.SetExtra("dump_size_bytes", dumpSize);
                    scope.SetExtra("ram_working_set_mb", ramMb);
                    scope.SetExtra("uptime_formatted", uptime.ToString());
                });
            }
            catch (Exception sentryEx)
            {
                SafeLogAppWarn("[CrashReportService:Sentry] Telemetry bypassed: " + sentryEx.Message);
            }

            string memoryDumpStatus;
            if (dumpGenerated)
            {
                var runtimeEngine = OperatingSystem.IsWindows() ? "Windows Native MiniDump" : ".NET DiagnosticsClient CoreDump";
                memoryDumpStatus = $"{dumpFileName} ({dumpSize / (1024.0 * 1024.0):F2} MB, written in {dumpElapsed:F2}ms via {runtimeEngine})";
            }
            else
            {
                memoryDumpStatus = "Unavailable";
            }

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("ARMA REFORGER RCON MANAGEMENT STUDIO - CRASH DIAGNOSTIC SNAPSHOT");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Crash ID:        {crashId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Installation ID: {AppLogger.InstallationId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Timestamp (UTC): {timestamp:O}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Source Handler:  {source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Terminating:     {isTerminating}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Exception Type:  {demystifiedEx.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Error Message:   {demystifiedEx.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Memory Dump:     {memoryDumpStatus}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine("--- SYSTEM ENVIRONMENT & RUNTIME SNAPSHOT ---");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Installation ID: {AppLogger.InstallationId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS Description:  {RuntimeInformation.OSDescription}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"OS Architecture: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Process Arch:    {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Processor Count: {Environment.ProcessorCount} Core(s)");
            sb.AppendLine(CultureInfo.InvariantCulture, $"RAM Working Set: {ramMb:F2} MB");
            sb.AppendLine(CultureInfo.InvariantCulture, $"CLR Runtime:     {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Process Uptime:  {uptime}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Thread ID:       T{Environment.CurrentManagedThreadId:D2}");
            sb.AppendLine();
            sb.AppendLine("--- RECENT EXECUTION BREADCRUMBS (ACTION HISTORY) ---");
            if (breadcrumbs.Count > 0)
            {
                foreach (var crumb in breadcrumbs)
                {
                    sb.AppendLine(CultureInfo.InvariantCulture, $"  {crumb}");
                }
            }
            else
            {
                sb.AppendLine("  (No execution breadcrumbs captured)");
            }
            sb.AppendLine();
            sb.AppendLine("--- DEMYSTIFIED EXCEPTION CHAIN & STACK TRACE ---");
            sb.AppendLine(demystifiedEx.ToString());
            sb.AppendLine();
            sb.AppendLine("--- ENHANCED CALL STACK FRAMES ---");
            sb.AppendLine(EnhancedStackTrace.Current().ToString());

            var fullReportText = sb.ToString();

            var report = new ErrorReportModel
            {
                ErrorId = crashId,
                InstallationId = AppLogger.InstallationId,
                Source = source,
                Timestamp = timestamp,
                ExceptionType = demystifiedEx.GetType().FullName ?? "UnknownException",
                Message = demystifiedEx.Message,
                StackTrace = demystifiedEx.ToString(),
                IsTerminating = isTerminating,
                ReportFilePath = textFilePath,
                DumpFilePath = dumpGenerated ? dumpFilePath : string.Empty,
                DumpFileSizeBytes = dumpSize,
                OsVersion = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString() + " (" + Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture) + " Cores)",
                ClrVersion = RuntimeInformation.FrameworkDescription,
                RamWorkingSetMb = ramMb,
                ProcessUptime = uptime,
                ProcessorCount = Environment.ProcessorCount,
                ThreadId = Environment.CurrentManagedThreadId,
                Breadcrumbs = [.. breadcrumbs],
                FullReportText = fullReportText
            };

            PendingReports.Enqueue(report);

            var totalReportMs = Stopwatch.GetElapsedTime(crashStart).TotalMilliseconds;
            SafeLogAppFatal("[CrashReportService:Fatal] CRITICAL ERROR [" + source + "] in " + totalReportMs.ToString("F2", CultureInfo.InvariantCulture) + "ms (CrashId: " + crashId + ", InstallId: " + AppLogger.InstallationId + ", Terminating: " + isTerminating.ToString() + ", OS: " + RuntimeInformation.OSDescription + ")", demystifiedEx);

            try
            {
                if (!Directory.Exists(CrashDirectory))
                {
                    Directory.CreateDirectory(CrashDirectory);
                }
                File.WriteAllText(textFilePath, fullReportText, Encoding.UTF8);
            }
            catch (Exception writeEx)
            {
                SafeLogAppError("Failed writing crash text dump file to disk.", writeEx);
            }

            bool dispatchedToUi = false;
            try
            {
                if (Dispatcher.UIThread is { } uiDispatcher)
                {
                    uiDispatcher.Post(() =>
                    {
                        try
                        {
                            UnhandledErrorCaptured?.Invoke(report);
                            SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
                            ToastNotificationService.Instance.ShowError(
                                "System Fault [" + crashId + "]",
                                demystifiedEx.GetType().Name + ": " + demystifiedEx.Message,
                                "CRASH_DIAGNOSTIC"
                            );
                        }
                        catch (Exception dispatchEx)
                        {
                            SafeLogAppError("Failed dispatching crash report to UI layer.", dispatchEx);
                            ShowNativeFallbackDialog(source, demystifiedEx, crashId, textFilePath, dumpFilePath, dumpGenerated, isTerminating);
                        }
                    });
                    dispatchedToUi = true;
                }
            }
            catch (Exception postEx)
            {
                SafeLogAppError("Dispatcher failed posting crash event to UI thread.", postEx);
            }

            if (!dispatchedToUi || isTerminating)
            {
                ShowNativeFallbackDialog(source, demystifiedEx, crashId, textFilePath, dumpFilePath, dumpGenerated, isTerminating);
            }
        }
        catch (Exception unhandled)
        {
            SafeLogAppFatal("Crash reporter encountered an internal crash.", unhandled);
        }
        finally
        {
            if (!isTerminating)
            {
                Interlocked.Exchange(ref _isHandlingCrash, 0);
            }
        }
    }

    private static void ShowNativeFallbackDialog(string source, Exception ex, string crashId, string textFilePath, string dumpFilePath, bool dumpGenerated, bool isTerminating)
    {
        if (OperatingSystem.IsWindows())
        {
            var outcomeText = isTerminating
                ? "The application will now terminate."
                : "The application captured the fault and will attempt to continue.";

            var dialogMessage =
                "An unexpected application error occurred:\n\n" +
                "Handler Source: " + source + "\n" +
                "Exception Type: " + ex.GetType().Name + "\n" +
                "Error Message:  " + ex.Message + "\n\n" +
                "Crash ID:        #" + crashId + "\n" +
                "Installation ID: " + AppLogger.InstallationId + "\n" +
                "Diagnostic Log:  " + textFilePath + "\n" +
                "Memory Dump:     " + (dumpGenerated ? dumpFilePath : "Unavailable") + "\n\n" +
                outcomeText;

            MessageBox(IntPtr.Zero, dialogMessage, isTerminating ? "ARMA Reforger RCON - Fatal Error" : "ARMA Reforger RCON - System Fault", MbIconError);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("[CRITICAL FAULT] " + source + " (Install ID: " + AppLogger.InstallationId + ") -> " + ex.GetType().Name + ": " + ex.Message);
            Console.Error.WriteLine("Report written to: " + textFilePath);
            if (dumpGenerated)
            {
                Console.Error.WriteLine("Memory dump written to: " + dumpFilePath);
            }
            Console.ResetColor();
        }
    }

    private static bool TryWriteMemoryDump(string dmpPath, out long dumpSize)
    {
        dumpSize = 0;
        var start = Stopwatch.GetTimestamp();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                SafeLogAppInfo($"[CrashReportService:MiniDump] Initiating Windows native MiniDumpWriteDump (PID={process.Id}, Threads={process.Threads.Count}, Path='{dmpPath}')...");

                using var fileStream = new FileStream(dmpPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

                const MiniDumpTypes dumpFlags = MiniDumpTypes.MiniDumpWithDataSegs |
                                                MiniDumpTypes.MiniDumpWithHandleData |
                                                MiniDumpTypes.MiniDumpWithUnloadedModules |
                                                MiniDumpTypes.MiniDumpWithThreadInfo |
                                                MiniDumpTypes.MiniDumpWithProcessThreadData |
                                                MiniDumpTypes.MiniDumpWithFullMemoryInfo;

                bool success = MiniDumpWriteDump(
                    process.Handle,
                    (uint)process.Id,
                    fileStream.SafeFileHandle,
                    dumpFlags,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (success)
                {
                    fileStream.Flush();
                    dumpSize = new FileInfo(dmpPath).Length;
                    var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    SafeLogAppInfo($"[CrashReportService:MiniDump] Windows memory dump created successfully at '{dmpPath}' ({dumpSize / 1024} KB) in {elapsedMs:F2}ms.");
                    return true;
                }

                int errorCode = Marshal.GetLastWin32Error();
                SafeLogAppError($"[CrashReportService:MiniDump] MiniDumpWriteDump returned false with Win32 Error Code: {errorCode}.", new Win32Exception(errorCode));
                return false;
            }
            catch (Exception ex)
            {
                SafeLogAppError($"[CrashReportService:MiniDump] Exception during Windows memory dump generation at '{dmpPath}': {ex.Message}", ex);
                return false;
            }
        }

        try
        {
            var pid = Environment.ProcessId;
            SafeLogAppInfo($"[CrashReportService:DiagnosticsClient] Attempting cross-platform core dump on {RuntimeInformation.OSDescription} via DiagnosticsClient (PID={pid}, TargetPath='{dmpPath}')...");

            var client = new DiagnosticsClient(pid);
            client.WriteDump(DumpType.Normal, dmpPath, logDumpGeneration: false);

            if (File.Exists(dmpPath))
            {
                dumpSize = new FileInfo(dmpPath).Length;
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                SafeLogAppInfo($"[CrashReportService:DiagnosticsClient] Cross-platform core dump created successfully at '{dmpPath}' ({dumpSize / 1024} KB) in {elapsedMs:F2}ms.");
                return true;
            }

            SafeLogAppWarn($"[CrashReportService:DiagnosticsClient] DiagnosticsClient returned without creating dump file at '{dmpPath}'.");
            return false;
        }
        catch (Exception ex)
        {
            SafeLogAppWarn($"[CrashReportService:DiagnosticsClient] Core dump generation bypassed on {RuntimeInformation.OSDescription}: {ex.Message}");
            return false;
        }
    }

    private static List<string> SafeGetBreadcrumbs()
    {
        try { return AppLogger.GetRecentBreadcrumbs(); }
        catch { return []; }
    }

    private static void SafeLogAppInfo(string msg)
    {
        try { AppLogger.Info(msg); }
        catch { System.Diagnostics.Debug.WriteLine(msg); }
    }

    private static void SafeLogAppWarn(string msg)
    {
        try { AppLogger.Warn(msg); }
        catch { System.Diagnostics.Debug.WriteLine(msg); }
    }

    private static void SafeLogAppError(string msg, Exception ex)
    {
        try { AppLogger.Error(msg, ex); }
        catch { System.Diagnostics.Debug.WriteLine(msg + " - " + ex.Message); }
    }

    private static void SafeLogAppFatal(string msg, Exception ex)
    {
        try { AppLogger.Fatal(msg, ex); }
        catch { System.Diagnostics.Debug.WriteLine(msg + " - " + ex.Message); }
    }
}