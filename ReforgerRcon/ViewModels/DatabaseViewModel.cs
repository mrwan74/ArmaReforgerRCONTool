using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class DatabaseViewModel(IRconService rconService, DashboardViewModel dashboard) : ViewModelBase
{
    public const string DefaultSortKey = "Default";

    private readonly IRconService _rconService = rconService;
    private readonly DashboardViewModel _dashboard = dashboard;
    private List<DatabasePlayerModel> _allDbPlayers = [];
    private bool _isUpdatingSelection;

    [ObservableProperty] public partial ObservableCollection<DatabasePlayerModel> Players { get; set; } = [];
    [ObservableProperty] public partial DatabasePlayerModel? SelectedPlayer { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial bool IsAllSelected { get; set; }
    [ObservableProperty] public partial string DatabaseStatsSummary { get; set; } = "Initializing database...";

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CurrentSortField => _dashboard.SettingsTab.Settings.DatabaseSortBy;
    public bool CurrentSortAscending => _dashboard.SettingsTab.Settings.DatabaseSortAscending;

    [RelayCommand]
    public Task<bool> LoadDbAsync() => ExecuteSafeAsync(async () =>
    {
        _allDbPlayers = await _rconService.GetDatabasePlayersAsync().ConfigureAwait(false);
        var stats = await PlayerDatabaseStorageService.GetDatabaseStatisticsAsync().ConfigureAwait(false);

        var protocolCount = IsBattlEyeProtocol ? stats.TotalBattlEyePlayers : stats.TotalReforgerPlayers;
        var protocolName = IsBattlEyeProtocol ? "BattlEye" : "Reforger";

        var summary = $"{protocolName} Records: {protocolCount:N0} | {stats.OnlinePlayers:N0} online | {stats.WatchlistedPlayers:N0} watchlisted | Size: {stats.DatabaseSizeBytes / 1024.0:F1} KB";

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DatabaseStatsSummary = summary;
            ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
        });
    }, "Failed to retrieve player database records from SQLite.");

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
            var mappedField = MapColumnTagToSortField(columnTag);
            if (string.IsNullOrEmpty(mappedField)) return;

            var currentField = _dashboard.SettingsTab.Settings.DatabaseSortBy;
            var currentAsc = _dashboard.SettingsTab.Settings.DatabaseSortAscending;

            if (string.Equals(currentField, mappedField, StringComparison.OrdinalIgnoreCase))
            {
                if (currentAsc)
                {
                    _dashboard.SettingsTab.Settings.DatabaseSortAscending = false;
                }
                else
                {
                    _dashboard.SettingsTab.Settings.DatabaseSortBy = DefaultSortKey;
                    _dashboard.SettingsTab.Settings.DatabaseSortAscending = true;
                }
            }
            else
            {
                _dashboard.SettingsTab.Settings.DatabaseSortBy = mappedField;
                _dashboard.SettingsTab.Settings.DatabaseSortAscending = true;
            }

            ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
        });
    }

    public async Task RefreshAfterOfflineBanAsync()
    {
        await _dashboard.BansTab.RefreshBansAsync().ConfigureAwait(false);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _dashboard.ActiveBansCount = _dashboard.BansTab.Bans.Count);
        await LoadDbAsync().ConfigureAwait(false);
    }

    private static int GetPlayerStatusWeight(DatabasePlayerModel p)
    {
        if (p.IsWatchlisted) return 3;
        if (p.IsOnline) return 2;
        if (p.HasAliases) return 1;
        return 0;
    }

    public void ApplyFilter(string query, string searchType)
    {
        ExecuteSafe(() =>
        {
            foreach (var p in Players)
            {
                p.PropertyChanged -= OnPlayerPropertyChanged;
            }

            IEnumerable<DatabasePlayerModel> filtered = _allDbPlayers;

            if (!string.IsNullOrWhiteSpace(query))
            {
                filtered = searchType switch
                {
                    "Name" => filtered.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Aliases?.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)) == true),
                    "UID" => filtered.Where(p => p.Uid.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Guid.Contains(query, StringComparison.OrdinalIgnoreCase) || p.ReforgerUid.Contains(query, StringComparison.OrdinalIgnoreCase) || p.BattlEyeGuid.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "Comment" => filtered.Where(p => p.Comment.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "Player #" => filtered.Where(p => p.Id.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase)),
                    _ => filtered.Where(p =>
                        p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Aliases?.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)) == true ||
                        p.Uid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Guid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.ReforgerUid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.BattlEyeGuid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Comment.Contains(query, StringComparison.OrdinalIgnoreCase))
                };
            }

            var sortField = _dashboard.SettingsTab.Settings.DatabaseSortBy;
            var isAscending = _dashboard.SettingsTab.Settings.DatabaseSortAscending;

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
                        ? filtered.OrderBy(p => p.DisplayBattlEyeGuid)
                        : filtered.OrderByDescending(p => p.DisplayBattlEyeGuid),
                    "IP:Port" => isAscending
                        ? filtered.OrderBy(p => p.FormattedEndpoint)
                        : filtered.OrderByDescending(p => p.FormattedEndpoint),
                    "Ping" => isAscending
                        ? filtered.OrderBy(p => p.Ping)
                        : filtered.OrderByDescending(p => p.Ping),
                    "Comment" => isAscending
                        ? filtered.OrderBy(p => p.Comment)
                        : filtered.OrderByDescending(p => p.Comment),
                    _ => filtered
                };
            }

            Players = new ObservableCollection<DatabasePlayerModel>(filtered);

            foreach (var p in Players)
            {
                p.PropertyChanged += OnPlayerPropertyChanged;
            }

            UpdateSelectedState();
        });
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isUpdatingSelection) return;
        if (e.PropertyName == nameof(DatabasePlayerModel.IsSelected))
        {
            UpdateSelectedState();
        }
    }

    partial void OnIsAllSelectedChanged(bool value)
    {
        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            _isUpdatingSelection = true;
            try
            {
                foreach (var p in Players)
                {
                    p.IsSelected = value;
                }
            }
            finally
            {
                _isUpdatingSelection = false;
            }
        });
    }

    private void UpdateSelectedState()
    {
        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            bool allSelected = Players.Count > 0 && Players.All(p => p.IsSelected);
            if (IsAllSelected != allSelected)
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
    public void OpenPlayerDetails(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;
            _dashboard.ShowDialog(new DatabasePlayerDetailViewModel(player, this));
        });
    }

    [RelayCommand]
    public void OpenOfflineBan(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;
            var targetId = IsBattlEyeProtocol ? player.DisplayBattlEyeGuid : player.DisplayReforgerUid;
            var endpoint = player.LastIpPort;
            _dashboard.ShowDialog(new OfflineBanDialogViewModel(targetId, endpoint, _rconService, this));
        });
    }

    [RelayCommand]
    public void QuickKick(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;

            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Quick Kick Target",
                $"Attempt to kick active sessions matching {player.Name}?",
                "Kick",
                true,
                async () =>
                {
                    var active = (await _rconService.GetPlayersAsync().ConfigureAwait(false)).FirstOrDefault(p =>
                        (!string.IsNullOrEmpty(player.ReforgerUid) && p.ReforgerUid == player.ReforgerUid) ||
                        (!string.IsNullOrEmpty(player.BattlEyeGuid) && p.BattlEyeGuid == player.BattlEyeGuid) ||
                        string.Equals(p.Name, player.Name, StringComparison.OrdinalIgnoreCase));

                    if (active != null)
                    {
                        await _rconService.KickPlayerAsync(active, "Kicked from Historical Database").ConfigureAwait(false);
                        ToastNotificationService.Instance.ShowToast("Kick Dispatched", $"Kicked {player.Name}", $"#kick {active.Id}");
                        _dashboard.PlayersTab.RemovePlayerFromList(active);
                        _ = _dashboard.PlayersTab.RefreshPlayersAsync();
                    }
                    else
                    {
                        ToastNotificationService.Instance.ShowToast("Player Offline", $"{player.Name} is not currently online.");
                    }
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
            if (player == null) return;
            var targetId = IsBattlEyeProtocol ? player.BattlEyeGuid : player.ReforgerUid;
            _dashboard.ShowDialog(new SetCommentDialogViewModel(player.Name, targetId, player.Comment, _rconService, _dashboard));
        });
    }

    [RelayCommand]
    public Task<bool> ToggleWatchlist(DatabasePlayerModel? player) => ExecuteSafeAsync(async () =>
    {
        player ??= SelectedPlayer;
        if (player == null) return;
        player.IsWatchlisted = !player.IsWatchlisted;

        if (_dashboard.PlayersTab.Players.FirstOrDefault(p =>
            (!string.IsNullOrEmpty(player.ReforgerUid) && p.ReforgerUid == player.ReforgerUid) ||
            (!string.IsNullOrEmpty(player.BattlEyeGuid) && p.BattlEyeGuid == player.BattlEyeGuid) ||
            string.Equals(p.Name, player.Name, StringComparison.OrdinalIgnoreCase)) is { } livePlayer)
        {
            livePlayer.IsWatchlisted = player.IsWatchlisted;
        }

        var identifier = IsBattlEyeProtocol ? player.BattlEyeGuid : player.ReforgerUid;
        await PlayerDatabaseStorageService.SetWatchlistStatusAsync(identifier, player.IsWatchlisted, _rconService.CurrentProtocol).ConfigureAwait(false);

        var feedbackMessage = player.IsWatchlisted ? $"Added {player.Name} to Watchlist" : $"Removed {player.Name} from Watchlist";
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
        player ??= SelectedPlayer;
        if (player == null) return;
        var text = FormatDatabasePlayerInfo(player);
        await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied info for {player.Name}");
    });

    [RelayCommand]
    private Task<bool> CopyAllInfoAsync() => ExecuteSafeAsync(async () =>
    {
        var selected = Players.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) selected = [.. Players];

        var formattedEntries = selected.Select(FormatDatabasePlayerInfo);
        var text = string.Join("\n\n", formattedEntries);

        await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied player database to clipboard.");
    });

    [RelayCommand]
    public void CloseDialog() => ExecuteSafe(() => _dashboard.CloseDialog());
}