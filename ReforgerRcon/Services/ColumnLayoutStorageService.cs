using System;
using System.Collections.Generic;
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
    private static readonly HashSet<DataGrid> BoundGrids = [];

    private static void EnsureLoaded()
    {
        if (_isLoaded) return;
        _isLoaded = true;

        if (!File.Exists(StorageFile))
        {
            _cache = GetDefaultColumnMap();
            return;
        }

        try
        {
            var json = File.ReadAllText(StorageFile);
            _cache = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ColumnState>>>(json, JsonOptions) ?? GetDefaultColumnMap();
        }
        catch (JsonException jsonEx)
        {
            AppLogger.Warn($"[ColumnLayoutStorage] JSON deserialization notice for '{StorageFile}': {jsonEx.Message}. Using default columns.", jsonEx);
            _cache = GetDefaultColumnMap();
        }
        catch (IOException ioEx)
        {
            AppLogger.Warn($"[ColumnLayoutStorage] I/O error reading column configuration: {ioEx.Message}", ioEx);
            _cache = GetDefaultColumnMap();
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Warn($"[ColumnLayoutStorage] Permission denied reading column configuration: {authEx.Message}", authEx);
            _cache = GetDefaultColumnMap();
        }
    }

    private static Dictionary<string, Dictionary<string, ColumnState>> GetDefaultColumnMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["PlayersGrid_BattlEye"] = new(StringComparer.OrdinalIgnoreCase)
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

        EnsureLoaded();

        if (!_cache.TryGetValue(gridKey, out var columnMap))
        {
            columnMap = new Dictionary<string, ColumnState>(StringComparer.OrdinalIgnoreCase);
            _cache[gridKey] = columnMap;
        }

        foreach (var col in dataGrid.Columns)
        {
            var key = GetColumnKey(col);
            if (key == ColSelectKey) continue;

            double newWidth = ResolveColumnWidth(col);
            int newIndex = col.DisplayIndex;

            if (columnMap.TryGetValue(key, out var previousState))
            {
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
            }
        }

        Save();
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
            AppLogger.Error($"[ColumnLayoutStorage] Disk I/O error writing column layout to '{StorageFile}': {ioEx.Message}", ioEx);
        }
        catch (UnauthorizedAccessException authEx)
        {
            AppLogger.Error($"[ColumnLayoutStorage] Permission denied saving column layout: {authEx.Message}", authEx);
        }
    }

    public static void BindPersistence(DataGrid dataGrid, string gridKey)
    {
        ArgumentNullException.ThrowIfNull(dataGrid);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridKey);

        if (BoundGrids.Contains(dataGrid))
        {
            RestoreGridState(dataGrid, gridKey);
            return;
        }

        BoundGrids.Add(dataGrid);
        EnsureLoaded();
        RestoreGridState(dataGrid, gridKey);

        dataGrid.Unloaded += (_, _) => SaveGridState(gridKey, dataGrid);
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
            RestoreWidths(dataGrid, columnMap);
            RestoreDisplayOrder(dataGrid, columnMap);
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
                catch (ArgumentOutOfRangeException argEx)
                {
                    AppLogger.Warn($"[ColumnLayoutStorage] Column '{GetColumnKey(col)}' index {targetIdx} out of range: {argEx.Message}");
                }
            }
        }
    }

    private static string GetColumnKey(DataGridColumn col) => col.Tag?.ToString() ?? col.Header?.ToString() ?? col.GetType().Name;
}