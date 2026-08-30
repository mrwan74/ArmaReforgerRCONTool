using System;
using System.IO;
using System.Text.Json;
using LuminaUI.Controls;
using ReforgerRcon.Models;
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
            ApplyInitialGlassSetting();
            DataContext = new MainViewModel();
            WindowStateStorageService.BindWindowPersistence(this, "MainWindow");
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
        try
        {
            var settingsFile = Path.Combine(AppContext.BaseDirectory, "appdata", "settings.json");
            if (File.Exists(settingsFile))
            {
                var json = File.ReadAllText(settingsFile);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    UseWindowGlass = settings.EnableWindowGlass;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[MainWindow] Glass inspection notice: {ex.Message}");
        }
    }
}