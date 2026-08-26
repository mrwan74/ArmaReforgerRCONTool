using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using Sentry;

namespace ReforgerRcon.ViewModels;

public partial class PlayersViewModel(IRconService rconService, DashboardViewModel dashboard) : ViewModelBase
{
    public const string DefaultSortKey = "Default";

    private readonly IRconService _rconService = rconService;
    private readonly DashboardViewModel _dashboard = dashboard;
    private List<PlayerModel> _allPlayers = [];
    private bool _isUpdatingSelection;

    [ObservableProperty] public partial ObservableCollection<PlayerModel> Players { get; set; } = [];
    [ObservableProperty] public partial PlayerModel? SelectedPlayer { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial int SelectedCount { get; set; }

    private bool _isAllSelected;
    public bool IsAllSelected
    {
        get => _isAllSelected;
        set
        {
            if (SetProperty(ref _isAllSelected, value))
            {
                ApplySelectAll(value);
            }
        }
    }

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CurrentSortField => _dashboard.SettingsTab.Settings.PlayersSortBy;
    public bool CurrentSortAscending => _dashboard.SettingsTab.Settings.PlayersSortAscending;

    [RelayCommand]
    public Task<bool> RefreshPlayersAsync() => ExecuteSafeAsync(async () =>
    {
        using var timing = AppLogger.Measure("PlayersViewModel.RefreshPlayersAsync");
        AppLogger.Debug("[PlayersViewModel] Querying live player list from server...");

        _allPlayers = await _rconService.GetPlayersAsync();
        ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);

        _dashboard.OnlinePlayersCount = Players.Count;
        AppLogger.Info($"[PlayersViewModel] Populated Players tab with {_allPlayers.Count} player record(s) ({Players.Count} visible after filter).");
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
            var mappedField = MapColumnTagToSortField(columnTag);
            if (string.IsNullOrEmpty(mappedField)) return;

            var currentField = _dashboard.SettingsTab.Settings.PlayersSortBy;
            var currentAsc = _dashboard.SettingsTab.Settings.PlayersSortAscending;

            if (string.Equals(currentField, mappedField, StringComparison.OrdinalIgnoreCase))
            {
                if (currentAsc)
                {
                    _dashboard.SettingsTab.Settings.PlayersSortAscending = false;
                    AppLogger.Info($"[PlayersViewModel] Cycled sort for '{mappedField}' -> Descending.");
                }
                else
                {
                    _dashboard.SettingsTab.Settings.PlayersSortBy = DefaultSortKey;
                    _dashboard.SettingsTab.Settings.PlayersSortAscending = true;
                    AppLogger.Info($"[PlayersViewModel] Cycled sort for '{mappedField}' -> Default (raw server order).");
                }
            }
            else
            {
                _dashboard.SettingsTab.Settings.PlayersSortBy = mappedField;
                _dashboard.SettingsTab.Settings.PlayersSortAscending = true;
                AppLogger.Info($"[PlayersViewModel] Cycled sort column -> '{mappedField}' (Ascending).");
            }

            ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
        });
    }

    public void AddOrUpdatePlayer(PlayerModel player)
    {
        ExecuteSafe(() =>
        {
            var existing = _allPlayers.FirstOrDefault(p => RconService.IsSamePlayer(p, player));

            if (existing != null)
            {
                existing.Name = player.Name;
                if (!string.IsNullOrEmpty(player.Ip) && !player.Ip.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)) existing.Ip = player.Ip;
                if (player.Port > 0) existing.Port = player.Port;
                if (player.Country != null && player.Country.Code != "xx") existing.Country = player.Country;
                if (!string.IsNullOrEmpty(player.DisplayLocation)) existing.DisplayLocation = player.DisplayLocation;
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
            AppLogger.Debug($"[PlayersViewModel] Added/Updated live player '{player.Name}' (ID: #{player.Id}, GUID: {player.Guid}). Total: {_allPlayers.Count}");
        });
    }

    public void RemovePlayerFromList(PlayerModel player)
    {
        ExecuteSafe(() =>
        {
            var purged = _allPlayers.RemoveAll(p => RconService.IsSamePlayer(p, player));

            var match = Players.FirstOrDefault(p => RconService.IsSamePlayer(p, player));
            if (match is not null)
            {
                match.PropertyChanged -= OnPlayerPropertyChanged;
                Players.Remove(match);
            }

            _dashboard.OnlinePlayersCount = Players.Count;
            UpdateSelectedCount();
            AppLogger.Info($"[PlayersViewModel] Removed '{player.Name}' (ID: #{player.Id}) from live list. Purged: {purged}, Remaining: {Players.Count}");
        });
    }

    public async Task TriggerPostBanRefreshAsync()
    {
        AppLogger.Info("[PlayersViewModel] Triggering post-ban refresh across Players and Bans tabs...");
        await RefreshPlayersAsync();
        await _dashboard.BansTab.RefreshBansAsync();
        _dashboard.ActiveBansCount = _dashboard.BansTab.Bans.Count;
    }

    private static int GetPlayerStatusWeight(PlayerModel p)
    {
        if (p.IsWatchlisted) return 2;
        if (p.HasAliases) return 1;
        return 0;
    }

    public void ApplyFilter(string query, string searchType)
    {
        ExecuteSafe(() =>
        {
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
            AppLogger.Trace($"[PlayersViewModel] Filtered {Players.Count}/{_allPlayers.Count} players using query '{query}' (Sort: {sortField}, Asc: {isAscending}).");
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
            AppLogger.Debug($"[PlayersViewModel] Multi-select mode toggled: {IsMultiSelectMode}");

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
    public void ToggleSelectAll() => ExecuteSafe(() => IsAllSelected = !IsAllSelected);

    private void ApplySelectAll(bool isSelected)
    {
        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            _isUpdatingSelection = true;
            try
            {
                foreach (var p in Players)
                {
                    p.IsSelected = isSelected;
                }
                SelectedCount = isSelected ? Players.Count : 0;
                AppLogger.Debug($"[PlayersViewModel] Toggled select-all: {isSelected} ({SelectedCount} selected).");
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
        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            SelectedCount = Players.Count(p => p.IsSelected);
            bool allSelected = Players.Count > 0 && SelectedCount == Players.Count;
            if (_isAllSelected != allSelected)
            {
                _isUpdatingSelection = true;
                try
                {
                    IsAllSelected = allSelected;
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
                AppLogger.Warn("[PlayersViewModel] OpenPlayerDetails invoked with null target.");
                return;
            }
            AppLogger.Info($"[PlayersViewModel] Opening details dialog for player '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}).");
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
            AppLogger.Info($"[PlayersViewModel] Opening kick dialog for '{player.Name}' (ID: #{player.Id}).");
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
            AppLogger.Info($"[PlayersViewModel] Opening ban dialog for '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}).");
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

            AppLogger.Info($"[PlayersViewModel] Prompting quick permanent ban for '{player.Name}' (ID: #{player.Id}).");
            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Quick Permanent Ban",
                $"Are you sure you want to PERMANENTLY ban {player.Name} (Player #{player.Id})?",
                "Permanent Ban",
                true,
                async () =>
                {
                    AppLogger.Info($"[PlayersViewModel] Executing quick permanent ban for '{player.Name}'...");
                    bool isSuccess = await _rconService.BanPlayerAsync(player, 0, "Quick Permanent Ban by Administrator");
                    var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                        ? $"#ban create {player.Id} 0 Quick Ban"
                        : $"addBan {player.Guid} 0 Quick Ban";

                    if (isSuccess)
                    {
                        ToastNotificationService.Instance.ShowSuccess("Permanent Ban", $"Banned {player.Name}", cmd, async () =>
                        {
                            AppLogger.Info($"[PlayersViewModel] Undo triggered for quick permanent ban of '{player.Name}'.");
                            var allBans = await _rconService.GetBansAsync();
                            var ban = allBans.FirstOrDefault(b => b.IdentityId == player.Uid || b.IdentityId == player.Guid);
                            if (ban != null)
                            {
                                await _rconService.RemoveBanAsync(ban);
                                await TriggerPostBanRefreshAsync();
                            }
                        });

                        RemovePlayerFromList(player);
                        await TriggerPostBanRefreshAsync();
                    }
                    else
                    {
                        ToastNotificationService.Instance.ShowError("Permanent Ban Failed", $"Could not ban {player.Name} (Server timeout or invalid ID).", cmd);
                    }
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
            AppLogger.Debug($"[PlayersViewModel] Opening set comment dialog for '{player.Name}'.");
            _dashboard.ShowDialog(new SetCommentDialogViewModel(player.Name, player.Uid, player.Comment, _rconService, _dashboard));
        });
    }

    [RelayCommand]
    public Task<bool> ToggleWatchlist(PlayerModel? player) => ExecuteSafeAsync(async () =>
    {
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
            identifier = string.IsNullOrWhiteSpace(player.Guid) || player.Guid.StartsWith("init", StringComparison.OrdinalIgnoreCase)
                ? player.Uid
                : player.Guid;
        }
        else
        {
            identifier = player.Uid;
        }

        await PlayerDatabaseStorageService.SetWatchlistStatusAsync(identifier, player.IsWatchlisted, _rconService.CurrentProtocol);
        var feedbackMessage = player.IsWatchlisted ? $"Added {player.Name} to Watchlist" : $"Removed {player.Name} from Watchlist";
        AppLogger.Info($"[PlayersViewModel] Watchlist toggled for '{player.Name}' (ID: {identifier}) -> {player.IsWatchlisted}");
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
        player ??= SelectedPlayer;
        if (player == null) return;
        var text = FormatPlayerInfo(player);
        await ClipboardService.SetTextAsync(text);
        AppLogger.Info($"[PlayersViewModel] Copied player info for '{player.Name}' to clipboard.");
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied info for {player.Name}");
    });

    [RelayCommand]
    private void KickSelected()
    {
        ExecuteSafe(() =>
        {
            var selected = Players.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;
            AppLogger.Info($"[PlayersViewModel] Opening batch kick dialog for {selected.Count} player(s).");
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
            AppLogger.Info($"[PlayersViewModel] Opening batch ban dialog for {selected.Count} player(s).");
            _dashboard.ShowDialog(new BanDialogViewModel(selected, _rconService, this));
        });
    }

    [RelayCommand]
    private Task<bool> CopyAllInfoAsync() => ExecuteSafeAsync(async () =>
    {
        var selected = Players.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) selected = [.. Players];

        var formattedEntries = selected.Select(FormatPlayerInfo);
        var text = string.Join("\n\n", formattedEntries);

        await ClipboardService.SetTextAsync(text);
        AppLogger.Info($"[PlayersViewModel] Copied {selected.Count} player entries to clipboard.");
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied player info to clipboard.");
    });

    [RelayCommand]
    public void CloseDialog() => ExecuteSafe(() => _dashboard.CloseDialog());

    [RelayCommand]
    public void OpenGlobalMessage() => ExecuteSafe(() =>
    {
        AppLogger.Info("[PlayersViewModel] Opening global message modal.");
        _dashboard.ShowDialog(new GlobalMessageDialogViewModel(_rconService, this));
    });

    [RelayCommand]
    public void OpenAnnouncement() => ExecuteSafe(() =>
    {
        AppLogger.Info("[PlayersViewModel] Opening announcement modal.");
        _dashboard.ShowDialog(new AnnouncementDialogViewModel(_rconService, this));
    });

    [RelayCommand]
    public Task<bool> RestartServerAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Info("[PlayersViewModel] Dispatching restart server command...");
        await _rconService.RestartServerAsync();
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
        AppLogger.Info("[PlayersViewModel] Dispatching shutdown server command...");
        await _rconService.ShutdownServerAsync();
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