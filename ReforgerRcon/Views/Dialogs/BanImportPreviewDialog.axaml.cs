using System;
using System.Diagnostics;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class BanImportPreviewDialog : UserControl
{
    public BanImportPreviewDialog()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            InitializeComponent();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[BanImportPreviewDialog:Init] Dialog component initialized in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("[BanImportPreviewDialog:Init] Dialog visual tree initialization failed.", ex);
            CrashReportService.HandleFatalException("BanImportPreviewDialog.Constructor", ex, isTerminating: false);
            throw;
        }
    }
}