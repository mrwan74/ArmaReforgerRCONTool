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

            DataContextChanged += (_, _) =>
            {
                if (DataContext is DatabaseViewModel vm)
                {
                    try
                    {
                        var gridKey = vm.IsBattlEyeProtocol ? "DatabaseGrid_BattlEye" : "DatabaseGrid_Reforger";
                        ColumnLayoutStorageService.BindPersistence(DatabaseGrid, gridKey);

                        UpdateColumnVisibilities(vm);
                        vm.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName is nameof(DatabaseViewModel.IsMultiSelectMode) or
                                                 nameof(DatabaseViewModel.IsReforgerProtocol) or
                                                 nameof(DatabaseViewModel.IsBattlEyeProtocol))
                            {
                                UpdateColumnVisibilities(vm);
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

    private void UpdateColumnVisibilities(DatabaseViewModel vm)
    {
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

            // Suppress context menu on empty area or headers
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