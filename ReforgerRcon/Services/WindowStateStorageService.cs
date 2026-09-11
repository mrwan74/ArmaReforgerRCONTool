using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private static readonly string StorageDirectory = AppPaths.AppDataDirectory;
    private static readonly string StorageFile = Path.Combine(StorageDirectory, "window_state.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static Dictionary<string, WindowStateModel>? _cachedDictionary;

    public static void BindWindowPersistence(Window window, string windowKey = "MainWindow")
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowKey);

        RestoreWindowState(window, windowKey);
        window.Closing += (_, _) => SaveWindowState(window, windowKey);
    }

    private static void RestoreWindowState(Window window, string windowKey)
    {
        var dict = LoadCurrentStateDictionary();
        if (dict != null && dict.TryGetValue(windowKey, out var state))
        {
            ApplyStateToWindow(window, state);
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

            var dict = LoadCurrentStateDictionary() ?? [];
            dict[windowKey] = CreateStateFromWindow(window);
            _cachedDictionary = dict;

            File.WriteAllText(StorageFile, JsonSerializer.Serialize(dict, JsonOptions));
        }
        catch (IOException ioEx)
        {
            AppLogger.Error($"[WindowStateStorage:Save] Disk error for {windowKey}: {ioEx.Message}", ioEx);
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Error($"[WindowStateStorage:Save] Access denied for {windowKey}: {authEx.Message}", authEx);
        }
    }

    private static Dictionary<string, WindowStateModel>? LoadCurrentStateDictionary()
    {
        if (_cachedDictionary != null)
        {
            return _cachedDictionary;
        }

        if (!File.Exists(StorageFile)) return null;

        try
        {
            _cachedDictionary = JsonSerializer.Deserialize<Dictionary<string, WindowStateModel>>(File.ReadAllText(StorageFile), JsonOptions);
            return _cachedDictionary;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[WindowStateStorage:LoadDict] Notice: {ex.Message}");
            return null;
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