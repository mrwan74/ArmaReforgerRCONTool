using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views.Tabs;

public partial class DatabaseTabView : UserControl
{
    public DatabaseTabView()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            InitializeComponent();
            AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnGridContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            Loaded += async (_, _) =>
            {
                if (DataContext is DatabaseViewModel vm)
                {
                    var loadStart = Stopwatch.GetTimestamp();
                    try
                    {
                        AppLogger.Debug("[DatabaseTabView:Loaded] Refreshing database records on tab loaded...");
                        await vm.LoadDbAsync().ConfigureAwait(false);
                        AppLogger.Debug($"[DatabaseTabView:Loaded] Sync finished in {Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds:F2}ms.");
                    }
                    catch (OperationCanceledException opEx)
                    {
                        AppLogger.Debug($"[DatabaseTabView:Loaded] Load canceled: {opEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("[DatabaseTabView:Loaded] Error loading database on tab loaded.", ex);
                        ToastNotificationService.Instance.ShowError("Database Load Error", "Failed retrieving records.");
                    }
                }
            };

            DataContextChanged += (_, _) =>
            {
                if (DataContext is DatabaseViewModel vm)
                {
                    var bindStart = Stopwatch.GetTimestamp();
                    try
                    {
                        var gridKey = vm.IsBattlEyeProtocol ? "DatabaseGrid_BattlEye" : "DatabaseGrid_Reforger";
                        ColumnLayoutStorageService.BindPersistence(DatabaseGrid, gridKey);

                        UpdateColumnVisibilities();
                        UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);

                        vm.PropertyChanged += (s, e) =>
                        {
                            try
                            {
                                if (e.PropertyName is nameof(DatabaseViewModel.IsMultiSelectMode) or
                                                     nameof(DatabaseViewModel.IsReforgerProtocol) or
                                                     nameof(DatabaseViewModel.IsBattlEyeProtocol))
                                {
                                    UpdateColumnVisibilities();
                                    UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
                                }
                            }
                            catch (Exception propEx)
                            {
                                AppLogger.Error("[DatabaseTabView] Error handling property change: " + propEx.Message, propEx);
                            }
                        };
                        AppLogger.Debug($"[DatabaseTabView:Bind] DataContext configured in {Stopwatch.GetElapsedTime(bindStart).TotalMilliseconds:F2}ms.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Failed configuring DatabaseTabView bindings.", ex);
                    }
                }
            };

            DebugLayoutLoggerService.RegisterDataGrid("DatabaseGrid", DatabaseGrid);
            AppLogger.Debug($"[DatabaseTabView:Init] Initialized in {Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed during DatabaseTabView initialization.", ex);
            CrashReportService.HandleFatalException("DatabaseTabView.Constructor", ex, isTerminating: false);
        }
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            e.Handled = true;
            var tag = e.Column.Tag?.ToString() ?? string.Empty;
            if (DataContext is DatabaseViewModel vm)
            {
                vm.CycleColumnSort(tag);
                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
            }
            AppLogger.Trace($"[DatabaseTabView:Sort] Handled in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (ArgumentException argEx)
        {
            AppLogger.Warn($"[DatabaseTabView:Sort] Invalid sorting parameter: {argEx.Message}", argEx);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DatabaseTabView:Sort] Error in sorting: " + ex.Message, ex);
            ToastNotificationService.Instance.ShowWarning("Sort Error", "Unable to cycle sort for selected column.");
        }
    }

    private static string GetCleanHeader(DataGridColumn col)
    {
        var headerText = col.Header?.ToString() ?? string.Empty;
        return headerText.TrimEnd(' ', '▲', '▼');
    }

    private void UpdateColumnSortGlyphs(string sortField, bool isAscending)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            foreach (var col in DatabaseGrid.Columns)
            {
                var tag = col.Tag?.ToString() ?? string.Empty;
                var field = DatabaseViewModel.MapColumnTagToSortField(tag);
                var cleanHeader = GetCleanHeader(col);

                if (string.IsNullOrEmpty(cleanHeader)) continue;

                if (!string.IsNullOrEmpty(field) &&
                    string.Equals(field, sortField, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sortField, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    col.Header = isAscending ? $"{cleanHeader} ▲" : $"{cleanHeader} ▼";
                }
                else
                {
                    col.Header = cleanHeader;
                }
            }
            AppLogger.Trace($"[DatabaseTabView:SortGlyph] Updated in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[DatabaseTabView:SortGlyph] Update notice: {ex.Message}");
        }
    }

    private void UpdateColumnVisibilities()
    {
        if (DataContext is not DatabaseViewModel vm) return;

        var start = Stopwatch.GetTimestamp();
        try
        {
            foreach (var col in DatabaseGrid.Columns)
            {
                var tag = col.Tag?.ToString();
                if (tag == null) continue;

                switch (tag)
                {
                    case "ColSelect":
                        col.IsVisible = vm.IsMultiSelectMode;
                        break;
                    case "ColReforgerId":
                        col.IsVisible = false;
                        break;
                    case "ColReforgerName":
                    case "ColReforgerUid":
                        col.IsVisible = vm.IsReforgerProtocol;
                        break;
                    case "ColBeId":
                    case "ColBeCountry":
                    case "ColBeName":
                    case "ColBeGuid":
                    case "ColBeEndpoint":
                    case "ColBePing":
                        col.IsVisible = vm.IsBattlEyeProtocol;
                        break;
                }
            }
            AppLogger.Trace($"[DatabaseTabView:Visibility] Updated in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error updating DatabaseGrid column visibility.", ex);
        }
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var point = e.GetCurrentPoint(this);
            if (point.Properties.IsRightButtonPressed && e.Source is Visual visual)
            {
                var row = visual.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is DatabasePlayerModel player)
                {
                    DatabaseGrid.SelectedItem = player;
                }
                else
                {
                    DatabaseGrid.SelectedItem = null;
                }
            }
        }
        catch (InvalidOperationException invOpEx)
        {
            AppLogger.Trace($"[DatabaseTabView:Pointer] Lookup notice: {invOpEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[DatabaseTabView:Pointer] Lookup notice: {ex.Message}");
        }
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        try
        {
            if (e.Source is Visual visual)
            {
                var row = visual.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is DatabasePlayerModel player)
                {
                    DatabaseGrid.SelectedItem = player;
                    return;
                }
            }

            DatabaseGrid.SelectedItem = null;
            e.Handled = true;
        }
        catch (InvalidOperationException invOpEx)
        {
            AppLogger.Trace($"[DatabaseTabView:Context] Lookup notice: {invOpEx.Message}");
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[DatabaseTabView:Context] Lookup notice: {ex.Message}");
            e.Handled = true;
        }
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (e.Source is Visual visual && visual.FindAncestorOfType<Button>() != null)
            {
                return;
            }

            if (DataContext is DatabaseViewModel vm && DatabaseGrid.SelectedItem is DatabasePlayerModel player)
            {
                vm.OpenPlayerDetails(player);
                e.Handled = true;
                AppLogger.Trace($"[DatabaseTabView:DoubleTap] Dispatched in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            }
        }
        catch (InvalidOperationException invOpEx)
        {
            AppLogger.Warn($"[DatabaseTabView:DoubleTap] Double tap invalid: {invOpEx.Message}", invOpEx);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error handling DatabaseGrid double tap event.", ex);
            ToastNotificationService.Instance.ShowError("Dialog Error", "Failed to open player details.");
        }
    }
}