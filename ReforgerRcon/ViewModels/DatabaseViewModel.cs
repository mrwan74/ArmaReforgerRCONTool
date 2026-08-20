using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class DatabaseViewModel : ViewModelBase
{
    private readonly IRconService _rconService;
    private readonly DashboardViewModel _dashboard;
    private List<DatabasePlayerModel> _allDbPlayers = [];
    private bool _isUpdatingSelection;

    [ObservableProperty] public partial ObservableCollection<DatabasePlayerModel> Players { get; set; } = [];
    [ObservableProperty] public partial DatabasePlayerModel? SelectedPlayer { get; set; }
    [ObservableProperty] public partial bool IsMultiSelectMode { get; set; }
    [ObservableProperty] public partial bool IsAllSelected { get; set; }

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public DatabaseViewModel(IRconService rconService, DashboardViewModel dashboard)
    {
        _rconService = rconService;
        _dashboard = dashboard;
        _ = LoadDbAsync();
    }

    [RelayCommand]
    public Task<bool> LoadDbAsync() => ExecuteSafeAsync(async () =>
    {
        _allDbPlayers = await _rconService.GetDatabasePlayersAsync();
        ApplyFilter(string.Empty, string.Empty);
    });

    // Database search strictly checks Player Name, Player UID, and Comment
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
                Players = new ObservableCollection<DatabasePlayerModel>(_allDbPlayers);
            }
            else
            {
                IEnumerable<DatabasePlayerModel> filtered = searchType switch
                {
                    "Name" => _allDbPlayers.Where(p => p.Name.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "UID" => _allDbPlayers.Where(p => p.Uid.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Guid.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    "Comment" => _allDbPlayers.Where(p => p.Comment.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    _ => _allDbPlayers.Where(p =>
                        p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Uid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Guid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Comment.Contains(query, StringComparison.OrdinalIgnoreCase))
                };

                Players = new ObservableCollection<DatabasePlayerModel>(filtered);
            }

            foreach (var p in Players)
            {
                p.PropertyChanged += OnPlayerPropertyChanged;
            }

            UpdateSelectedState();
        });
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DatabasePlayerModel.IsSelected))
        {
            UpdateSelectedState();
        }
    }

    [SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "Generated partial property change hook")]
    partial void OnIsAllSelectedChanged(bool value)
    {
        ExecuteSafe(() =>
        {
            if (_isUpdatingSelection) return;
            _isUpdatingSelection = true;
            foreach (var p in Players)
            {
                p.IsSelected = value;
            }
            _isUpdatingSelection = false;
        });
    }

    private void UpdateSelectedState()
    {
        ExecuteSafe(() =>
        {
            if (!_isUpdatingSelection)
            {
                _isUpdatingSelection = true;
                IsAllSelected = Players.Count > 0 && Players.All(p => p.IsSelected);
                _isUpdatingSelection = false;
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
            _dashboard.ShowDialog(new OfflineBanDialogViewModel(player.Uid, player.LastIp, _rconService, this));
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
                $"Attempt to kick active sessions matching {player.Name} ({player.Uid})?",
                "Kick",
                true,
                async () =>
                {
                    var active = (await _rconService.GetPlayersAsync()).FirstOrDefault(p => p.Uid == player.Uid);
                    if (active != null)
                    {
                        await _rconService.KickPlayerAsync(active, "Kicked from Historical Database");
                        ToastNotificationService.Instance.ShowToast("Kick Dispatched", $"Kicked {player.Name}", $"#kick {active.Id}");
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
            _dashboard.ShowDialog(new SetCommentDialogViewModel(player.Name, player.Uid, player.Comment, _rconService, _dashboard));
        });
    }

    [RelayCommand]
    public void ToggleWatchlist(DatabasePlayerModel? player)
    {
        ExecuteSafe(() =>
        {
            player ??= SelectedPlayer;
            if (player == null) return;
            player.IsWatchlisted = !player.IsWatchlisted;
            ToastNotificationService.Instance.ShowToast("Watchlist", $"{player.Name} watchlist status: {player.IsWatchlisted}");
        });
    }

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

        // For Reforger, Player# is dynamic and not shown in database info
        if (IsReforgerProtocol)
        {
            return $"Status: {status}\n" +
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
               $"Ping: {p.PingDisplay}\n" +
               $"Comment: {p.Comment}";
    }

    [RelayCommand]
    public Task<bool> CopyPlayerInfoAsync(DatabasePlayerModel? player) => ExecuteSafeAsync(async () =>
    {
        player ??= SelectedPlayer;
        if (player == null) return;
        var text = FormatDatabasePlayerInfo(player);
        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied info for {player.Name}");
    });

    [RelayCommand]
    private Task<bool> CopyAllInfoAsync() => ExecuteSafeAsync(async () =>
    {
        var selected = Players.Where(p => p.IsSelected).ToList();
        if (selected.Count == 0) selected = [.. Players];

        var formattedEntries = selected.Select(FormatDatabasePlayerInfo);
        var text = string.Join("\n\n", formattedEntries);

        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Clipboard", "Copied player database to clipboard.");
    });

    [RelayCommand]
    public void CloseDialog() => ExecuteSafe(() => _dashboard.CloseDialog());
}