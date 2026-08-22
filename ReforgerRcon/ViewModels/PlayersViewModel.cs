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

namespace ReforgerRcon.ViewModels;

public partial class PlayersViewModel(IRconService rconService, DashboardViewModel dashboard) : ViewModelBase
{
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

    [RelayCommand]
    public Task<bool> RefreshPlayersAsync() => ExecuteSafeAsync(async () =>
    {
        var sw = Stopwatch.StartNew();
        AppLogger.Debug("[PlayersViewModel:Timing] Starting player refresh operation...");

        _allPlayers = await _rconService.GetPlayersAsync();
        var networkTime = sw.ElapsedMilliseconds;

        var filterSw = Stopwatch.StartNew();
        ApplyFilter(_dashboard.SearchQuery, _dashboard.SearchType);
        filterSw.Stop();
        sw.Stop();

        _dashboard.OnlinePlayersCount = Players.Count;
        AppLogger.Info($"[PlayersViewModel:Timing] Players tab populated: {_allPlayers.Count} players loaded (Network/DB: {networkTime} ms, Filter: {filterSw.ElapsedMilliseconds} ms, Total: {sw.ElapsedMilliseconds} ms).");
    });

    public void RemovePlayerFromList(PlayerModel player)
    {
        ExecuteSafe(() =>
        {
            _allPlayers.RemoveAll(p => p.Uid == player.Uid || (p.Id == player.Id && p.Id != 0));

            var match = Players.FirstOrDefault(p => p.Uid == player.Uid || (p.Id == player.Id && p.Id != 0));
            if (match is not null)
            {
                Players.Remove(match);
            }

            _dashboard.OnlinePlayersCount = Players.Count;
            UpdateSelectedCount();
            AppLogger.Debug($"[PlayersViewModel] Removed '{player.Name}' from live list immediately.");
        });
    }

    public async Task TriggerPostBanRefreshAsync()
    {
        await RefreshPlayersAsync();
        await _dashboard.BansTab.RefreshBansAsync();
        _dashboard.ActiveBansCount = _dashboard.BansTab.Bans.Count;
    }

    public void ApplyFilter(string query, string searchType)
    {
        ExecuteSafe(() =>
        {
            foreach (var p in Players)
            {
                p.PropertyChanged -= OnPlayerPropertyChanged;
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                Players = new ObservableCollection<PlayerModel>(_allPlayers);
            }
            else
            {
                IEnumerable<PlayerModel> filtered = searchType switch
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
                Players = new ObservableCollection<PlayerModel>(filtered);
            }

            foreach (var p in Players)
            {
                p.PropertyChanged += OnPlayerPropertyChanged;
            }

            UpdateSelectedCount();
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
            if (player == null) return;
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
            _dashboard.ShowDialog(new ConfirmDialogViewModel(
                "Quick Permanent Ban",
                $"Are you sure you want to PERMANENTLY ban {player.Name} (Player #{player.Id})?",
                "Permanent Ban",
                true,
                async () =>
                {
                    bool isSuccess = await _rconService.BanPlayerAsync(player, 0, "Quick Permanent Ban by Administrator");
                    var cmd = _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn
                        ? $"#ban create {player.Id} 0 Quick Ban"
                        : $"addBan {player.Guid} 0 Quick Ban";

                    if (isSuccess)
                    {
                        ToastNotificationService.Instance.ShowSuccess("Permanent Ban", $"Banned {player.Name}", cmd, async () =>
                        {
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

        await PlayerDatabaseStorageService.SetWatchlistStatusAsync(player.Uid, player.IsWatchlisted);
        var feedbackMessage = player.IsWatchlisted ? $"Added {player.Name} to Watchlist" : $"Removed {player.Name} from Watchlist";
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
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied info for {player.Name}");
    });

    [RelayCommand]
    private void KickSelected()
    {
        ExecuteSafe(() =>
        {
            var selected = Players.Where(p => p.IsSelected).ToList();
            if (selected.Count == 0) return;
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
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied player info to clipboard.");
    });

    [RelayCommand]
    public void CloseDialog() => ExecuteSafe(() => _dashboard.CloseDialog());

    [RelayCommand]
    public void OpenGlobalMessage() => ExecuteSafe(() => _dashboard.ShowDialog(new GlobalMessageDialogViewModel(_rconService, this)));

    [RelayCommand]
    public void OpenAnnouncement() => ExecuteSafe(() => _dashboard.ShowDialog(new AnnouncementDialogViewModel(_rconService, this)));

    [RelayCommand]
    public Task<bool> RestartServerAsync() => ExecuteSafeAsync(async () =>
    {
        await _rconService.RestartServerAsync();
        ToastNotificationService.Instance.ShowToast("Server Restart", "Restart command sent.", "#restart");
    });

    [RelayCommand]
    public void ConfirmRestart()
    {
        ExecuteSafe(() =>
        {
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
        await _rconService.ShutdownServerAsync();
        ToastNotificationService.Instance.ShowToast("Server Shutdown", "Shutdown command sent.", "#shutdown");
    });

    [RelayCommand]
    public void ConfirmShutdown()
    {
        ExecuteSafe(() =>
        {
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