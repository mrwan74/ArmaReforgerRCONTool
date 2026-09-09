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

public partial class BansTabView : UserControl
{
    private const string ColSelectTag = "ColSelect";
    private const string ColActionsTag = "ColActions";

    public BansTabView()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            InitializeComponent();
            AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnGridContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            DataContextChanged += (_, _) =>
            {
                if (DataContext is BansViewModel vm)
                {
                    var bindStart = Stopwatch.GetTimestamp();
                    try
                    {
                        foreach (var col in BansGrid.Columns)
                        {
                            if (col.Tag?.ToString() == ColSelectTag)
                            {
                                col.Header = vm;
                            }
                        }

                        var gridKey = vm.IsBattlEyeProtocol ? "BansGrid_BattlEye" : "BansGrid_Reforger";
                        AppLogger.Debug($"[BansTabView:Bind] Binding persistence for DataGrid with key: '{gridKey}'...");
                        ColumnLayoutStorageService.BindPersistence(BansGrid, gridKey);

                        UpdateColumnVisibilities(vm);
                        UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);

                        vm.PropertyChanged += (s, e) =>
                        {
                            try
                            {
                                if (e.PropertyName is nameof(BansViewModel.IsMultiSelectMode) or
                                                     nameof(BansViewModel.IsReforgerProtocol) or
                                                     nameof(BansViewModel.IsBattlEyeProtocol))
                                {
                                    AppLogger.Debug($"[BansTabView:PropertyChange] Updating visual state due to '{e.PropertyName}' change.");
                                    UpdateColumnVisibilities(vm);
                                    UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
                                }
                            }
                            catch (Exception propEx)
                            {
                                AppLogger.Error("[BansTabView:PropertyChange] Error handling property change: " + propEx.Message, propEx);
                            }
                        };
                        AppLogger.Debug($"[BansTabView:Bind] DataContext bound in {Stopwatch.GetElapsedTime(bindStart).TotalMilliseconds:F2}ms.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error("[BansTabView:Bind] Failed configuring BansTabView bindings: " + ex.Message, ex);
                        ToastNotificationService.Instance.ShowError("UI Binding Error", "Failed initializing Bans tab bindings.");
                    }
                }
            };

            AppLogger.Debug($"[BansTabView:Init] Initialized in {Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Fatal("[BansTabView:Init] Failed during BansTabView initialization.", ex);
            CrashReportService.HandleFatalException("BansTabView.Constructor", ex, isTerminating: false);
            throw;
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
            if (DataContext is BansViewModel vm)
            {
                AppLogger.Debug($"[BansTabView:Sort] User clicked column header '{tag}'. Cycling column sort...");
                vm.CycleColumnSort(tag);
                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
            }
            AppLogger.Trace($"[BansTabView:Sort] Handled in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[BansTabView:Sort] Error in sorting: " + ex.Message, ex);
            ToastNotificationService.Instance.ShowWarning("Sort Error", "Unable to sort bans column: " + ex.Message);
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
            foreach (var col in BansGrid.Columns)
            {
                var tag = col.Tag?.ToString() ?? string.Empty;
                if (tag is ColSelectTag or ColActionsTag) continue;

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
            AppLogger.Trace($"[BansTabView:SortGlyph] Updated in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[BansTabView:SortGlyph] Update notice: {ex.Message}");
        }
    }

    private void UpdateColumnVisibilities(BansViewModel vm)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            int updatedCount = 0;
            foreach (var col in BansGrid.Columns)
            {
                var tag = col.Tag?.ToString();
                if (tag == null) continue;

                switch (tag)
                {
                    case ColSelectTag:
                        col.IsVisible = vm.IsMultiSelectMode;
                        col.Header = vm;
                        updatedCount++;
                        break;
                    case "ColReforgerBannedName":
                    case "ColReforgerIdentity":
                        col.IsVisible = vm.IsReforgerProtocol;
                        updatedCount++;
                        break;
                    case "ColBeBanNo":
                    case "ColBeIdentity":
                    case "ColBeMinutes":
                    case "ColBeReason":
                        col.IsVisible = vm.IsBattlEyeProtocol;
                        updatedCount++;
                        break;
                }
            }
            AppLogger.Trace($"[BansTabView:Visibility] Evaluated {updatedCount} columns in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms (MultiSelect={vm.IsMultiSelectMode}, Protocol={vm.IsBattlEyeProtocol}).");
        }
        catch (Exception ex)
        {
            AppLogger.Error("[BansTabView:Visibility] Error updating BansGrid column visibility: " + ex.Message, ex);
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
                if (header?.FindDescendantOfType<CheckBox>() is not null && leftVisual.FindAncestorOfType<CheckBox>() is null && DataContext is BansViewModel vm)
                {
                    AppLogger.Debug("[BansTabView:Pointer] Left-click on column header checkbox detected. Toggling select-all...");
                    vm.ToggleSelectAll();
                    e.Handled = true;
                    return;
                }
            }

            if (point.Properties.IsRightButtonPressed && e.Source is Visual visual)
            {
                var row = visual.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is BanModel ban)
                {
                    AppLogger.Trace($"[BansTabView:Pointer] Right-click selection targeting Ban #{ban.BanNumber} ({ban.IdentityId}).");
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
            AppLogger.Trace($"[BansTabView:Pointer] Pointer lookup notice: {ex.Message}");
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
                    AppLogger.Trace($"[BansTabView:Context] Context requested for Ban #{ban.BanNumber}.");
                    BansGrid.SelectedItem = ban;
                    return;
                }
            }

            BansGrid.SelectedItem = null;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Trace($"[BansTabView:Context] Context lookup notice: {ex.Message}");
            e.Handled = true;
        }
    }
}