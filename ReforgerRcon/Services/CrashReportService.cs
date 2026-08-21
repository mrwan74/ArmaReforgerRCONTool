using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;
using ReforgerRcon.Models;
using Sentry;

namespace ReforgerRcon.Services;

public static partial class CrashReportService
{
    private const uint MbIconError = 0x00000010;
    private static readonly string CrashDirectory = Path.Combine(AppContext.BaseDirectory, "appdata", "crash_reports");
    public static event Action<ErrorReportModel>? UnhandledErrorCaptured;
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
        try
        {
            if (!Directory.Exists(CrashDirectory))
            {
                Directory.CreateDirectory(CrashDirectory);
            }
        }
        catch (IOException ex)
        {
            SafeLogAppError("Failed to create crash report directory.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeLogAppError("Access denied creating crash report directory.", ex);
        }
    }

    public static void HandleFatalException(string source, Exception ex, bool isTerminating)
    {
        if (Interlocked.CompareExchange(ref _isHandlingCrash, 1, 0) != 0 && !isTerminating)
        {
            SafeLogAppWarn($"[CrashReportService] Concurrent fault suppressed while another report is active ({source}).");
            return;
        }

        try
        {
            var demystifiedEx = ex.Demystify();
            var timestamp = DateTime.UtcNow;
            var crashId = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var textFileName = $"crash_{timestamp:yyyyMMdd_HHmmss}_{crashId}.txt";
            var textFilePath = Path.Combine(CrashDirectory, textFileName);
            var dumpFileName = $"crash_{timestamp:yyyyMMdd_HHmmss}_{crashId}.dmp";
            var dumpFilePath = Path.Combine(CrashDirectory, dumpFileName);

            bool dumpGenerated = TryWriteMemoryDump(dumpFilePath, out long dumpSize);
            var breadcrumbs = SafeGetBreadcrumbs();
            var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
            var ramMb = Environment.WorkingSet / (1024.0 * 1024.0);

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
                SafeLogAppWarn($"[CrashReportService] Sentry telemetry dispatch bypassed during crash reporting: {sentryEx.Message}");
            }

            string memoryDumpStatus;
            if (dumpGenerated)
            {
                memoryDumpStatus = $"{dumpFileName} ({dumpSize / (1024.0 * 1024.0):F2} MB)";
            }
            else if (OperatingSystem.IsWindows())
            {
                memoryDumpStatus = "Unavailable";
            }
            else
            {
                memoryDumpStatus = "Windows Minidump Native Only (Linux Text Dump Captured)";
            }

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine("ARMA REFORGER RCON MANAGEMENT STUDIO - CRASH DIAGNOSTIC SNAPSHOT");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Crash ID:        {crashId}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Timestamp (UTC): {timestamp:O}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Source Handler:  {source}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Terminating:     {isTerminating}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Exception Type:  {demystifiedEx.GetType().FullName}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Error Message:   {demystifiedEx.Message}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Memory Dump:     {memoryDumpStatus}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();
            sb.AppendLine("--- SYSTEM ENVIRONMENT & RUNTIME SNAPSHOT ---");
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
                Architecture = string.Create(CultureInfo.InvariantCulture, $"{RuntimeInformation.ProcessArchitecture} ({Environment.ProcessorCount} Cores)"),
                ClrVersion = RuntimeInformation.FrameworkDescription,
                RamWorkingSetMb = ramMb,
                ProcessUptime = uptime,
                ProcessorCount = Environment.ProcessorCount,
                ThreadId = Environment.CurrentManagedThreadId,
                Breadcrumbs = [.. breadcrumbs],
                FullReportText = fullReportText
            };

            SafeLogAppFatal(string.Create(CultureInfo.InvariantCulture, $"CRITICAL ERROR [{source}] (CrashId: {crashId}, Terminating: {isTerminating}, OS: {RuntimeInformation.OSDescription})"), demystifiedEx);

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

            try
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        UnhandledErrorCaptured?.Invoke(report);
                        ToastNotificationService.Instance.ShowToast(
                            $"System Alert [{crashId}]",
                            $"{demystifiedEx.GetType().Name}: {demystifiedEx.Message}",
                            "CRASH_DUMP"
                        );
                    }
                    catch (Exception dispatchEx)
                    {
                        SafeLogAppError("Failed dispatching crash report to UI layer.", dispatchEx);
                    }
                });
            }
            catch (Exception postEx)
            {
                SafeLogAppError("Dispatcher failed posting crash event to UI thread.", postEx);

                if (OperatingSystem.IsWindows())
                {
                    var outcomeText = isTerminating
                        ? "The application will now shut down."
                        : "The application will attempt to continue running.";

                    var dialogMessage = string.Create(CultureInfo.InvariantCulture,
                        $"A critical application fault occurred:\n\nSource: {source}\nException: {demystifiedEx.GetType().Name}\nMessage: {demystifiedEx.Message}\n\nCrash ID: #{crashId}\nDiagnostic Report: {textFilePath}\nMemory Dump: {(dumpGenerated ? dumpFilePath : "Unavailable")}\n\n{outcomeText}");

                    MessageBox(IntPtr.Zero, dialogMessage, isTerminating ? "ARMA Reforger RCON - Fatal Error" : "ARMA Reforger RCON - System Fault", MbIconError);
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"[CRITICAL FAULT] {source} -> {demystifiedEx.GetType().Name}: {demystifiedEx.Message}");
                    Console.Error.WriteLine($"Report written to: {textFilePath}");
                    Console.ResetColor();
                }
            }
        }
        finally
        {
            if (!isTerminating)
            {
                Interlocked.Exchange(ref _isHandlingCrash, 0);
            }
        }
    }

    private static bool TryWriteMemoryDump(string dmpPath, out long dumpSize)
    {
        dumpSize = 0;
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = Process.GetCurrentProcess();
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
                SafeLogAppInfo(string.Create(CultureInfo.InvariantCulture, $"[CrashReportService] Memory dump successfully created at '{dmpPath}' ({dumpSize / 1024} KB)."));
                return true;
            }

            int errorCode = Marshal.GetLastWin32Error();
            SafeLogAppError(string.Create(CultureInfo.InvariantCulture, $"[CrashReportService] MiniDumpWriteDump returned false with Win32 Error Code: {errorCode}."), new Win32Exception(errorCode));
            return false;
        }
        catch (IOException ioEx)
        {
            SafeLogAppError(string.Create(CultureInfo.InvariantCulture, $"[CrashReportService] I/O error creating dump file '{dmpPath}': {ioEx.Message}"), ioEx);
            return false;
        }
        catch (Exception ex)
        {
            SafeLogAppError(string.Create(CultureInfo.InvariantCulture, $"[CrashReportService] Failed generating memory dump at '{dmpPath}'."), ex);
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
        catch { System.Diagnostics.Debug.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{msg} - {ex.Message}")); }
    }

    private static void SafeLogAppFatal(string msg, Exception ex)
    {
        try { AppLogger.Fatal(msg, ex); }
        catch { System.Diagnostics.Debug.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{msg} - {ex.Message}")); }
    }
}