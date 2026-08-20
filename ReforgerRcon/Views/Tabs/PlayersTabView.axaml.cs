using System;
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
    private bool _isSyncingHeaderCheck;

    public PlayersTabView()
    {
        try
        {
            InitializeComponent();
            AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            // Direct event wiring for the Select-All Header CheckBox
            if (this.FindControl<CheckBox>("SelectAllCheckBox") is { } selectAllBox)
            {
                selectAllBox.IsCheckedChanged += (_, _) =>
                {
                    if (_isSyncingHeaderCheck) return;
                    if (DataContext is PlayersViewModel vm)
                    {
                        vm.IsAllSelected = selectAllBox.IsChecked == true;
                    }
                };
            }

            // Direct event wiring for the Select-All Header Border (click anywhere on header cell)
            if (this.FindControl<Border>("SelectAllHeaderBorder") is { } selectAllBorder)
            {
                selectAllBorder.PointerPressed += (_, e) =>
                {
                    if (DataContext is PlayersViewModel vm)
                    {
                        vm.IsAllSelected = !vm.IsAllSelected;
                        e.Handled = true;
                    }
                };
            }

            DataContextChanged += (_, _) =>
            {
                if (DataContext is PlayersViewModel vm)
                {
                    try
                    {
                        var gridKey = vm.IsBattlEyeProtocol ? "PlayersGrid_BattlEye" : "PlayersGrid_Reforger";
                        ColumnLayoutStorageService.BindPersistence(PlayersGrid, gridKey);

                        UpdateColumnVisibilities(vm);

                        // Keep the header checkbox synchronized when ViewModel selection changes
                        vm.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName == nameof(PlayersViewModel.IsAllSelected))
                            {
                                if (this.FindControl<CheckBox>("SelectAllCheckBox") is { } box)
                                {
                                    _isSyncingHeaderCheck = true;
                                    box.IsChecked = vm.IsAllSelected;
                                    _isSyncingHeaderCheck = false;
                                }
                            }
                            else if (e.PropertyName is nameof(PlayersViewModel.IsMultiSelectMode) or
                                                     nameof(PlayersViewModel.IsReforgerProtocol) or
                                                     nameof(PlayersViewModel.IsBattlEyeProtocol))
                            {
                                UpdateColumnVisibilities(vm);
                            }
                        };
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("Failed configuring PlayersTabView data context bindings.", ex);
                    }
                }
            };

            DebugLayoutLoggerService.RegisterDataGrid("PlayersGrid", PlayersGrid);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed during PlayersTabView constructor initialization.", ex);
        }
    }

    private void UpdateColumnVisibilities(PlayersViewModel vm)
    {
        try
        {
            foreach (var col in PlayersGrid.Columns)
            {
                var tag = col.Tag?.ToString();
                if (tag == null) continue;

                switch (tag)
                {
                    case "ColSelect":
                        col.IsVisible = vm.IsMultiSelectMode;
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

            // Right-click row selection: only select if clicking a real DataGridRow
            if (point.Properties.IsRightButtonPressed && e.Source is Visual visual)
            {
                var row = visual.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is PlayerModel player)
                {
                    PlayersGrid.SelectedItem = player;
                }
                else
                {
                    // Prevent selection or context menu on empty space
                    PlayersGrid.SelectedItem = null;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"OnGridPointerPressed handled non-fatal visual lookup: {ex.Message}");
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

            if (DataContext is PlayersViewModel vm && PlayersGrid.SelectedItem is PlayerModel player)
            {
                vm.OpenPlayerDetails(player);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("Error handling PlayersGrid double tap event.", ex);
        }
    }
}