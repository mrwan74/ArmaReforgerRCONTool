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

            DataContextChanged += (_, _) =>
            {
                if (DataContext is BansViewModel vm)
                {
                    try
                    {
                        var gridKey = vm.IsBattlEyeProtocol ? "BansGrid_BattlEye" : "BansGrid_Reforger";
                        ColumnLayoutStorageService.BindPersistence(BansGrid, gridKey);

                        UpdateColumnVisibilities(vm);
                        vm.PropertyChanged += (s, e) =>
                        {
                            if (e.PropertyName is nameof(BansViewModel.IsMultiSelectMode) or
                                                 nameof(BansViewModel.IsReforgerProtocol) or
                                                 nameof(BansViewModel.IsBattlEyeProtocol))
                            {
                                UpdateColumnVisibilities(vm);
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
            }
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"OnGridPointerPressed handled non-fatal visual lookup: {ex.Message}");
        }
    }
}