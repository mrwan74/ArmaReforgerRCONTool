using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using ReforgerRcon.ViewModels;

namespace ReforgerRcon.Views.Tabs;

public partial class BansTabView : UserControl
{
    public BansTabView()
    {
        try
        {
            InitializeComponent();
            AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnGridContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            DataContextChanged += (_, _) =>
            {
                if (DataContext is BansViewModel vm)
                {
                    try
                    {
                        var gridKey = vm.IsBattlEyeProtocol ? "BansGrid_BattlEye" : "BansGrid_Reforger";
                        ColumnLayoutStorageService.BindPersistence(BansGrid, gridKey);

                        UpdateColumnVisibilities(vm);
                        UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);

                        vm.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName is nameof(BansViewModel.IsMultiSelectMode) or
                                                 nameof(BansViewModel.IsReforgerProtocol) or
                                                 nameof(BansViewModel.IsBattlEyeProtocol))
                            {
                                UpdateColumnVisibilities(vm);
                                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Failed configuring BansTabView data context bindings.", ex);
                    }
                }
            };

            DebugLayoutLoggerService.RegisterDataGrid("BansGrid", BansGrid);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed during BansTabView constructor initialization.", ex);
        }
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        try
        {
            e.Handled = true;
            var tag = e.Column.Tag?.ToString() ?? "";
            if (DataContext is BansViewModel vm)
            {
                vm.CycleColumnSort(tag);
                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("[BansTabView] Error in OnDataGridSorting.", ex);
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
            foreach (var col in BansGrid.Columns)
            {
                var tag = col.Tag?.ToString() ?? "";
                var field = BansViewModel.MapColumnTagToSortField(tag);
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
            AppLogger.Trace($"[BansTabView] Sort glyph update notice: {ex.Message}");
        }
    }

    private void UpdateColumnVisibilities(BansViewModel vm)
    {
        try
        {
            foreach (var col in BansGrid.Columns)
            {
                var tag = col.Tag?.ToString();
                if (tag == null) continue;

                switch (tag)
                {
                    case "ColSelect":
                        col.IsVisible = vm.IsMultiSelectMode;
                        break;
                    case "ColReforgerBannedName":
                    case "ColReforgerIdentity":
                        col.IsVisible = vm.IsReforgerProtocol;
                        break;
                    case "ColBeBanNo":
                    case "ColBeIdentity":
                    case "ColBeMinutes":
                    case "ColBeReason":
                        col.IsVisible = vm.IsBattlEyeProtocol;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error updating BansGrid column visibility.", ex);
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
                if (row?.DataContext is BanModel ban)
                {
                    BansGrid.SelectedItem = ban;
                }
                else
                {
                    BansGrid.SelectedItem = null;
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
                if (row?.DataContext is BanModel ban)
                {
                    BansGrid.SelectedItem = ban;
                    return;
                }
            }

            BansGrid.SelectedItem = null;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"OnGridContextRequested handled non-fatal visual lookup: {ex.Message}");
            e.Handled = true;
        }
    }
}