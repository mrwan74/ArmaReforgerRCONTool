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

public partial class PlayersTabView : UserControl
{
    private const string ColSelectTag = "ColSelect";
    private const string ColActionsTag = "ColActions";

    public PlayersTabView()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            InitializeComponent();

            AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnGridContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            DataContextChanged += (_, _) =>
            {
                if (DataContext is PlayersViewModel vm)
                {
                    var bindStart = Stopwatch.GetTimestamp();
                    try
                    {
                        foreach (var col in PlayersGrid.Columns)
                        {
                            if (col.Tag?.ToString() == ColSelectTag)
                            {
                                col.Header = vm;
                            }
                        }

                        var gridKey = vm.IsBattlEyeProtocol ? "PlayersGrid_BattlEye" : "PlayersGrid_Reforger";
                        ColumnLayoutStorageService.BindPersistence(PlayersGrid, gridKey);

                        UpdateColumnVisibilities();
                        UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);

                        vm.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName is nameof(PlayersViewModel.IsMultiSelectMode) or
                                                 nameof(PlayersViewModel.IsReforgerProtocol) or
                                                 nameof(PlayersViewModel.IsBattlEyeProtocol))
                            {
                                var activeKey = vm.IsBattlEyeProtocol ? "PlayersGrid_BattlEye" : "PlayersGrid_Reforger";
                                ColumnLayoutStorageService.RestoreGridState(PlayersGrid, activeKey);
                                UpdateColumnVisibilities();
                                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
                            }
                        };
                        AppLogger.Debug($"[PlayersTabView:Bind] DataContext bound in {Stopwatch.GetElapsedTime(bindStart).TotalMilliseconds:F2}ms.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Failed configuring PlayersTabView bindings.", ex);
                    }
                }
            };

            DebugLayoutLoggerService.RegisterDataGrid("PlayersGrid", PlayersGrid);
            AppLogger.Debug($"[PlayersTabView:Init] Initialized in {Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed during PlayersTabView constructor initialization.", ex);
        }
    }

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var tag = e.Column.Tag?.ToString() ?? string.Empty;
            if (tag is ColSelectTag or ColActionsTag)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            if (DataContext is PlayersViewModel vm)
            {
                vm.CycleColumnSort(tag);
                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
            }
            AppLogger.Trace($"[PlayersTabView:Sort] Handled in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[PlayersTabView:Sort] Error sorting: " + ex.Message, ex);
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
            foreach (var col in PlayersGrid.Columns)
            {
                var tag = col.Tag?.ToString() ?? string.Empty;
                if (tag is ColSelectTag or ColActionsTag) continue;

                var field = PlayersViewModel.MapColumnTagToSortField(tag);
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
            AppLogger.Trace($"[PlayersTabView:SortGlyph] Updated in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[PlayersTabView:SortGlyph] Update notice: {ex.Message}");
        }
    }

    private void UpdateColumnVisibilities()
    {
        if (DataContext is not PlayersViewModel vm) return;

        var start = Stopwatch.GetTimestamp();
        try
        {
            foreach (var col in PlayersGrid.Columns)
            {
                var tag = col.Tag?.ToString();
                if (tag == null) continue;

                switch (tag)
                {
                    case ColSelectTag:
                        col.IsVisible = vm.IsMultiSelectMode;
                        col.Header = vm;
                        break;
                    case "ColReforgerId":
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
            AppLogger.Trace($"[PlayersTabView:Visibility] Updated in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error updating PlayersGrid column visibility.", ex);
        }
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var point = e.GetCurrentPoint(this);

            if (point.Properties.IsLeftButtonPressed && e.Source is Visual leftVisual)
            {
                var header = leftVisual.FindAncestorOfType<DataGridColumnHeader>();
                if (header?.FindDescendantOfType<CheckBox>() is not null && leftVisual.FindAncestorOfType<CheckBox>() is null && DataContext is PlayersViewModel vm)
                {
                    vm.ToggleSelectAll();
                    e.Handled = true;
                    return;
                }
            }

            if (point.Properties.IsRightButtonPressed && e.Source is Visual visual)
            {
                var row = visual.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is PlayerModel player)
                {
                    PlayersGrid.SelectedItem = player;
                }
                else
                {
                    PlayersGrid.SelectedItem = null;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[PlayersTabView:Pointer] Lookup notice: {ex.Message}");
        }
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        try
        {
            if (e.Source is Visual visual)
            {
                var row = visual.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is PlayerModel player)
                {
                    PlayersGrid.SelectedItem = player;
                    return;
                }
            }

            PlayersGrid.SelectedItem = null;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[PlayersTabView:Context] Lookup notice: {ex.Message}");
            e.Handled = true;
        }
    }

    private void OnGridDoubleTapped(object? sender, TappedEventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            if (e.Source is Visual visual && (visual.FindAncestorOfType<Button>() != null || visual.FindAncestorOfType<CheckBox>() != null || visual.FindAncestorOfType<DataGridColumnHeader>() != null))
            {
                return;
            }

            if (DataContext is PlayersViewModel vm && PlayersGrid.SelectedItem is PlayerModel player)
            {
                vm.OpenPlayerDetails(player);
                e.Handled = true;
                AppLogger.Trace($"[PlayersTabView:DoubleTap] Handled in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error handling PlayersGrid double tap event.", ex);
        }
    }
}