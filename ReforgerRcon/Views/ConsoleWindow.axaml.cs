using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using LuminaUI.Controls;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views;

public partial class ConsoleWindow : LuminaWindow
{
    private readonly Action? _onReattach;

    public ConsoleWindow()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            InitializeComponent();
            ApplyInitialGlassSetting();
            WindowStateStorageService.BindWindowPersistence(this, "ConsoleWindow");
            AppLogger.Debug($"[ConsoleWindow:Init] Standalone console window initialized in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed initializing ConsoleWindow component.", ex);
        }
    }

    public ConsoleWindow(ConsoleViewModel vm, Action onReattach) : this()
    {
        DataContext = vm;
        _onReattach = onReattach;
        Closed += (_, _) =>
        {
            try
            {
                AppLogger.Info("[ConsoleWindow:Close] Closed. Invoking reattach callback...");
                _onReattach?.Invoke();
            }
            catch (Exception ex)
            {
                AppLogger.Error("Exception during console window reattach callback.", ex);
            }
        };
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
                    AppLogger.Debug($"[ConsoleWindow:Glass] Initialized UseWindowGlass={UseWindowGlass}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[ConsoleWindow:Glass] Inspection notice: {ex.Message}");
        }
    }
}