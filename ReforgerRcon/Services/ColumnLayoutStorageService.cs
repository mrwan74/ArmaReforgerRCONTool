using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
    private static readonly string StorageDirectory = AppPaths.AppDataDirectory;
    private static readonly string StorageFile = Path.Combine(StorageDirectory, "grid_columns.json");

    private static Dictionary<string, Dictionary<string, ColumnState>> _cache = [];
    private static volatile bool _isLoaded;
    private static readonly SemaphoreSlim CacheSemaphore = new(1, 1);
    private static readonly HashSet<DataGrid> RestoringGrids = [];
    private static readonly HashSet<DataGrid> BoundGrids = [];
    private static CancellationTokenSource? _saveDebounceCts;

    public static void Prewarm()
    {
        if (_isLoaded) return;
        _ = Task.Run(EnsureLoaded, CancellationToken.None);
    }

    private static void EnsureLoaded()
    {
        if (_isLoaded) return;

        CacheSemaphore.Wait(CancellationToken.None);
        try
        {
            if (_isLoaded) return;

            var start = Stopwatch.GetTimestamp();

            if (!File.Exists(StorageFile))
            {
                _cache = GetDefaultColumnMap();
                _isLoaded = true;
                AppLogger.Debug($"[ColumnLayoutStorage:Load] Defaults initialized in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
                return;
            }

            try
            {
                var json = File.ReadAllText(StorageFile);
                _cache = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ColumnState>>>(json, JsonOptions) ?? GetDefaultColumnMap();
                _isLoaded = true;
                AppLogger.Debug($"[ColumnLayoutStorage:Load] Loaded column map in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms ({_cache.Count} grids).");
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[ColumnLayoutStorage:Load] Notice reading storage: {ex.Message}. Using defaults.");
                _cache = GetDefaultColumnMap();
                _isLoaded = true;
            }
        }
        finally
        {
            CacheSemaphore.Release();
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

        CacheSemaphore.Wait(CancellationToken.None);
        try
        {
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
        }
        finally
        {
            CacheSemaphore.Release();
        }

        ScheduleDebouncedSave();
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

    private static void ScheduleDebouncedSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _saveDebounceCts = new CancellationTokenSource();
        var token = _saveDebounceCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(300, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                string json;
                await CacheSemaphore.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    json = JsonSerializer.Serialize(_cache, JsonOptions);
                }
                finally
                {
                    CacheSemaphore.Release();
                }

                if (!Directory.Exists(StorageDirectory))
                {
                    Directory.CreateDirectory(StorageDirectory);
                }

                await File.WriteAllTextAsync(StorageFile, json, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                AppLogger.Trace($"[ColumnLayoutStorage:Save] Operation canceled: {ex.Message}");
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[ColumnLayoutStorage:Save] Async notice: {ex.Message}");
            }
        }, token);
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

        Dictionary<string, ColumnState>? columnMap;
        CacheSemaphore.Wait(CancellationToken.None);
        try
        {
            if (!_cache.TryGetValue(gridKey, out columnMap)) return;
        }
        finally
        {
            CacheSemaphore.Release();
        }

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
                catch (ArgumentOutOfRangeException ex)
                {
                    AppLogger.Trace($"[ColumnLayoutStorage:Order] Index out of range: {ex.Message}");
                }
            }
        }
    }

    private static string GetColumnKey(DataGridColumn col) => col.Tag?.ToString() ?? col.Header?.ToString() ?? col.GetType().Name;
}