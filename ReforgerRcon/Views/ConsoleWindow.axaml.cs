using System;
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
        try
        {
            InitializeComponent();
            ApplyInitialGlassSetting();
            WindowStateStorageService.BindWindowPersistence(this, "ConsoleWindow");
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
                    AppLogger.Debug($"[ConsoleWindow] Initialized UseWindowGlass={UseWindowGlass}");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[ConsoleWindow] Non-fatal glass setting inspection notice: {ex.Message}");
        }
    }
}