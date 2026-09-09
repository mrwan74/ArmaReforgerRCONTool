using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Data.Sqlite;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Instance property for XAML data binding")]
[SuppressMessage("Minor Code Smell", "S1125:Boolean literals should not be redundant", Justification = "Nullable boolean comparison")]
[SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "Partial callback methods are invoked by CommunityToolkit.Mvvm generated property setters")]
public partial class DatabaseViewModel(IRconService rconService, DashboardViewModel dashboard) : ViewModelBase, IDisposable
{
    public const string DefaultSortKey = "Default";

    private readonly IRconService _rconService = rconService;
    private readonly DashboardViewModel _dashboard = dashboard;
    private readonly HashSet<string> _selectedUids = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loadCts;
    private bool _isUpdatingSelection;
    private string _currentQuery = string.Empty;
    private string _currentSearchType = "Name";
    private bool _isDisposed;

    [ObservableProperty] public partial ObservableCollection<DatabasePlayerModel> Players { get; set; } = [];
    [ObservableProperty] public partial DatabasePlayerModel? SelectedPlayer { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial int SelectedCount { get; set; }

    [ObservableProperty] public partial int CurrentPage { get; set; } = 1;
    [ObservableProperty] public partial int PageSize { get; set; } = 50;
    [ObservableProperty] public partial int TotalCount { get; set; }
    [ObservableProperty] public partial int TotalPages { get; set; } = 1;
    [ObservableProperty] public partial bool HasPreviousPage { get; set; }
    [ObservableProperty] public partial bool HasNextPage { get; set; }
    [ObservableProperty] public partial string PageStatusText { get; set; } = "Loading database...";
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public ObservableCollection<int> PageSizes { get; } = [25, 50, 100, 200];

    private bool? _isAllSelected = false;
    public bool? IsAllSelected
    {
        get => _isAllSelected;
        set
        {
            if (SetProperty(ref _isAllSelected, value))
            {
                OnPropertyChanged(nameof(SelectAllTooltipText));
                OnPropertyChanged(nameof(SelectAllButtonText));

                if (!_isUpdatingSelection && value.HasValue)
                {
                    ApplySelectAll(value.Value);
                }
            }
        }
    }

    public string SelectAllTooltipText => IsAllSelected is true
        ? "Click to deselect current page"
        : "Click to select all on current page (Ctrl+A)";

    public string SelectAllButtonText => IsAllSelected is true
        ? "Deselect Page"
        : "Select Page";

    [ObservableProperty] public partial string DatabaseStatsSummary { get; set; } = "Initializing database...";

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        AppLogger.Debug($"[DatabaseViewModel:MultiSelect] Multi-select mode changed: {value} (Previous selected count: {_selectedUids.Count}).");
        if (!value)
        {
            _selectedUids.Clear();
            foreach (var p in Players) p.IsSelected = false;
            UpdateSelectedState();
        }
    }

    partial void OnPageSizeChanged(int value)
    {
        if (value <= 0)
        {
            AppLogger.Warn($"[DatabaseViewModel:PageSize] Ignored non-positive page size value: {value}.");
            return;
        }

        AppLogger.Info($"[DatabaseViewModel:PageSize] PageSize changed to {value}. Resetting to page 1.");
        CurrentPage = 1;
        _ = LoadDbAsync();
    }

    [RelayCommand]
    public void ToggleSelectAll() => ExecuteSafe(() =>
    {
        if (Players.Count == 0)
        {
            AppLogger.Debug("[DatabaseViewModel:SelectAll] ToggleSelectAll ignored: current page has 0 players.");
            return;
        }

        if (!IsMultiSelectMode)
        {
            AppLogger.Debug("[DatabaseViewModel:SelectAll] Multi-select was inactive. Enabling multi-select mode.");
            IsMultiSelectMode = true;
        }

        bool targetState = IsAllSelected is not true;
        AppLogger.Info($"[DatabaseViewModel:SelectAll] ToggleSelectAll executed: targeting {targetState} for {Players.Count} players on Page {CurrentPage}.");
        ApplySelectAll(targetState);
    });

    private void ApplySelectAll(bool isSelected)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplySelectAll(isSelected));
            return;
        }

        ExecuteSafe(() =>
        {
            _isUpdatingSelection = true;
            try
            {
                int modifiedCount = 0;
                foreach (var p in Players)
                {
                    p.IsSelected = isSelected;
                    if (isSelected)
                    {
                        if (_selectedUids.Add(p.Uid)) modifiedCount++;
                    }
                    else
                    {
                        if (_selectedUids.Remove(p.Uid)) modifiedCount++;
                    }
                }
                SelectedCount = _selectedUids.Count;
                IsAllSelected = isSelected;
                AppLogger.Debug($"[DatabaseViewModel:SelectAll] Applied selection state {isSelected} to page {CurrentPage} ({modifiedCount} modified, TotalSelected={SelectedCount}).");
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        });
    }

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CurrentSortField => _dashboard.SettingsTab.Settings.DatabaseSortBy;
    public bool CurrentSortAscending => _dashboard.SettingsTab.Settings.DatabaseSortAscending;

    [RelayCommand]
    public Task<bool> LoadDbAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        var threadId = Environment.CurrentManagedThreadId;
        using var timing = AppLogger.Measure($"DatabaseViewModel.LoadDbAsync(Page={CurrentPage}, Size={PageSize}, Protocol={_rconService.CurrentProtocol})");

        AppLogger.Debug($"[DatabaseViewModel:LoadDb] Commencing load for Page {CurrentPage} (PageSize={PageSize}, Protocol={_rconService.CurrentProtocol}, Thread=T{threadId:D2})...");

        if (_loadCts != null)
        {
            try
            {
                AppLogger.Trace("[DatabaseViewModel:LoadDb] Cancelling in-flight query CTS...");
                await _loadCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException ex)
            {
                AppLogger.Trace($"[DatabaseViewModel:LoadDb] CTS already disposed during cancellation: {ex.Message}");
            }
            _loadCts.Dispose();
            _loadCts = null;
        }

        _loadCts = new CancellationTokenSource();
        var token = _loadCts.Token;

        await Dispatcher.UIThread.InvokeAsync(() => IsLoading = true, DispatcherPriority.Normal, token);

        try
        {
            var sortField = _dashboard.SettingsTab.Settings.DatabaseSortBy;
            var isAscending = _dashboard.SettingsTab.Settings.DatabaseSortAscending;

            var queryParams = new DatabaseQueryParameters(
                _rconService.CurrentProtocol,
                CurrentPage,
                PageSize,
                _currentQuery,
                _currentSearchType,
                sortField,
                isAscending);

            AppLogger.Debug($"[DatabaseViewModel:LoadDb] Dispatching database request with QueryParams: Sort='{sortField}' (Asc={isAscending}), Query='{AppLogger.SanitizeSensitiveData(_currentQuery)}', Type='{_currentSearchType}'...");

            var pagedResult = await _rconService.GetPagedDatabasePlayersAsync(queryParams, token).ConfigureAwait(false);
            var stats = await PlayerDatabaseStorageService.GetDatabaseStatisticsAsync(token).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();

            var protocolCount = IsBattlEyeProtocol ? stats.TotalBattlEyePlayers : stats.TotalReforgerPlayers;
            var protocolName = IsBattlEyeProtocol ? "BattlEye" : "Reforger";
            var summary = $"{protocolName} Records: {protocolCount:N0} | {stats.OnlinePlayers:N0} online | {stats.WatchlistedPlayers:N0} watchlisted | Size: {stats.DatabaseSizeBytes / 1024.0:F1} KB";

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                DatabaseStatsSummary = summary;
                TotalCount = pagedResult.TotalCount;
                TotalPages = Math.Max(1, (int)Math.Ceiling((double)TotalCount / PageSize));

                if (CurrentPage > TotalPages && TotalPages > 0)
                {
                    AppLogger.Warn($"[DatabaseViewModel:LoadDb] CurrentPage ({CurrentPage}) exceeded TotalPages ({TotalPages}). Clamping to {TotalPages}.");
                    CurrentPage = TotalPages;
                }

                HasPreviousPage = CurrentPage > 1;
                HasNextPage = CurrentPage < TotalPages;

                if (TotalCount == 0)
                {
                    PageStatusText = "No matching records found";
                }
                else
                {
                    int startRecord = ((CurrentPage - 1) * PageSize) + 1;
                    int endRecord = Math.Min(CurrentPage * PageSize, TotalCount);
                    PageStatusText = $"Showing {startRecord:N0}–{endRecord:N0} of {TotalCount:N0} records";
                }

                foreach (var p in Players)
                {
                    p.PropertyChanged -= OnPlayerPropertyChanged;
                }

                int matchingSelectedOnPage = 0;
                foreach (var p in pagedResult.Items)
                {
                    p.IsSelected = _selectedUids.Contains(p.Uid);
                    if (p.IsSelected) matchingSelectedOnPage++;
                    p.PropertyChanged += OnPlayerPropertyChanged;
                }

                Players = new ObservableCollection<DatabasePlayerModel>(pagedResult.Items);
                UpdateSelectedState();

                AppLogger.Trace($"[DatabaseViewModel:LoadDb] UI collection reconciled: {Players.Count} items displayed ({matchingSelectedOnPage} selected on page).");
            }, DispatcherPriority.Normal, token);

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[DatabaseViewModel:LoadDb] Paged load completed in {elapsedMs:F2}ms (Returned={pagedResult.Items.Count}, TotalCount={TotalCount}, Page={CurrentPage}/{TotalPages}, Thread=T{threadId:D2}).");
        }
        catch (OperationCanceledException)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Trace($"[DatabaseViewModel:LoadDb] Query superseded/cancelled after {elapsedMs:F2}ms for Page {CurrentPage}.");
        }
        catch (SqliteException sqlEx)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Error($"[DatabaseViewModel:LoadDb] SQLite error during LoadDbAsync after {elapsedMs:F2}ms (ErrorCode: {sqlEx.SqliteErrorCode}, ExtendedCode: {sqlEx.SqliteExtendedErrorCode}): {sqlEx.Message}", sqlEx);
            ToastNotificationService.Instance.ShowError("Database Error", $"SQLite query failed (Code: {sqlEx.SqliteErrorCode}): {sqlEx.Message}");
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Error($"[DatabaseViewModel:LoadDb] Critical error retrieving database page after {elapsedMs:F2}ms: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError("Database Error", $"Failed loading player database: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false, DispatcherPriority.Normal, CancellationToken.None);
        }
    }, "Failed to retrieve player database records from SQLite.");

    [RelayCommand]
    public void NextPage()
    {
        if (CurrentPage < TotalPages)
        {
            var previous = CurrentPage;
            CurrentPage++;
            AppLogger.Info($"[DatabaseViewModel:Paging] Navigating Next: Page {previous} -> {CurrentPage} of {TotalPages}.");
            _ = LoadDbAsync();
        }
        else
        {
            AppLogger.Debug($"[DatabaseViewModel:Paging] NextPage ignored: already on last page ({CurrentPage}/{TotalPages}).");
        }
    }

    [RelayCommand]
    public void PreviousPage()
    {
        if (CurrentPage > 1)
        {
            var previous = CurrentPage;
            CurrentPage--;
            AppLogger.Info($"[DatabaseViewModel:Paging] Navigating Previous: Page {previous} -> {CurrentPage} of {TotalPages}.");
            _ = LoadDbAsync();
        }
        else
        {
            AppLogger.Debug($"[DatabaseViewModel:Paging] PreviousPage ignored: already on first page ({CurrentPage}).");
        }
    }

    [RelayCommand]
    public void FirstPage()
    {
        if (CurrentPage != 1)
        {
            var previous = CurrentPage;
            CurrentPage = 1;
            AppLogger.Info($"[DatabaseViewModel:Paging] Navigating to First Page: Page {previous} -> 1 of {TotalPages}.");
            _ = LoadDbAsync();
        }
        else
        {
            AppLogger.Debug("[DatabaseViewModel:Paging] FirstPage ignored: already on page 1.");
        }
    }

    [RelayCommand]
    public void LastPage()
    {
        if (CurrentPage != TotalPages)
        {
            var previous = CurrentPage;
            CurrentPage = TotalPages;
            AppLogger.Info($"[DatabaseViewModel:Paging] Navigating to Last Page: Page {previous} -> {TotalPages}.");
            _ = LoadDbAsync();
        }
        else
        {
            AppLogger.Debug($"[DatabaseViewModel:Paging] LastPage ignored: already on last page ({CurrentPage}).");
        }
    }

    public static string MapColumnTagToSortField(string? tag)
    {
        return tag switch
        {
            "ColStatus" => "Status",
            "ColReforgerName" or "ColBeName" => "Name",
            "ColReforgerUid" or "ColBeGuid" => "BattlEye GUID",
            "ColBeCountry" => "Country",
            "ColBeEndpoint" => "IP:Port",
            "ColBePing" => "Ping",
            "ColComment" => "Comment",
            "ColReforgerId" or "ColBeId" => DefaultSortKey,
            _ => string.Empty
        };
    }

    public void CycleColumnSort(string columnTag)
    {
        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var mappedField = MapColumnTagToSortField(columnTag);
            if (string.IsNullOrEmpty(mappedField))
            {
                AppLogger.Warn($"[DatabaseViewModel:Sort] Unrecognized column tag '{columnTag}'. Sort cycle aborted.");
                return;
            }

            var currentField = _dashboard.SettingsTab.Settings.DatabaseSortBy;
            var currentAsc = _dashboard.SettingsTab.Settings.DatabaseSortAscending;

            if (string.Equals(currentField, mappedField, StringComparison.OrdinalIgnoreCase))
            {
                if (currentAsc)
                {
                    _dashboard.SettingsTab.Settings.DatabaseSortAscending = false;
                    AppLogger.Info($"[DatabaseViewModel:Sort] Cycled sort for '{mappedField}' (Ascending -> Descending).");
                }
                else
                {
                    _dashboard.SettingsTab.Settings.DatabaseSortBy = DefaultSortKey;
                    _dashboard.SettingsTab.Settings.DatabaseSortAscending = true;
                    AppLogger.Info($"[DatabaseViewModel:Sort] Cycled sort for '{mappedField}' (Descending -> Default raw server order).");
                }
            }
            else
            {
                _dashboard.SettingsTab.Settings.DatabaseSortBy = mappedField;
                _dashboard.SettingsTab.Settings.DatabaseSortAscending = true;
                AppLogger.Info($"[DatabaseViewModel:Sort] Switched sort column to '{mappedField}' (Ascending).");
            }

            CurrentPage = 1;
            _ = LoadDbAsync();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[DatabaseViewModel:Sort] Column sort change processed in {elapsedMs:F2}ms.");
        });
    }

    public async Task RefreshAfterOfflineBanAsync()
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info("[DatabaseViewModel:PostOfflineBan] Initiating database and bans synchronization following offline ban execution...");
        await _dashboard.BansTab.RefreshBansAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => _dashboard.ActiveBansCount = _dashboard.BansTab.Bans.Count, DispatcherPriority.Normal, CancellationToken.None);
        await LoadDbAsync().ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[DatabaseViewModel:PostOfflineBan] Full post-ban synchronization finished in {elapsedMs:F2}ms.");
    }

    public void ApplyFilter(string query, string searchType)
    {
        ExecuteSafe(() =>
        {
            var sanitizedQuery = AppLogger.SanitizeSensitiveData(query);
            AppLogger.Info($"[DatabaseViewModel:Filter] Filter applied: Query='{sanitizedQuery}', SearchType='{searchType}' (PreviousQuery='{AppLogger.SanitizeSensitiveData(_currentQuery)}', PreviousType='{_currentSearchType}'). Resetting to Page 1.");
            _currentQuery = query;
            _currentSearchType = searchType;
            CurrentPage = 1;
            _ = LoadDbAsync();
        });
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (e.PropertyName == nameof(DatabasePlayerModel.IsSelected) && sender is DatabasePlayerModel p)
        {
            if (p.IsSelected)
            {
                if (_selectedUids.Add(p.Uid))
                {
                    AppLogger.Trace($"[DatabaseViewModel:Selection] Player selected: '{p.Name}' (UID: {p.Uid}, TotalSelected: {_selectedUids.Count}).");
                }
            }
            else
            {
                if (_selectedUids.Remove(p.Uid))
                {
                    AppLogger.Trace($"[DatabaseViewModel:Selection] Player deselected: '{p.Name}' (UID: {p.Uid}, TotalSelected: {_selectedUids.Count}).");
                }
            }
            UpdateSelectedState();
        }
    }

    private void UpdateSelectedState()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateSelectedState);
            return;
        }

        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            SelectedCount = _selectedUids.Count;

            int pageSelectedCount = Players.Count(p => p.IsSelected);

            bool? newSelectionState;
            if (Players.Count == 0 || pageSelectedCount == 0)
            {
                newSelectionState = false;
            }
            else if (pageSelectedCount == Players.Count)
            {
                newSelectionState = true;
            }
            else
            {
                newSelectionState = null;
            }

            if (_isAllSelected != newSelectionState)
            {
                _isUpdatingSelection = true;
                try
                {
                    IsAllSelected = newSelectionState;
                    AppLogger.Trace($"[DatabaseViewModel:Selection] Updated IsAllSelected state: {newSelectionState?.ToString() ?? "Indeterminate"} (PageSelected={pageSelectedCount}/{Players.Count}, GlobalSelected={SelectedCount}).");
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            }
        });
    }

    [RelayCommand]
    public void OpenPlayerDetails(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null)
            {
                AppLogger.Warn("[DatabaseViewModel:Details] OpenPlayerDetails invoked with null player reference.");
                return;
            }

            AppLogger.Info($"[DatabaseViewModel:Details] Opening player details overlay for '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}, BE-GUID: {player.BattlEyeGuid}, IP:Port: {player.FormattedEndpoint}).");
            _dashboard.ShowDialog(new DatabasePlayerDetailViewModel(player, this));
        });
    }

    [RelayCommand]
    public void OpenOfflineBan(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null)
            {
                AppLogger.Warn("[DatabaseViewModel:OfflineBan] OpenOfflineBan invoked with null player reference.");
                return;
            }

            var targetId = IsBattlEyeProtocol ? player.DisplayBattlEyeGuid : player.DisplayReforgerUid;
            var endpoint = player.LastIpPort;
            AppLogger.Info($"[DatabaseViewModel:OfflineBan] Launching offline ban dialog for '{player.Name}' (TargetIdentifier='{targetId}', Endpoint='{endpoint}', Protocol={_rconService.CurrentProtocol}).");
            _dashboard.ShowDialog(new OfflineBanDialogViewModel(targetId, endpoint, _rconService, this));
        });
    }

    [RelayCommand]
    public void QuickKick(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null)
            {
                AppLogger.Warn("[DatabaseViewModel:QuickKick] QuickKick invoked with null player reference.");
                return;
            }

            AppLogger.Info($"[DatabaseViewModel:QuickKick] Prompting Quick Kick dialog for historical player '{player.Name}' (UID: {player.Uid})...");
            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Quick Kick Target",
                $"Attempt to kick active sessions matching {player.Name}?",
                "Kick",
                true,
                async () =>
                {
                    var start = Stopwatch.GetTimestamp();
                    AppLogger.Info($"[DatabaseViewModel:QuickKick] Executing quick kick resolution for '{player.Name}'...");
                    var active = (await _rconService.GetPlayersAsync(CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(p =>
                        (!string.IsNullOrEmpty(player.ReforgerUid) && p.ReforgerUid == player.ReforgerUid) ||
                        (!string.IsNullOrEmpty(player.BattlEyeGuid) && p.BattlEyeGuid == player.BattlEyeGuid) ||
                        string.Equals(p.Name, player.Name, StringComparison.OrdinalIgnoreCase));

                    if (active != null)
                    {
                        AppLogger.Info($"[DatabaseViewModel:QuickKick] Matched online player: '{active.Name}' (ID: #{active.Id}). Sending kick command...");
                        await _rconService.KickPlayerAsync(active, "Kicked from Historical Database", CancellationToken.None).ConfigureAwait(false);
                        ToastNotificationService.Instance.ShowToast("Kick Dispatched", $"Kicked {player.Name}", $"#kick {active.Id}");
                        _dashboard.PlayersTab.RemovePlayerFromList(active);
                        _ = _dashboard.PlayersTab.RefreshPlayersAsync();
                    }
                    else
                    {
                        AppLogger.Warn($"[DatabaseViewModel:QuickKick] Quick kick aborted: player '{player.Name}' has no active online session.");
                        ToastNotificationService.Instance.ShowToast("Player Offline", $"{player.Name} is not currently online.");
                    }
                    AppLogger.Debug($"[DatabaseViewModel:QuickKick] Quick kick resolution finished in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }

    [RelayCommand]
    public void OpenSetComment(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null)
            {
                AppLogger.Warn("[DatabaseViewModel:Comment] OpenSetComment invoked with null player reference.");
                return;
            }

            var targetId = IsBattlEyeProtocol ? player.BattlEyeGuid : player.ReforgerUid;
            AppLogger.Debug($"[DatabaseViewModel:Comment] Launching comment editor for '{player.Name}' (TargetID: {targetId}, Length: {player.Comment.Length}).");
            _dashboard.ShowDialog(new SetCommentDialogViewModel(player.Name, targetId, player.Comment, _rconService, _dashboard));
        });
    }

    [RelayCommand]
    public Task<bool> ToggleWatchlist(DatabasePlayerModel? player) => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        player ??= SelectedPlayer;
        if (player == null)
        {
            AppLogger.Warn("[DatabaseViewModel:Watchlist] ToggleWatchlist invoked with null player reference.");
            return;
        }

        var previousState = player.IsWatchlisted;
        player.IsWatchlisted = !previousState;

        if (_dashboard.PlayersTab.Players.FirstOrDefault(p =>
            (!string.IsNullOrEmpty(player.ReforgerUid) && p.ReforgerUid == player.ReforgerUid) ||
            (!string.IsNullOrEmpty(player.BattlEyeGuid) && p.BattlEyeGuid == player.BattlEyeGuid) ||
            string.Equals(p.Name, player.Name, StringComparison.OrdinalIgnoreCase)) is { } livePlayer)
        {
            livePlayer.IsWatchlisted = player.IsWatchlisted;
            AppLogger.Trace($"[DatabaseViewModel:Watchlist] Synced live player tab state for '{player.Name}' -> {player.IsWatchlisted}.");
        }

        var identifier = IsBattlEyeProtocol ? player.BattlEyeGuid : player.ReforgerUid;
        await PlayerDatabaseStorageService.SetWatchlistStatusAsync(identifier, player.IsWatchlisted, _rconService.CurrentProtocol).ConfigureAwait(false);

        var feedbackMessage = player.IsWatchlisted ? $"Added {player.Name} to Watchlist" : $"Removed {player.Name} from Watchlist";
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[DatabaseViewModel:Watchlist] Watchlist toggled in {elapsedMs:F2}ms for '{player.Name}' ({identifier}): {previousState} -> {player.IsWatchlisted}.");
        ToastNotificationService.Instance.ShowToast("Watchlist Updated", feedbackMessage);
    });

    private string FormatDatabasePlayerInfo(DatabasePlayerModel p)
    {
        string status;
        if (p.IsWatchlisted)
        {
            status = "Watchlisted";
        }
        else if (p.IsOnline)
        {
            status = "Online";
        }
        else
        {
            status = "Offline";
        }

        var aliasText = p.Aliases is { Count: > 0 } ? string.Join(", ", p.Aliases) : "None";

        if (IsReforgerProtocol)
        {
            return $"Status: {status}\n" +
                   $"Player Name: {p.Name}\n" +
                   $"Player UID: {p.DisplayReforgerUid}\n" +
                   $"Aliases: {aliasText}\n" +
                   $"Comment: {p.Comment}";
        }

        return $"Status: {status}\n" +
               $"[#]: {p.Id}\n" +
               $"Country: {p.Country.Name}\n" +
               $"Name: {p.Name}\n" +
               $"Aliases: {aliasText}\n" +
               $"BattlEye GUID: {p.DisplayBattlEyeGuid}\n" +
               $"IP:Port: {p.FormattedEndpoint}\n" +
               $"Ping: {p.PingDisplay}\n" +
               $"Comment: {p.Comment}";
    }

    [RelayCommand]
    public Task<bool> CopyPlayerInfoAsync(DatabasePlayerModel? player) => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        player ??= SelectedPlayer;
        if (player == null)
        {
            AppLogger.Warn("[DatabaseViewModel:Clipboard] CopyPlayerInfoAsync called with null player reference.");
            return;
        }

        var text = FormatDatabasePlayerInfo(player);
        await ClipboardService.SetTextAsync(text, CancellationToken.None).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[DatabaseViewModel:Clipboard] Copied player info in {elapsedMs:F2}ms for '{player.Name}' (UID: {player.Uid}, Length={text.Length}).");
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied info for {player.Name}");
    });

    [RelayCommand]
    private Task<bool> CopyAllInfoAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        var selected = Players.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) selected = [.. Players];

        var formattedEntries = selected.Select(FormatDatabasePlayerInfo);
        var text = string.Join("\n\n", formattedEntries);

        await ClipboardService.SetTextAsync(text, CancellationToken.None).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[DatabaseViewModel:Clipboard] Copied {selected.Count} database record(s) ({text.Length} chars) in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied player database to clipboard.");
    });

    [RelayCommand]
    public void CloseDialog() => ExecuteSafe(() =>
    {
        AppLogger.Debug("[DatabaseViewModel:Dialog] CloseDialog invoked.");
        _dashboard.CloseDialog();
    });

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            if (disposing)
            {
                try
                {
                    AppLogger.Debug("[DatabaseViewModel:Dispose] Disposing active CTS and clearing resources...");
                    _loadCts?.Cancel();
                    _loadCts?.Dispose();
                }
                catch (ObjectDisposedException ex)
                {
                    AppLogger.Trace($"[DatabaseViewModel:Dispose] CTS already disposed: {ex.Message}");
                }
            }
            _isDisposed = true;
        }
    }
}