using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ReforgerRcon.Services;

public static class DebugLayoutLoggerService
{
    private static readonly string LogFile = Path.Combine(AppPaths.AppDataDirectory, "debug_layout.txt");
    private static Window? _mainWindow;
    private static readonly Dictionary<string, DataGrid> RegisteredGrids = [];
    private static readonly Lock DumpLock = new();
    private static CancellationTokenSource? _debounceCts;

    public static void AttachMainWindow(Window window)
    {
        _mainWindow = window;
        window.SizeChanged += (_, _) => Dump();
        window.PositionChanged += (_, _) => Dump();
        window.Opened += (_, _) => Dump();
        window.Closing += (_, _) => Dump();
        Dump();
    }

    public static void RegisterDataGrid(string name, DataGrid grid)
    {
        RegisteredGrids[name] = grid;
        grid.SizeChanged += (_, _) => Dump();
        grid.Loaded += (_, _) => Dump();

        foreach (var col in grid.Columns)
        {
            col.PropertyChanged += (_, e) =>
            {
                if (e.Property.Name is "ActualWidth" or "Width" or "DisplayIndex")
                {
                    Dump();
                }
            };
        }

        Dump();
    }

    public static void Dump()
    {
        lock (DumpLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = ScheduleDebouncedDumpAsync(token);
        }
    }

    private static async Task ScheduleDebouncedDumpAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;

            Dispatcher.UIThread.Post(ExecuteDump, DispatcherPriority.Background);
        }
        catch (OperationCanceledException)
        {
            // Expected on debounce cancellation
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DebugLayoutLogger] Debounce notice: {ex.Message}");
        }
    }

    private static void ExecuteDump()
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var dir = Path.GetDirectoryName(LogFile);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("================================================================================");
            sb.AppendLine(CultureInfo.InvariantCulture, $"LAYOUT DEBUG SNAPSHOT - {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine("================================================================================");
            sb.AppendLine();

            if (_mainWindow != null)
            {
                sb.AppendLine("--- MAIN WINDOW ---");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Title:           {_mainWindow.Title}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Width x Height:  {_mainWindow.Width} x {_mainWindow.Height}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Bounds:          {_mainWindow.Bounds.Width:F1} x {_mainWindow.Bounds.Height:F1}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"ClientSize:      {_mainWindow.ClientSize.Width:F1} x {_mainWindow.ClientSize.Height:F1}");
                sb.AppendLine(CultureInfo.InvariantCulture, $"Position (X, Y): ({_mainWindow.Position.X}, {_mainWindow.Position.Y})");
                sb.AppendLine(CultureInfo.InvariantCulture, $"WindowState:     {_mainWindow.WindowState}");
                sb.AppendLine();
            }

            sb.AppendLine("--- DATA GRID COLUMNS ---");
            foreach (var (name, grid) in RegisteredGrids)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"Grid: [{name}] (Columns={grid.Columns.Count}, Bounds={grid.Bounds.Width:F1}x{grid.Bounds.Height:F1})");
                sb.AppendFormat(CultureInfo.InvariantCulture, "{0,-6} | {1,-22} | {2,-16} | {3,-15} | {4,-8} | {5,-10}", "Index", "Header/Tag", "ActualWidth (px)", "Width Setting", "Visible", "DisplayIdx").AppendLine();
                sb.AppendLine(new string('-', 90));

                int idx = 0;
                foreach (var col in grid.Columns)
                {
                    var header = col.Tag?.ToString() ?? col.Header?.ToString() ?? col.GetType().Name;
                    var actualWidth = string.Create(CultureInfo.InvariantCulture, $"{col.ActualWidth:F1} px");
                    var widthStr = col.Width.ToString();
                    var visible = col.IsVisible ? "True" : "False";
                    var displayIdx = col.DisplayIndex.ToString(CultureInfo.InvariantCulture);

                    sb.AppendFormat(CultureInfo.InvariantCulture, "{0,-6} | {1,-22} | {2,-16} | {3,-15} | {4,-8} | {5,-10}", idx.ToString(CultureInfo.InvariantCulture), header, actualWidth, widthStr, visible, displayIdx).AppendLine();
                    idx++;
                }
                sb.AppendLine();
            }

            File.WriteAllText(LogFile, sb.ToString());
            AppLogger.Trace($"[DebugLayoutLogger:Dump] Snapshot written in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DebugLayoutLogger] Error writing dump: {ex.Message}");
        }
    }
}