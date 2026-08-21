using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class ErrorDetailsDialogViewModel(ErrorReportModel report, Action onClose) : ViewModelBase
{
    private static readonly string[] UnixStandardBinDirectories = ["/usr/bin", "/bin", "/usr/local/bin"];
    private readonly Action _onClose = onClose;

    [ObservableProperty] public partial ErrorReportModel Report { get; set; } = report;
    [ObservableProperty] public partial string SelectedTab { get; set; } = "StackTrace";

    public string LogFileName => Path.GetFileName(Report.ReportFilePath);
    public string DumpFileName => !string.IsNullOrEmpty(Report.DumpFilePath) ? Path.GetFileName(Report.DumpFilePath) : "No dump generated";

    public string FormattedBreadcrumbs => Report.Breadcrumbs.Count > 0
        ? string.Join(Environment.NewLine, Report.Breadcrumbs)
        : "(No execution breadcrumbs captured prior to fault)";

    public string FormattedSystemInfo =>
        $"Crash ID:           {Report.ErrorId}\n" +
        $"Timestamp (UTC):    {Report.Timestamp:O}\n" +
        $"Source Handler:     {Report.Source}\n" +
        $"Terminating:        {Report.IsTerminating}\n" +
        $"OS Version:         {Report.OsVersion}\n" +
        $"Architecture:       {Report.Architecture}\n" +
        $"CLR Framework:      {Report.ClrVersion}\n" +
        $"RAM Working Set:    {Report.RamWorkingSetMb:F2} MB\n" +
        $"Process Uptime:     {Report.FormattedUptime}\n" +
        $"Managed Thread:     T{Report.ThreadId:D2}\n" +
        $"Memory Dump Size:   {Report.FormattedDumpSize}\n" +
        $"Dump Location:      {Report.DumpFilePath}\n" +
        $"Report Location:    {Report.ReportFilePath}";

    [RelayCommand]
    private void SetTab(string tab)
    {
        SelectedTab = tab;
    }

    private static string ResolveExplorerPath()
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidate = Path.Combine(winDir, "explorer.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var sysDir = Environment.SystemDirectory;
        var sysCandidate = Path.GetFullPath(Path.Combine(sysDir, "..", "explorer.exe"));
        return File.Exists(sysCandidate) ? sysCandidate : candidate;
    }

    private static string ResolveUnixExecutable(string binaryName, string defaultFallback)
    {
        foreach (var dir in UnixStandardBinDirectories)
        {
            var candidate = Path.Combine(dir, binaryName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return defaultFallback;
    }

    private static string ResolveMacFileOpener() => ResolveUnixExecutable("open", "/usr/bin/open");

    private static string ResolveLinuxFileOpener() => ResolveUnixExecutable("xdg-open", "/usr/bin/xdg-open");

    [RelayCommand]
    public static void OpenLogFolder(string? filePath)
    {
        try
        {
            var targetPath = filePath;
            if (string.IsNullOrEmpty(targetPath))
            {
                targetPath = Path.Combine(AppContext.BaseDirectory, "appdata", "crash_reports");
            }

            if (OperatingSystem.IsWindows())
            {
                var explorerPath = ResolveExplorerPath();
                if (File.Exists(targetPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = explorerPath,
                        Arguments = $"/select,\"{targetPath}\"",
                        UseShellExecute = false
                    });
                }
                else
                {
                    var dir = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = explorerPath,
                            Arguments = $"\"{dir}\"",
                            UseShellExecute = false
                        });
                    }
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                var macOpenPath = ResolveMacFileOpener();
                if (File.Exists(targetPath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = macOpenPath,
                        Arguments = $"-R \"{targetPath}\"",
                        UseShellExecute = false
                    });
                }
                else
                {
                    var dir = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = macOpenPath,
                            Arguments = $"\"{dir}\"",
                            UseShellExecute = false
                        });
                    }
                }
            }
            else // Linux / Unix
            {
                var dir = Directory.Exists(targetPath) ? targetPath : Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    dir = Path.Combine(AppContext.BaseDirectory, "appdata", "crash_reports");
                }

                if (Directory.Exists(dir))
                {
                    var xdgOpenPath = ResolveLinuxFileOpener();
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = xdgOpenPath,
                        Arguments = $"\"{dir}\"",
                        UseShellExecute = false
                    });
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open crash log folder in native file manager.", ex);
            ToastNotificationService.Instance.ShowToast("File Manager Error", "Unable to launch native file explorer.");
        }
    }

    [RelayCommand]
    private async Task CopyDetailsAsync()
    {
        var content = Report.FullReportText;
        if (string.IsNullOrWhiteSpace(content))
        {
            content = $"CRASH REPORT [{Report.ErrorId}]\nTimestamp: {Report.Timestamp:O}\nSource: {Report.Source}\nException: {Report.ExceptionType}\nMessage: {Report.Message}\nMemory Dump: {Report.DumpFilePath} ({Report.FormattedDumpSize})\n\nStack Trace:\n{Report.StackTrace}";
        }

        var success = await ClipboardService.SetTextAsync(content);
        if (success)
        {
            ToastNotificationService.Instance.ShowToast("Copied", "Full diagnostic crash report with breadcrumbs and system stats copied to clipboard.");
        }
    }

    [RelayCommand]
    private async Task CopyDumpPathAsync()
    {
        if (string.IsNullOrEmpty(Report.DumpFilePath)) return;
        var success = await ClipboardService.SetTextAsync(Report.DumpFilePath);
        if (success)
        {
            ToastNotificationService.Instance.ShowToast("Copied", "Memory dump file path copied to clipboard.");
        }
    }

    [RelayCommand]
    public static void RestartApp()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false
                });
            }
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to restart application.", ex);
        }
    }

    [RelayCommand]
    private void Close() => _onClose();
}