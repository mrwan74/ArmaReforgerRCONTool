using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using ReforgerRcon.Services;

namespace ReforgerRcon.Views.Dialogs;

public partial class UpdateDialog : UserControl
{
    public UpdateDialog()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AppLogger.Trace("[UpdateDialog:Constructor] Instantiating UpdateDialog XAML visual tree...");
            InitializeComponent();

            AttachedToVisualTree += OnAttachedToVisualTree;
            DetachedFromVisualTree += OnDetachedFromVisualTree;

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[UpdateDialog:Init] Visual tree initialized successfully in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("[UpdateDialog:Init] Fatal exception initializing UpdateDialog XAML visual tree.", ex);
            CrashReportService.HandleFatalException("UpdateDialog.Constructor", ex, isTerminating: false);
            throw;
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        try
        {
            var rootVisualName = e.RootVisual?.GetType().Name ?? "UnknownRoot";
            AppLogger.Trace($"[UpdateDialog:VisualTree] Attached to visual root '{rootVisualName}'. Bounds: {Bounds.Width:F1}x{Bounds.Height:F1}");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[UpdateDialog:VisualTree] Non-critical visual tree attachment notice: {ex.Message}");
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        try
        {
            AppLogger.Trace("[UpdateDialog:VisualTree] Detached from visual tree.");
            AttachedToVisualTree -= OnAttachedToVisualTree;
            DetachedFromVisualTree -= OnDetachedFromVisualTree;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[UpdateDialog:VisualTree] Non-critical visual tree detachment notice: {ex.Message}");
        }
    }
}