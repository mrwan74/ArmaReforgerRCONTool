using System;
using Avalonia.Controls;
using LuminaUI.Controls;
using ReforgerRcon.Services;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views;

public partial class MainWindow : LuminaWindow
{
    public MainWindow()
    {
        try
        {
            InitializeComponent();
            DataContext = new MainViewModel();
            WindowStateStorageService.BindWindowPersistence(this, "MainWindow");
            DebugLayoutLoggerService.AttachMainWindow(this);
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("Failed initializing MainWindow visual tree component.", ex);
            CrashReportService.HandleFatalException("MainWindow.Constructor", ex, isTerminating: true);
            throw;
        }
    }
}