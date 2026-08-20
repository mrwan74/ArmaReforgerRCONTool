// FILE: ReforgerRcon/Services/WindowStateStorageService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;

namespace ReforgerRcon.Services;

public class WindowStateModel
{
    public double Width { get; set; } = 1696;
    public double Height { get; set; } = 937;
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public bool IsMaximized { get; set; }
}

public static class WindowStateStorageService
{
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string StorageFile = Path.Combine(StorageDirectory, "window_state.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static void BindWindowPersistence(Window window, string windowKey = "MainWindow")
    {
        window.Opened += (_, _) => RestoreWindowState(window, windowKey);
        window.Closing += (_, _) => SaveWindowState(window, windowKey);
    }

    private static void RestoreWindowState(Window window, string windowKey)
    {
        try
        {
            if (!File.Exists(StorageFile)) return;
            var json = File.ReadAllText(StorageFile);
            var dict = JsonSerializer.Deserialize<Dictionary<string, WindowStateModel>>(json);
            if (dict != null && dict.TryGetValue(windowKey, out var state))
            {
                ApplyStateToWindow(window, state);
                AppLogger.Info($"Restored window geometry for '{windowKey}': {state.Width}x{state.Height} at ({state.X},{state.Y}) [Maximized: {state.IsMaximized}]");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to restore window state for {windowKey}", ex);
        }
    }

    private static void ApplyStateToWindow(Window window, WindowStateModel state)
    {
        if (state.Width >= window.MinWidth) window.Width = state.Width;
        if (state.Height >= window.MinHeight) window.Height = state.Height;

        if (state.X >= 0 && state.Y >= 0)
        {
            window.Position = new PixelPoint(state.X, state.Y);
        }

        if (state.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private static void SaveWindowState(Window window, string windowKey)
    {
        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
            }

            var dict = LoadCurrentStateDictionary();
            dict[windowKey] = CreateStateFromWindow(window);

            File.WriteAllText(StorageFile, JsonSerializer.Serialize(dict, JsonOptions));
            AppLogger.Debug($"Saved window geometry state for '{windowKey}'.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to persist window state for {windowKey}", ex);
        }
    }

    private static Dictionary<string, WindowStateModel> LoadCurrentStateDictionary()
    {
        if (!File.Exists(StorageFile)) return [];

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, WindowStateModel>>(File.ReadAllText(StorageFile)) ?? [];
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed reading existing window state dictionary: {ex.Message}");
            return [];
        }
    }

    private static WindowStateModel CreateStateFromWindow(Window window)
    {
        var isMax = window.WindowState == WindowState.Maximized;
        return new WindowStateModel
        {
            Width = isMax ? window.Bounds.Width : window.Width,
            Height = isMax ? window.Bounds.Height : window.Height,
            X = window.Position.X,
            Y = window.Position.Y,
            IsMaximized = isMax
        };
    }
}