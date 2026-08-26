using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;

namespace ReforgerRcon.Services;

public class ColumnState
{
    public double Width { get; set; }
    public int DisplayIndex { get; set; } = -1;
}

public static class ColumnLayoutStorageService
{
    private const string ColSelectKey = "ColSelect";
    private const string ColActionsKey = "ColActions";
    private const string ColStatusKey = "ColStatus";
    private const string ColCommentKey = "ColComment";
    private const string ColReforgerIdKey = "ColReforgerId";
    private const string ColReforgerNameKey = "ColReforgerName";
    private const string ColReforgerUidKey = "ColReforgerUid";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string StorageDirectory = Path.Combine(AppContext.BaseDirectory, "appdata");
    private static readonly string StorageFile = Path.Combine(StorageDirectory, "grid_columns.json");

    private static Dictionary<string, Dictionary<string, ColumnState>> _cache = [];
    private static bool _isLoaded;
    private static readonly HashSet<DataGrid> RestoringGrids = [];

    private static void EnsureLoaded()
    {
        if (_isLoaded) return;
        _isLoaded = true;
        var sw = Stopwatch.StartNew();

        try
        {
            if (File.Exists(StorageFile))
            {
                var json = File.ReadAllText(StorageFile);
                _cache = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ColumnState>>>(json) ?? GetDefaultColumnMap();
                sw.Stop();
                AppLogger.Info($"[ColumnLayoutStorage] Loaded column layouts from '{StorageFile}' ({_cache.Count} table configurations in {sw.ElapsedMilliseconds} ms).");
            }
            else
            {
                AppLogger.Info("[ColumnLayoutStorage] No existing column layout file found. Initializing default column dimensions.");
                _cache = GetDefaultColumnMap();
            }
        }
        catch (JsonException jsonEx)
        {
            sw.Stop();
            AppLogger.Warn($"[ColumnLayoutStorage] Corrupted JSON in '{StorageFile}': {jsonEx.Message}. Reverting to defaults.");
            _cache = GetDefaultColumnMap();
        }
        catch (IOException ioEx)
        {
            sw.Stop();
            AppLogger.Warn($"[ColumnLayoutStorage] Disk I/O error reading '{StorageFile}': {ioEx.Message}. Reverting to defaults.");
            _cache = GetDefaultColumnMap();
        }
        catch (UnauthorizedAccessException authEx)
        {
            sw.Stop();
            AppLogger.Warn($"[ColumnLayoutStorage] Access denied reading '{StorageFile}': {authEx.Message}. Reverting to defaults.");
            _cache = GetDefaultColumnMap();
        }
    }

    private static Dictionary<string, Dictionary<string, ColumnState>> GetDefaultColumnMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PlayersGrid_BattlEye"] = new(StringComparer.OrdinalIgnoreCase)
        {
            [ColStatusKey] = new() { Width = 95, DisplayIndex = 1 },
            [ColReforgerIdKey] = new() { Width = 110, DisplayIndex = 2 },
            [ColReforgerNameKey] = new() { Width = 256, DisplayIndex = 3 },
            [ColReforgerUidKey] = new() { Width = 396, DisplayIndex = 4 },
            ["ColBeId"] = new() { Width = 72, DisplayIndex = 5 },
            ["ColBeCountry"] = new() { Width = 107, DisplayIndex = 6 },
            ["ColBeName"] = new() { Width = 220, DisplayIndex = 7 },
            ["ColBeGuid"] = new() { Width = 343, DisplayIndex = 8 },
            ["ColBeEndpoint"] = new() { Width = 217, DisplayIndex = 9 },
            ["ColBePing"] = new() { Width = 90, DisplayIndex = 10 },
            [ColCommentKey] = new() { Width = 348, DisplayIndex = 11 },
            [ColActionsKey] = new() { Width = 195, DisplayIndex = 12 }
        },
        ["PlayersGrid_Reforger"] = new(StringComparer.OrdinalIgnoreCase)
        {
            [ColStatusKey] = new() { Width = 114, DisplayIndex = 1 },
            [ColReforgerIdKey] = new() { Width = 110, DisplayIndex = 2 },
            [ColReforgerNameKey] = new() { Width = 256, DisplayIndex = 3 },
            [ColReforgerUidKey] = new() { Width = 396, DisplayIndex = 4 },
            ["ColBeId"] = new() { Width = 72, DisplayIndex = 5 },
            ["ColBeCountry"] = new() { Width = 107, DisplayIndex = 6 },
            ["ColBeName"] = new() { Width = 220, DisplayIndex = 7 },
            ["ColBeGuid"] = new() { Width = 343, DisplayIndex = 8 },
            ["ColBeEndpoint"] = new() { Width = 217, DisplayIndex = 9 },
            ["ColBePing"] = new() { Width = 90, DisplayIndex = 10 },
            [ColCommentKey] = new() { Width = 434, DisplayIndex = 11 },
            [ColActionsKey] = new() { Width = 195, DisplayIndex = 12 }
        },
        ["BansGrid_Reforger"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ColReforgerBannedName"] = new() { Width = 260, DisplayIndex = 1 },
            ["ColReforgerIdentity"] = new() { Width = 800, DisplayIndex = 2 },
            [ColActionsKey] = new() { Width = 140, DisplayIndex = 3 }
        },
        ["BansGrid_BattlEye"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ColBeBanNo"] = new() { Width = 70, DisplayIndex = 1 },
            ["ColBeIdentity"] = new() { Width = 320, DisplayIndex = 2 },
            ["ColBeMinutes"] = new() { Width = 150, DisplayIndex = 3 },
            ["ColBeReason"] = new() { Width = 700, DisplayIndex = 4 },
            [ColActionsKey] = new() { Width = 140, DisplayIndex = 5 }
        },
        ["DatabaseGrid_BattlEye"] = new(StringComparer.OrdinalIgnoreCase)
        {
            [ColStatusKey] = new() { Width = 95, DisplayIndex = 1 },
            ["ColBeId"] = new() { Width = 72, DisplayIndex = 2 },
            ["ColBeCountry"] = new() { Width = 107, DisplayIndex = 3 },
            ["ColBeName"] = new() { Width = 204, DisplayIndex = 4 },
            ["ColBeGuid"] = new() { Width = 343, DisplayIndex = 5 },
            ["ColBeEndpoint"] = new() { Width = 217, DisplayIndex = 6 },
            ["ColBePing"] = new() { Width = 90, DisplayIndex = 7 },
            [ColCommentKey] = new() { Width = 323, DisplayIndex = 8 },
            [ColActionsKey] = new() { Width = 195, DisplayIndex = 9 }
        },
        ["DatabaseGrid_Reforger"] = new(StringComparer.OrdinalIgnoreCase)
        {
            [ColStatusKey] = new() { Width = 95, DisplayIndex = 1 },
            [ColReforgerNameKey] = new() { Width = 220, DisplayIndex = 2 },
            [ColReforgerUidKey] = new() { Width = 260, DisplayIndex = 3 },
            [ColCommentKey] = new() { Width = 323, DisplayIndex = 4 },
            [ColActionsKey] = new() { Width = 195, DisplayIndex = 5 }
        }
    };

    public static void SaveGridState(string gridKey, DataGrid dataGrid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gridKey);
        ArgumentNullException.ThrowIfNull(dataGrid);

        if (RestoringGrids.Contains(dataGrid)) return;

        try
        {
            EnsureLoaded();

            if (!_cache.TryGetValue(gridKey, out var columnMap))
            {
                columnMap = new Dictionary<string, ColumnState>(StringComparer.OrdinalIgnoreCase);
                _cache[gridKey] = columnMap;
            }

            var changes = new List<string>();

            foreach (var col in dataGrid.Columns)
            {
                var key = GetColumnKey(col);
                if (key == ColSelectKey) continue;

                double newWidth = ResolveColumnWidth(col);
                int newIndex = col.DisplayIndex;

                if (columnMap.TryGetValue(key, out var previousState))
                {
                    if (Math.Abs(previousState.Width - newWidth) > 0.5)
                    {
                        changes.Add($"{key}: {previousState.Width:F1}px -> {newWidth:F1}px");
                    }
                    previousState.Width = newWidth;
                    previousState.DisplayIndex = newIndex;
                }
                else
                {
                    columnMap[key] = new ColumnState
                    {
                        Width = newWidth,
                        DisplayIndex = newIndex
                    };
                    changes.Add($"{key}: New({newWidth:F1}px)");
                }
            }

            Save();

            if (changes.Count > 0)
            {
                AppLogger.Debug($"[ColumnLayoutStorage] Grid '{gridKey}' layout modified: [{string.Join(", ", changes)}]");
            }
            else
            {
                AppLogger.Trace($"[ColumnLayoutStorage] Saved {columnMap.Count} column layout settings for grid '{gridKey}'.");
            }
        }
        catch (IOException ioEx)
        {
            AppLogger.Error($"[ColumnLayoutStorage] Disk I/O error saving layout for grid '{gridKey}': {ioEx.Message}", ioEx);
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Error($"[ColumnLayoutStorage] Access denied saving layout for grid '{gridKey}': {authEx.Message}", authEx);
        }
    }

    private static double ResolveColumnWidth(DataGridColumn col)
    {
        if (col.ActualWidth > 20)
        {
            return Math.Round(col.ActualWidth, 1);
        }

        if (col.Width.IsAbsolute)
        {
            return col.Width.Value;
        }

        return 100;
    }

    private static void Save()
    {
        try
        {
            if (!Directory.Exists(StorageDirectory))
            {
                Directory.CreateDirectory(StorageDirectory);
            }
            var json = JsonSerializer.Serialize(_cache, JsonOptions);
            File.WriteAllText(StorageFile, json);
        }
        catch (IOException ioEx)
        {
            AppLogger.Error($"[ColumnLayoutStorage] I/O error writing column layout to disk: {ioEx.Message}", ioEx);
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Error($"[ColumnLayoutStorage] Permission error writing column layout to disk: {authEx.Message}", authEx);
        }
    }

    public static void BindPersistence(DataGrid dataGrid, string gridKey)
    {
        ArgumentNullException.ThrowIfNull(dataGrid);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridKey);

        EnsureLoaded();
        RestoreGridState(dataGrid, gridKey);

        dataGrid.Loaded += (_, _) => RestoreGridState(dataGrid, gridKey);
        dataGrid.Unloaded += (_, _) => SaveGridState(gridKey, dataGrid);
        dataGrid.DetachedFromVisualTree += (_, _) => SaveGridState(gridKey, dataGrid);

        foreach (var col in dataGrid.Columns)
        {
            col.PropertyChanged += (_, e) =>
            {
                if (RestoringGrids.Contains(dataGrid)) return;

                if (e.Property.Name is "ActualWidth" or "Width" or "DisplayIndex")
                {
                    SaveGridState(gridKey, dataGrid);
                }
            };
        }
        AppLogger.Debug($"[ColumnLayoutStorage] Bound column persistence hooks for grid '{gridKey}'.");
    }

    public static void RestoreGridState(DataGrid dataGrid, string gridKey)
    {
        ArgumentNullException.ThrowIfNull(dataGrid);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridKey);

        EnsureLoaded();
        if (!_cache.TryGetValue(gridKey, out var columnMap)) return;

        RestoringGrids.Add(dataGrid);
        try
        {
            var sw = Stopwatch.StartNew();
            RestoreWidths(dataGrid, columnMap);
            RestoreDisplayOrder(dataGrid, columnMap);
            sw.Stop();
            AppLogger.Debug($"[ColumnLayoutStorage] Restored column layout state for grid '{gridKey}' in {sw.ElapsedMilliseconds} ms.");
        }
        finally
        {
            RestoringGrids.Remove(dataGrid);
        }
    }

    private static void RestoreWidths(DataGrid dataGrid, Dictionary<string, ColumnState> columnMap)
    {
        foreach (var col in dataGrid.Columns)
        {
            var key = GetColumnKey(col);
            if (key != ColSelectKey && columnMap.TryGetValue(key, out var state) && state.Width > 20)
            {
                col.Width = new DataGridLength(state.Width, DataGridLengthUnitType.Pixel);
            }
        }
    }

    private static void RestoreDisplayOrder(DataGrid dataGrid, Dictionary<string, ColumnState> columnMap)
    {
        var orderedSaved = dataGrid.Columns
            .Where(c => GetColumnKey(c) != ColSelectKey && columnMap.TryGetValue(GetColumnKey(c), out var state) && state.DisplayIndex >= 0)
            .OrderBy(c => columnMap[GetColumnKey(c)].DisplayIndex)
            .ToList();

        foreach (var col in orderedSaved)
        {
            var targetIdx = columnMap[GetColumnKey(col)].DisplayIndex;
            if (targetIdx >= 0 && targetIdx < dataGrid.Columns.Count)
            {
                try
                {
                    col.DisplayIndex = targetIdx;
                }
                catch (ArgumentOutOfRangeException ex)
                {
                    AppLogger.Trace($"[ColumnLayoutStorage] Column reorder index skipped for '{GetColumnKey(col)}': {ex.Message}");
                }
            }
        }
    }

    private static string GetColumnKey(DataGridColumn col) => col.Tag?.ToString() ?? col.Header?.ToString() ?? col.GetType().Name;
}