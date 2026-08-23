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
                    AppLogger.Debug("[DatabaseTabView] Tab Loaded event triggered: refreshing SQLite database records...");
                    await vm.LoadDbAsync();
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
                            if (e.PropertyName is nameof(DatabaseViewModel.IsMultiSelectMode) or
                                                 nameof(DatabaseViewModel.IsReforgerProtocol) or
                                                 nameof(DatabaseViewModel.IsBattlEyeProtocol))
                            {
                                UpdateColumnVisibilities();
                                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
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
        }
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;
        var tag = e.Column.Tag?.ToString() ?? "";
        if (DataContext is DatabaseViewModel vm)
        {
            vm.CycleColumnSort(tag);
            UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
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
                var tag = col.Tag?.ToString() ?? "";
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
        catch (Exception ex)
        {
            AppLogger.Trace($"OnGridPointerPressed handled non-fatal visual lookup: {ex.Message}");
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
        catch (Exception ex)
        {
            AppLogger.Trace($"OnGridContextRequested handled non-fatal visual lookup: {ex.Message}");
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
        catch (Exception ex)
        {
            AppLogger.Error("Error handling DatabaseGrid double tap event.", ex);
        }
    }
}