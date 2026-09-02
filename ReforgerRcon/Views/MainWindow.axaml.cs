using System;
using System.Diagnostics;
using LuminaUI.Controls;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views;

public partial class MainWindow : LuminaWindow
{
    public MainWindow()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            AppLogger.Debug("[MainWindow:Init] Commencing visual tree initialization...");
            InitializeComponent();
            ApplyInitialGlassSetting();
            DataContext = new MainViewModel();
            WindowStateStorageService.BindWindowPersistence(this, "MainWindow");
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Info($"[MainWindow:Init] Main window visual tree initialized in {elapsedMs:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("Failed initializing MainWindow visual tree component.", ex);
            CrashReportService.HandleFatalException("MainWindow.Constructor", ex, isTerminating: true);
            throw;
        }
    }

    private void ApplyInitialGlassSetting()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var settings = AppSettings.LoadFromDisk();
            UseWindowGlass = settings.EnableWindowGlass;
            AppLogger.Debug($"[MainWindow:Glass] Applied UseWindowGlass={UseWindowGlass} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[MainWindow:Glass] Inspection notice: {ex.Message}");
        }
    }
}