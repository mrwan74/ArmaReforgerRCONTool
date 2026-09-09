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
    private const string ColSelectTag = "ColSelect";
    private const string ColActionsTag = "ColActions";

    public DatabaseTabView()
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        try
        {
            AppLogger.Debug($"[DatabaseTabView:Init] Beginning visual component initialization (Thread=T{threadId:D2})...");
            InitializeComponent();
            AddHandler(PointerPressedEvent, OnGridPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            AddHandler(ContextRequestedEvent, OnGridContextRequested, Avalonia.Interactivity.RoutingStrategies.Tunnel);

            Loaded += async (_, _) =>
            {
                var loadStart = Stopwatch.GetTimestamp();
                if (DataContext is DatabaseViewModel vm && vm.Players.Count == 0 && !vm.IsLoading)
                {
                    try
                    {
                        AppLogger.Debug($"[DatabaseTabView:Loaded] Tab loaded event triggered. Refreshing database page {vm.CurrentPage}...");
                        await vm.LoadDbAsync().ConfigureAwait(false);
                        var elapsed = Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
                        AppLogger.Debug($"[DatabaseTabView:Loaded] Database records load finalized in {elapsed:F2}ms (Displayed={vm.Players.Count}/{vm.TotalCount}).");
                    }
                    catch (OperationCanceledException opEx)
                    {
                        AppLogger.Debug($"[DatabaseTabView:Loaded] Load superseded/cancelled: {opEx.Message}");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"[DatabaseTabView:Loaded] Critical error during tab load: {ex.Message}", ex);
                        ToastNotificationService.Instance.ShowError("Database Load Error", $"Failed loading records on tab display: {ex.Message}");
                    }
                }
            };

            DataContextChanged += (_, _) =>
            {
                var bindStart = Stopwatch.GetTimestamp();
                if (DataContext is DatabaseViewModel vm)
                {
                    try
                    {
                        AppLogger.Debug($"[DatabaseTabView:Bind] DataContext changed to DatabaseViewModel. Protocol={GetProtocolName(vm)}");

                        foreach (var col in DatabaseGrid.Columns)
                        {
                            if (col.Tag?.ToString() == ColSelectTag)
                            {
                                col.Header = vm;
                            }
                        }

                        var gridKey = vm.IsBattlEyeProtocol ? "DatabaseGrid_BattlEye" : "DatabaseGrid_Reforger";
                        AppLogger.Debug($"[DatabaseTabView:Bind] Binding grid persistence layout for key '{gridKey}' ({DatabaseGrid.Columns.Count} columns)...");
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
                                    AppLogger.Debug($"[DatabaseTabView:PropertyChanged] Detected '{e.PropertyName}'. Refreshing column layout...");
                                    UpdateColumnVisibilities();
                                    UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
                                }
                            }
                            catch (Exception propEx)
                            {
                                AppLogger.Error($"[DatabaseTabView:PropertyChange] Error updating grid visuals on property '{e.PropertyName}': {propEx.Message}", propEx);
                                ToastNotificationService.Instance.ShowError("UI Error", $"Failed updating column layout: {propEx.Message}");
                            }
                        };

                        var elapsed = Stopwatch.GetElapsedTime(bindStart).TotalMilliseconds;
                        AppLogger.Debug($"[DatabaseTabView:Bind] DataContext configured in {elapsed:F2}ms.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error($"[DatabaseTabView:Bind] Failed configuring DatabaseTabView bindings: {ex.Message}", ex);
                        ToastNotificationService.Instance.ShowError("Binding Error", $"Database tab view setup error: {ex.Message}");
                    }
                }
            };

            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            AppLogger.Debug($"[DatabaseTabView:Init] Initialized in {elapsedMs:F2}ms (Thread=T{threadId:D2}).");
        }
        catch (Exception ex)
        {
            AppLogger.Fatal($"[DatabaseTabView:Init] Failed during DatabaseTabView component initialization: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Initialization Error", $"Database UI setup failed: {ex.Message}");
            CrashReportService.HandleFatalException("DatabaseTabView.Constructor", ex, isTerminating: false);
        }
    }

    private static string GetProtocolName(DatabaseViewModel vm) => vm.IsBattlEyeProtocol ? "BattlEye" : "Reforger";

    private void OnDataGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        var start = Stopwatch.GetTimestamp();
        var tag = e.Column.Tag?.ToString() ?? string.Empty;

        try
        {
            if (tag is ColSelectTag or ColActionsTag)
            {
                AppLogger.Trace($"[DatabaseTabView:Sort] Sorting bypassed for system column '{tag}'.");
                e.Handled = true;
                return;
            }

            e.Handled = true;
            if (DataContext is DatabaseViewModel vm)
            {
                AppLogger.Info($"[DatabaseTabView:Sort] User clicked column header '{tag}' (CurrentSort='{vm.CurrentSortField}', Asc={vm.CurrentSortAscending}).");
                vm.CycleColumnSort(tag);
                UpdateColumnSortGlyphs(vm.CurrentSortField, vm.CurrentSortAscending);
            }
            else
            {
                AppLogger.Warn("[DatabaseTabView:Sort] Sorting ignored: DataContext is null or not DatabaseViewModel.");
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Trace($"[DatabaseTabView:Sort] Column header sort handled in {elapsedMs:F2}ms.");
        }
        catch (ArgumentException argEx)
        {
            AppLogger.Warn($"[DatabaseTabView:Sort] Invalid sorting parameter on '{tag}': {argEx.Message}", argEx);
            ToastNotificationService.Instance.ShowWarning("Sorting Notice", $"Invalid sort field: {argEx.Message}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DatabaseTabView:Sort] Error during column sorting for '{tag}': {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Sort Error", $"Failed to sort database column: {ex.Message}");
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
            int updatedCount = 0;
            foreach (var col in DatabaseGrid.Columns)
            {
                var tag = col.Tag?.ToString() ?? string.Empty;
                if (tag is ColSelectTag or ColActionsTag) continue;

                var field = DatabaseViewModel.MapColumnTagToSortField(tag);
                var cleanHeader = GetCleanHeader(col);

                if (string.IsNullOrEmpty(cleanHeader)) continue;

                if (!string.IsNullOrEmpty(field) &&
                    string.Equals(field, sortField, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(sortField, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    col.Header = isAscending ? $"{cleanHeader} ▲" : $"{cleanHeader} ▼";
                    updatedCount++;
                }
                else
                {
                    col.Header = cleanHeader;
                }
            }
            AppLogger.Trace($"[DatabaseTabView:SortGlyph] Updated sort glyphs in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms ({updatedCount} column decorated for '{sortField}').");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DatabaseTabView:SortGlyph] Error updating sort glyphs for '{sortField}': {ex.Message}", ex);
        }
    }

    private void UpdateColumnVisibilities()
    {
        if (DataContext is not DatabaseViewModel vm)
        {
            AppLogger.Warn("[DatabaseTabView:Visibility] UpdateColumnVisibilities aborted: DataContext is not DatabaseViewModel.");
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            int visibleCount = 0;
            foreach (var col in DatabaseGrid.Columns)
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

                if (col.IsVisible) visibleCount++;
            }
            AppLogger.Trace($"[DatabaseTabView:Visibility] Configured {visibleCount}/{DatabaseGrid.Columns.Count} visible columns in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms (MultiSelect={vm.IsMultiSelectMode}, Protocol={GetProtocolName(vm)}).");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DatabaseTabView:Visibility] Error updating column visibility: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Layout Error", $"Failed updating column visibility: {ex.Message}");
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
                if (header?.FindDescendantOfType<CheckBox>() is not null && leftVisual.FindAncestorOfType<CheckBox>() is null && DataContext is DatabaseViewModel vm)
                {
                    AppLogger.Debug("[DatabaseTabView:Pointer] Left-click on column header checkbox detected. Toggling select-all on page...");
                    vm.ToggleSelectAll();
                    e.Handled = true;
                    return;
                }
            }

            if (point.Properties.IsRightButtonPressed && e.Source is Visual visual)
            {
                var row = visual.FindAncestorOfType<DataGridRow>();
                if (row?.DataContext is DatabasePlayerModel player)
                {
                    AppLogger.Trace($"[DatabaseTabView:Pointer] Right-click selection targeting player '{player.Name}' (UID: {player.Uid}).");
                    DatabaseGrid.SelectedItem = player;
                }
                else
                {
                    AppLogger.Trace("[DatabaseTabView:Pointer] Right-click outside of valid DataGridRow. Selection cleared.");
                    DatabaseGrid.SelectedItem = null;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DatabaseTabView:Pointer] Pointer lookup error: {ex.Message}", ex);
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
                    AppLogger.Trace($"[DatabaseTabView:Context] Context menu requested for '{player.Name}' (UID: {player.Uid}).");
                    DatabaseGrid.SelectedItem = player;
                    return;
                }
            }

            AppLogger.Trace("[DatabaseTabView:Context] Context menu requested without target item. Setting SelectedItem to null.");
            DatabaseGrid.SelectedItem = null;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DatabaseTabView:Context] Context menu lookup failure: {ex.Message}", ex);
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
                AppLogger.Trace("[DatabaseTabView:DoubleTap] Double tap bypassed: source is an interactive control (Button/CheckBox/Header).");
                return;
            }

            if (DataContext is DatabaseViewModel vm && DatabaseGrid.SelectedItem is DatabasePlayerModel player)
            {
                AppLogger.Info($"[DatabaseTabView:DoubleTap] Double-click on player row: opening details for '{player.Name}' (UID: {player.Uid}).");
                vm.OpenPlayerDetails(player);
                e.Handled = true;
                AppLogger.Trace($"[DatabaseTabView:DoubleTap] Handled in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            }
            else
            {
                AppLogger.Debug("[DatabaseTabView:DoubleTap] Double tap ignored: SelectedItem is null or DataContext invalid.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DatabaseTabView:DoubleTap] Error handling double tap: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Dialog Error", $"Failed opening player details: {ex.Message}");
        }
    }
}