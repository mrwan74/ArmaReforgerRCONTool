using System;
using System.Collections.Generic;
using System.IO;

namespace ReforgerRcon.Models;

public class ErrorReportModel
{
    public string ErrorId { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ExceptionType { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string StackTrace { get; set; } = string.Empty;
    public bool IsTerminating { get; set; }
    public string ReportFilePath { get; set; } = string.Empty;
    public string DumpFilePath { get; set; } = string.Empty;
    public long DumpFileSizeBytes { get; set; }

    public string OsVersion { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string ClrVersion { get; set; } = string.Empty;
    public double RamWorkingSetMb { get; set; }
    public TimeSpan ProcessUptime { get; set; }
    public int ProcessorCount { get; set; }
    public int ThreadId { get; set; }
    public List<string> Breadcrumbs { get; set; } = [];
    public string FullReportText { get; set; } = string.Empty;

    public bool HasMemoryDump => !string.IsNullOrEmpty(DumpFilePath) && File.Exists(DumpFilePath);
    public string FormattedDumpSize => DumpFileSizeBytes > 0 ? $"{DumpFileSizeBytes / (1024.0 * 1024.0):F2} MB" : "N/A";
    public string FormattedUptime => $"{ProcessUptime.Hours:D2}h {ProcessUptime.Minutes:D2}m {ProcessUptime.Seconds:D2}s";
}