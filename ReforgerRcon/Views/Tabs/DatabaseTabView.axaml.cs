using System;
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
        try
        {
            InitializeComponent();
            AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnGridContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            Loaded += async (_, _) =>
            {
                if (DataContext is DatabaseViewModel vm)
                {
                    try
                    {
                        AppLogger.Debug("[DatabaseTabView] Tab Loaded event triggered: refreshing SQLite database records...");
                        await vm.LoadDbAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException opEx)
                    {
                        AppLogger.Debug($"[DatabaseTabView] Database load operation canceled on load: {opEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("[DatabaseTabView] Failed loading database records on tab loaded event.", ex);
                        ToastNotificationService.Instance.ShowError("Database Load Error", "Failed retrieving historical records from database.");
                    }
                }
            };

            DataContextChanged += (_, _) =>
            {
                if (DataContext is DatabaseViewModel vm)
                {
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
                                AppLogger.Error("[DatabaseTabView] Error handling property change event for database grid layout.", propEx);
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Failed configuring DatabaseTabView data context bindings.", ex);
                    }
                }
            };

            DebugLayoutLoggerService.RegisterDataGrid("DatabaseGrid", DatabaseGrid);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed during DatabaseTabView constructor initialization.", ex);
            CrashReportService.HandleFatalException("DatabaseTabView.Constructor", ex, isTerminating: false);
        }
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        try
        {
            e.Handled = true;
            var tag = e.Column.Tag?.ToString() ?? string.Empty;
            if (DataContext is DatabaseViewModel vm)
            {
                vm.CycleColumnSort(tag);
                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
            }
        }
        catch (ArgumentException argEx)
        {
            AppLogger.Warn($"[DatabaseTabView] Invalid column sorting parameter: {argEx.Message}", argEx);
        }
        catch (Exception ex)
        {
            AppLogger.Error("[DatabaseTabView] Unexpected exception in OnDataGridSorting.", ex);
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
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[DatabaseTabView] Sort glyph update notice: {ex.Message}");
        }
    }

    private void UpdateColumnVisibilities()
    {
        if (DataContext is not DatabaseViewModel vm) return;

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
            AppLogger.Trace($"[DatabaseTabView] Visual ancestor lookup invalid state on pointer press: {invOpEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[DatabaseTabView] OnGridPointerPressed handled visual lookup notice: {ex.Message}");
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
            AppLogger.Trace($"[DatabaseTabView] Visual ancestor lookup invalid state on context request: {invOpEx.Message}");
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[DatabaseTabView] OnGridContextRequested handled visual lookup notice: {ex.Message}");
            e.Handled = true;
        }
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
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
            }
        }
        catch (InvalidOperationException invOpEx)
        {
            AppLogger.Warn($"[DatabaseTabView] Double tap action invalid in current visual state: {invOpEx.Message}", invOpEx);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error handling DatabaseGrid double tap event.", ex);
            ToastNotificationService.Instance.ShowError("Dialog Error", "Failed to open player details dialog.");
        }
    }
}