using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using Sentry;

namespace ReforgerRcon.ViewModels;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Instance property for XAML data binding")]
[SuppressMessage("Minor Code Smell", "S1125:Boolean literals should not be redundant", Justification = "Nullable boolean comparison")]
public partial class PlayersViewModel(IRconService rconService, DashboardViewModel dashboard) : ViewModelBase
{
    public const string DefaultSortKey = "Default";
    private const string DialogOpenedEvent = "dialog_opened";
    private const string DialogKey = "dialog";

    private readonly IRconService _rconService = rconService;
    private readonly DashboardViewModel _dashboard = dashboard;
    private List<PlayerModel> _allPlayers = [];
    private bool _isUpdatingSelection;

    [ObservableProperty] public partial ObservableCollection<PlayerModel> Players { get; set; } = [];
    [ObservableProperty] public partial PlayerModel? SelectedPlayer { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial int SelectedCount { get; set; }

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

                // Propagate selection to all players when toggled by user
                if (!_isUpdatingSelection && value.HasValue)
                {
                    ApplySelectAll(value.Value);
                }
            }
        }
    }

    public string SelectAllTooltipText => IsAllSelected is true
        ? "Click to deselect all"
        : "Click to select all (Ctrl+A)";

    public string SelectAllButtonText => IsAllSelected is true
        ? "Deselect All"
        : "Select All";

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        if (!value)
        {
            foreach (var p in Players) p.IsSelected = false;
            UpdateSelectedCount();
        }
    }

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CurrentSortField => _dashboard.SettingsTab.Settings.PlayersSortBy;
    public bool CurrentSortAscending => _dashboard.SettingsTab.Settings.PlayersSortAscending;

    [RelayCommand]
    public Task<bool> RefreshPlayersAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        using var timing = AppLogger.Measure("PlayersViewModel.RefreshPlayersAsync");
        AppLogger.Debug($"[PlayersViewModel:Refresh] Querying live player list ({_rconService.CurrentProtocol})...");

        // Preserve current selections across auto-refresh
        var selectedUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedIds = new HashSet<int>();
        bool wasAllSelected = IsAllSelected is true;

        foreach (var p in _allPlayers)
        {
            if (p.IsSelected)
            {
                selectedIds.Add(p.Id);
                var uid = !string.IsNullOrWhiteSpace(p.Guid) && !p.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase)
                    ? p.Guid
                    : p.Uid;
                if (!string.IsNullOrWhiteSpace(uid)) selectedUids.Add(uid);
            }
        }

        _allPlayers = await _rconService.GetPlayersAsync().ConfigureAwait(false);

        // Auto-resolve GeoIP and restore selections
        foreach (var p in _allPlayers)
        {
            if ((p.Country == null || p.Country.Code == "xx") &&
                !string.IsNullOrWhiteSpace(p.Ip) &&
                !p.Ip.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                var resolved = GeoIpService.GetLocation(p.Ip);
                if (resolved.CountryCode != "xx")
                {
                    p.Country = new CountryInfo { Code = resolved.CountryCode, Name = resolved.CountryName };
                    p.DisplayLocation = resolved.NaturalLocation;
                    p.TimeZone = resolved.TimeZone;
                    p.LocationCity = resolved.CityName;
                    p.LocationState = resolved.SubdivisionName;
                }
            }

            var uid = !string.IsNullOrWhiteSpace(p.Guid) && !p.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase)
                ? p.Guid
                : p.Uid;

            if (wasAllSelected || selectedIds.Contains(p.Id) || (!string.IsNullOrEmpty(uid) && selectedUids.Contains(uid)))
            {
                p.IsSelected = true;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard.OnlinePlayersCount = Players.Count;
            UpdateSelectedCount();
        });

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[PlayersViewModel:Refresh] Refreshed {_allPlayers.Count} players ({Players.Count} visible, {SelectedCount} selected) in {elapsedMs:F2}ms.");
    });

    public static string MapColumnTagToSortField(string? tag)
    {
        return tag switch
        {
            "ColStatus" => "Status",
            "ColReforgerId" or "ColBeId" => DefaultSortKey,
            "ColReforgerName" or "ColBeName" => "Name",
            "ColReforgerUid" or "ColBeGuid" => "BattlEye GUID",
            "ColBeCountry" => "Country",
            "ColBeEndpoint" => "IP:Port",
            "ColBePing" => "Ping",
            "ColComment" => "Comment",
            _ => string.Empty
        };
    }

    public void CycleColumnSort(string columnTag)
    {
        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var mappedField = MapColumnTagToSortField(columnTag);
            if (string.IsNullOrEmpty(mappedField)) return;

            var currentField = _dashboard.SettingsTab.Settings.PlayersSortBy;
            var currentAsc = _dashboard.SettingsTab.Settings.PlayersSortAscending;

            if (string.Equals(currentField, mappedField, StringComparison.OrdinalIgnoreCase))
            {
                if (currentAsc)
                {
                    _dashboard.SettingsTab.Settings.PlayersSortAscending = false;
                    AppLogger.Info($"[PlayersViewModel:Sort] Cycled sort '{mappedField}' -> Descending.");
                }
                else
                {
                    _dashboard.SettingsTab.Settings.PlayersSortBy = DefaultSortKey;
                    _dashboard.SettingsTab.Settings.PlayersSortAscending = true;
                    AppLogger.Info($"[PlayersViewModel:Sort] Cycled sort '{mappedField}' -> Default.");
                }
            }
            else
            {
                _dashboard.SettingsTab.Settings.PlayersSortBy = mappedField;
                _dashboard.SettingsTab.Settings.PlayersSortAscending = true;
                AppLogger.Info($"[PlayersViewModel:Sort] Cycled sort column -> '{mappedField}' (Ascending).");
            }

            ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            AppLogger.Debug($"[PlayersViewModel:Sort] Sort cycling finished in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
        });
    }

    public void AddOrUpdatePlayer(PlayerModel player)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => AddOrUpdatePlayer(player));
            return;
        }

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var existing = _allPlayers.FirstOrDefault(p => RconService.IsSamePlayer(p, player));

            if (existing != null)
            {
                existing.Name = player.Name;
                if (!string.IsNullOrEmpty(player.Ip) && !player.Ip.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) existing.Ip = player.Ip;
                if (player.Port > 0) existing.Port = player.Port;
                if (player.Country != null && player.Country.Code != "xx")
                {
                    existing.Country = player.Country;
                }
                else if (existing.Country == null || existing.Country.Code == "xx")
                {
                    var resolved = GeoIpService.GetLocation(existing.Ip);
                    if (resolved.CountryCode != "xx")
                    {
                        existing.Country = new CountryInfo { Code = resolved.CountryCode, Name = resolved.CountryName };
                    }
                }

                if (!string.IsNullOrEmpty(player.DisplayLocation) && !player.DisplayLocation.Equals("Unknown Region", StringComparison.OrdinalIgnoreCase)) existing.DisplayLocation = player.DisplayLocation;
                if (!string.IsNullOrEmpty(player.TimeZone)) existing.TimeZone = player.TimeZone;
                if (!string.IsNullOrEmpty(player.Guid) && !player.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase)) existing.Guid = player.Guid;
                if (!string.IsNullOrEmpty(player.Uid) && !player.Uid.StartsWith("init", StringComparison.OrdinalIgnoreCase)) existing.Uid = player.Uid;
                if (player.Ping > 0) existing.Ping = player.Ping;
            }
            else
            {
                _allPlayers.Add(player);
            }

            ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
            _dashboard.OnlinePlayersCount = Players.Count;
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Debug($"[PlayersViewModel:PlayerUpdate] Added/Updated '{player.Name}' in {elapsedMs:F2}ms (Total={_allPlayers.Count}).");
        });
    }

    public void RemovePlayerFromList(PlayerModel player)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RemovePlayerFromList(player));
            return;
        }

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            var purged = _allPlayers.RemoveAll(p => RconService.IsSamePlayer(p, player));

            var match = Players.FirstOrDefault(p => RconService.IsSamePlayer(p, player));
            if (match is not null)
            {
                match.PropertyChanged -= OnPlayerPropertyChanged;
                Players.Remove(match);
            }

            _dashboard.OnlinePlayersCount = Players.Count;
            UpdateSelectedCount();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[PlayersViewModel:PlayerRemove] Removed '{player.Name}' (ID: #{player.Id}) in {elapsedMs:F2}ms (Purged={purged}, Remaining={Players.Count}).");
        });
    }

    public async Task TriggerPostBanRefreshAsync()
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info("[PlayersViewModel:PostBan] Triggering post-ban refresh...");
        await RefreshPlayersAsync().ConfigureAwait(false);
        await _dashboard.BansTab.RefreshBansAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => _dashboard.ActiveBansCount = _dashboard.BansTab.Bans.Count);
        AppLogger.Debug($"[PlayersViewModel:PostBan] Post-ban refresh complete in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
    }

    private static int GetPlayerStatusWeight(PlayerModel p)
    {
        if (p.IsWatchlisted) return 2;
        if (p.HasAliases) return 1;
        return 0;
    }

    public void ApplyFilter(string query, string searchType)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyFilter(query, searchType));
            return;
        }

        ExecuteSafe(() =>
        {
            var start = Stopwatch.GetTimestamp();
            using var timing = AppLogger.Measure($"PlayersViewModel.ApplyFilter('{query}', '{searchType}')");

            foreach (var p in Players)
            {
                p.PropertyChanged -= OnPlayerPropertyChanged;
            }

            IEnumerable<PlayerModel> filtered = _allPlayers;

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = searchType switch
                {
                    "Player #" => _allPlayers.Where(p => p.Id.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "UID" => _allPlayers.Where(p => p.Uid.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Guid.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "Comment" => _allPlayers.Where(p => p.Comment.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "Name" => _allPlayers.Where(p => FuzzyMatch(p.Name, query)),
                    _ => _allPlayers.Where(p =>
                        FuzzyMatch(p.Name, query) ||
                        p.Uid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Guid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Comment.Contains(query, StringComparison.OrdinalIgnoreCase))
                };
            }

            var sortField = _dashboard.SettingsTab.Settings.PlayersSortBy;
            var isAscending = _dashboard.SettingsTab.Settings.PlayersSortAscending;

            if (!string.Equals(sortField, DefaultSortKey, StringComparison.OrdinalIgnoreCase))
            {
                filtered = sortField switch
                {
                    "Status" => isAscending
                        ? filtered.OrderBy(GetPlayerStatusWeight)
                        : filtered.OrderByDescending(GetPlayerStatusWeight),
                    "Country" => isAscending
                        ? filtered.OrderBy(p => p.Country.Name)
                        : filtered.OrderByDescending(p => p.Country.Name),
                    "Name" => isAscending
                        ? filtered.OrderBy(p => p.Name)
                        : filtered.OrderByDescending(p => p.Name),
                    "BattlEye GUID" => isAscending
                        ? filtered.OrderBy(p => p.Guid)
                        : filtered.OrderByDescending(p => p.Guid),
                    "IP:Port" => isAscending
                        ? filtered.OrderBy(p => p.Ip).ThenBy(p => p.Port)
                        : filtered.OrderByDescending(p => p.Ip).ThenByDescending(p => p.Port),
                    "Ping" => isAscending
                        ? filtered.OrderBy(p => p.Ping)
                        : filtered.OrderByDescending(p => p.Ping),
                    "Comment" => isAscending
                        ? filtered.OrderBy(p => p.Comment)
                        : filtered.OrderByDescending(p => p.Comment),
                    _ => filtered
                };
            }

            Players = new ObservableCollection<PlayerModel>(filtered);

            foreach (var p in Players)
            {
                p.PropertyChanged += OnPlayerPropertyChanged;
            }

            UpdateSelectedCount();
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Trace($"[PlayersViewModel:Filter] Filtered {Players.Count}/{_allPlayers.Count} players in {elapsedMs:F2}ms (Query='{query}', Sort='{sortField}', Asc={isAscending}).");
        });
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (e.PropertyName == nameof(PlayerModel.IsSelected))
        {
            UpdateSelectedCount();
        }
    }

    private static bool FuzzyMatch(string source, string target)
    {
        if (string.IsNullOrEmpty(target)) return true;
        if (string.IsNullOrEmpty(source)) return false;
        return source.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    public void ToggleMultiSelect(PlayerModel? initialPlayer = null)
    {
        ExecuteSafe(() =>
        {
            IsMultiSelectMode = !IsMultiSelectMode;
            AppLogger.Debug($"[PlayersViewModel:MultiSelect] Multi-select toggled: {IsMultiSelectMode}");

            if (IsMultiSelectMode && initialPlayer != null)
            {
                initialPlayer.IsSelected = true;
            }
            else if (!IsMultiSelectMode)
            {
                foreach (var p in Players) p.IsSelected = false;
            }
            UpdateSelectedCount();
        });
    }

    [RelayCommand]
    public void ToggleSelectAll() => ExecuteSafe(() =>
    {
        if (Players.Count == 0) return;
        if (!IsMultiSelectMode) IsMultiSelectMode = true;

        bool targetState = IsAllSelected is not true;
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
                foreach (var p in Players)
                {
                    p.IsSelected = isSelected;
                }
                SelectedCount = isSelected ? Players.Count : 0;
                IsAllSelected = isSelected;
                AppLogger.Debug($"[PlayersViewModel:SelectAll] Toggled select-all: {isSelected} ({SelectedCount} selected).");
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        });
    }

    [RelayCommand]
    public void UpdateSelectedCount()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateSelectedCount);
            return;
        }

        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            SelectedCount = Players.Count(p => p.IsSelected);

            bool? newSelectionState;
            if (Players.Count == 0 || SelectedCount == 0)
            {
                newSelectionState = false;
            }
            else if (SelectedCount == Players.Count)
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
                }
                finally
                {
                    _isUpdatingSelection = false;
                }
            }
        });
    }

    [RelayCommand]
    public void OpenPlayerDetails(PlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null)
            {
                AppLogger.Warn("[PlayersViewModel:Details] OpenPlayerDetails called with null target.");
                return;
            }
            AppLogger.Info($"[PlayersViewModel:Details] Opening details for '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}).");
            AppLogger.TrackEvent(DialogOpenedEvent, new Dictionary<string, object> { [DialogKey] = "PlayerDetailDialog" });
            _dashboard.ShowDialog(new PlayerDetailViewModel(player, _rconService, this));
        });
    }

    [RelayCommand]
    public void OpenKickDialog(PlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;
            AppLogger.Info($"[PlayersViewModel:Kick] Opening kick dialog for '{player.Name}' (ID: #{player.Id}).");
            AppLogger.TrackEvent(DialogOpenedEvent, new Dictionary<string, object> { [DialogKey] = "KickDialog" });
            _dashboard.ShowDialog(new KickDialogViewModel([player], _rconService, this));
        });
    }

    [RelayCommand]
    public void OpenBanDialog(PlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;
            AppLogger.Info($"[PlayersViewModel:Ban] Opening ban dialog for '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}).");
            AppLogger.TrackEvent(DialogOpenedEvent, new Dictionary<string, object> { [DialogKey] = "BanDialog" });
            _dashboard.ShowDialog(new BanDialogViewModel([player], _rconService, this));
        });
    }

    [RelayCommand]
    public void QuickPermanentBan(PlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;

            AppLogger.Info($"[PlayersViewModel:QuickBan] Prompting quick permanent ban for '{player.Name}' (ID: #{player.Id}).");
            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Quick Permanent Ban",
                $"Are you sure you want to PERMANENTLY ban {player.Name} (Player #{player.Id})?",
                "Permanent Ban",
                true,
                async () =>
                {
                    var start = Stopwatch.GetTimestamp();
                    AppLogger.Info($"[PlayersViewModel:QuickBan] Executing ban for '{player.Name}'...");
                    bool isSuccess = await _rconService.BanPlayerAsync(player, 0, "Quick Permanent Ban by Administrator").ConfigureAwait(false);
                    var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                        ? $"#ban create {player.Id} 0 Quick Ban"
                        : $"addBan {player.Guid} 0 Quick Ban";

                    if (isSuccess)
                    {
                        ToastNotificationService.Instance.ShowSuccess("Permanent Ban", $"Banned {player.Name}", cmd, async () =>
                        {
                            AppLogger.Info($"[PlayersViewModel:QuickBan] Undo triggered for '{player.Name}'. Reinstating unban...");
                            var allBans = await _rconService.GetBansAsync().ConfigureAwait(false);
                            var ban = allBans.FirstOrDefault(b => b.IdentityId == player.Uid || b.IdentityId == player.Guid);
                            if (ban != null)
                            {
                                await _rconService.RemoveBanAsync(ban).ConfigureAwait(false);
                                await TriggerPostBanRefreshAsync().ConfigureAwait(false);
                            }
                        });

                        RemovePlayerFromList(player);
                        await TriggerPostBanRefreshAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        ToastNotificationService.Instance.ShowError("Permanent Ban Failed", $"Could not ban {player.Name}.", cmd);
                    }
                    AppLogger.Debug($"[PlayersViewModel:QuickBan] Finished in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
                },
                () => _dashboard.CloseDialog()
            ));
        });
    }

    [RelayCommand]
    public void OpenSetComment(PlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;
            AppLogger.Debug($"[PlayersViewModel:Comment] Opening comment editor for '{player.Name}'.");
            AppLogger.TrackEvent(DialogOpenedEvent, new Dictionary<string, object> { [DialogKey] = "SetCommentDialog" });
            _dashboard.ShowDialog(new SetCommentDialogViewModel(player.Name, player.Uid, player.Comment, _rconService, _dashboard));
        });
    }

    [RelayCommand]
    public Task<bool> ToggleWatchlist(PlayerModel? player) => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        player ??= SelectedPlayer;
        if (player == null) return;
        player.IsWatchlisted = !player.IsWatchlisted;

        if (_dashboard.DatabaseTab.Players.FirstOrDefault(p => p.Uid == player.Uid) is { } dbPlayer)
        {
            dbPlayer.IsWatchlisted = player.IsWatchlisted;
        }

        string identifier;
        if (IsBattlEyeProtocol)
        {
            if (string.IsNullOrWhiteSpace(player.Guid) || player.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase))
            {
                identifier = player.Uid;
            }
            else
            {
                identifier = player.Guid;
            }
        }
        else
        {
            identifier = player.Uid;
        }

        await PlayerDatabaseStorageService.SetWatchlistStatusAsync(identifier, player.IsWatchlisted, _rconService.CurrentProtocol).ConfigureAwait(false);
        var feedbackMessage = player.IsWatchlisted ? $"Added {player.Name} to Watchlist" : $"Removed {player.Name} from Watchlist";
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[PlayersViewModel:Watchlist] Toggled watchlist in {elapsedMs:F2}ms for '{player.Name}' ({identifier}) -> {player.IsWatchlisted}");
        ToastNotificationService.Instance.ShowToast("Watchlist Updated", feedbackMessage);
    });

    private string FormatPlayerInfo(PlayerModel p)
    {
        var status = p.IsWatchlisted ? "Watchlisted" : "Online";

        if (IsReforgerProtocol)
        {
            return $"Status: {status}\n" +
                   $"Player#: {p.Id}\n" +
                   $"Player Name: {p.Name}\n" +
                   $"Player UID: {p.Uid}\n" +
                   $"Comment: {p.Comment}";
        }

        return $"Status: {status}\n" +
               $"[#]: {p.Id}\n" +
               $"Country: {p.Country.Name}\n" +
               $"Name: {p.Name}\n" +
               $"BattlEye GUID: {p.Guid}\n" +
               $"IP:Port: {p.FormattedEndpoint}\n" +
               $"Ping: {p.Ping} ms\n" +
               $"Comment: {p.Comment}";
    }

    [RelayCommand]
    public Task<bool> CopyPlayerInfoAsync(PlayerModel? player) => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        player ??= SelectedPlayer;
        if (player == null) return;
        var text = FormatPlayerInfo(player);
        await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[PlayersViewModel:Clipboard] Copied player info in {elapsedMs:F2}ms for '{player.Name}'.");
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied info for {player.Name}");
    });

    [RelayCommand]
    private void KickSelected()
    {
        ExecuteSafe(() =>
        {
            var selected = Players.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;
            AppLogger.Info($"[PlayersViewModel:BatchKick] Opening batch kick for {selected.Count} player(s).");
            AppLogger.TrackEvent(DialogOpenedEvent, new Dictionary<string, object> { [DialogKey] = "BatchKickDialog", ["count"] = selected.Count });
            _dashboard.ShowDialog(new KickDialogViewModel(selected, _rconService, this));
        });
    }

    [RelayCommand]
    private void BanSelected()
    {
        ExecuteSafe(() =>
        {
            var selected = Players.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;
            AppLogger.Info($"[PlayersViewModel:BatchBan] Opening batch ban for {selected.Count} player(s).");
            AppLogger.TrackEvent(DialogOpenedEvent, new Dictionary<string, object> { [DialogKey] = "BatchBanDialog", ["count"] = selected.Count });
            _dashboard.ShowDialog(new BanDialogViewModel(selected, _rconService, this));
        });
    }

    [RelayCommand]
    private Task<bool> CopyAllInfoAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        var selected = Players.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) selected = [.. Players];

        var formattedEntries = selected.Select(FormatPlayerInfo);
        var text = string.Join("\n\n", formattedEntries);

        await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[PlayersViewModel:Clipboard] Copied {selected.Count} player entries in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied player info to clipboard.");
    });

    [RelayCommand]
    public void CloseDialog() => ExecuteSafe(() => _dashboard.CloseDialog());

    [RelayCommand]
    public void OpenGlobalMessage() => ExecuteSafe(() =>
    {
        AppLogger.Info("[PlayersViewModel] Opening global message dialog.");
        _dashboard.ShowDialog(new GlobalMessageDialogViewModel(_rconService, this));
    });

    [RelayCommand]
    public void OpenAnnouncement() => ExecuteSafe(() =>
    {
        AppLogger.Info("[PlayersViewModel] Opening announcement dialog.");
        _dashboard.ShowDialog(new AnnouncementDialogViewModel(_rconService, this));
    });

    [RelayCommand]
    public Task<bool> RestartServerAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info("[PlayersViewModel] Dispatching '#restart' command...");
        AppLogger.TrackEvent("server_restart_dispatched");
        await _rconService.RestartServerAsync().ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Debug($"[PlayersViewModel] Server restart dispatched in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Server Restart", "Restart command sent.", "#restart");
    });

    [RelayCommand]
    public void ConfirmRestart()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[PlayersViewModel] Prompting confirmation for server restart.");
            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Restart Server",
                "Are you sure you want to trigger a server restart now?",
                "Restart Server",
                true,
                () => _rconService.RestartServerAsync(),
                () => _dashboard.CloseDialog()
            ));
        });
    }

    [RelayCommand]
    public Task<bool> ShutdownServerAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        AppLogger.Info("[PlayersViewModel] Dispatching '#shutdown' command...");
        AppLogger.TrackEvent("server_shutdown_dispatched");
        await _rconService.ShutdownServerAsync().ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Debug($"[PlayersViewModel] Server shutdown dispatched in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Server Shutdown", "Shutdown command sent.", "#shutdown");
    });

    [RelayCommand]
    public void ConfirmShutdown()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[PlayersViewModel] Prompting confirmation for server shutdown.");
            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Shutdown Server",
                "Are you sure you want to trigger a server shutdown now?",
                "Shutdown Server",
                true,
                () => _rconService.ShutdownServerAsync(),
                () => _dashboard.CloseDialog()
            ));
        });
    }
}