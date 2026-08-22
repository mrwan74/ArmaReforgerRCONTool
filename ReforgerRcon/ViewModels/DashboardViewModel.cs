using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Theming;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using ReforgerRcon.Views;

namespace ReforgerRcon.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly IRconService _rconService;
    private readonly Action _onDisconnectRequested;
    private readonly DispatcherTimer _timer;
    private ConsoleWindow? _detachedConsoleWindow;

    [ObservableProperty] public partial ServerProfile Profile { get; set; }
    [ObservableProperty] public partial int OnlinePlayersCount { get; set; }
    [ObservableProperty] public partial int ActiveBansCount { get; set; }
    [ObservableProperty] public partial int RefreshCountdown { get; set; }
    [ObservableProperty] public partial double RefreshProgress { get; set; } = 100;
    [ObservableProperty] public partial string LastPacketTimerText { get; set; } = "0s ago";
    [ObservableProperty] public partial int Ping { get; set; } = 25;
    [ObservableProperty] public partial bool IsHeartbeatVisible { get; set; } = true;
    [ObservableProperty] public partial bool IsConnected { get; set; } = true;
    [ObservableProperty] public partial bool IsConsoleFullscreen { get; set; }
    [ObservableProperty] public partial bool IsConsoleDetached { get; set; }

    [ObservableProperty] public partial GridLength TabsRowHeight { get; set; } = new(3, GridUnitType.Star);
    [ObservableProperty] public partial GridLength SplitterRowHeight { get; set; } = new(8, GridUnitType.Pixel);
    [ObservableProperty] public partial GridLength ConsoleRowHeight { get; set; } = new(2, GridUnitType.Star);

    [ObservableProperty] public partial ViewModelBase? ActiveDialog { get; set; }
    [ObservableProperty] public partial bool IsDialogVisible { get; set; }

    [ObservableProperty] public partial string SearchQuery { get; set; } = string.Empty;
    [ObservableProperty] public partial string SearchType { get; set; } = "Name";

    public ObservableCollection<string> SearchTypes { get; } = ["Name", "UID", "Player #", "Comment"];

    public PlayersViewModel PlayersTab { get; }
    public BansViewModel BansTab { get; }
    public DatabaseViewModel DatabaseTab { get; }
    public ConsoleViewModel ConsoleTab { get; }
    public SettingsViewModel SettingsTab { get; }

    public bool IsReforgerProtocol => Profile.Protocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => Profile.Protocol == RconProtocol.BattlEye;

    public DashboardViewModel(ServerProfile profile, IRconService rconService, Action onDisconnectRequested)
    {
        Profile = profile;
        _rconService = rconService;
        _onDisconnectRequested = onDisconnectRequested;

        AppLogger.Info($"[DashboardViewModel] Initializing for {Profile.ServerIp}:{Profile.Port} ({Profile.Protocol})");

        PlayersTab = new PlayersViewModel(_rconService, this);
        BansTab = new BansViewModel(_rconService, this);
        DatabaseTab = new DatabaseViewModel(_rconService, this);
        ConsoleTab = new ConsoleViewModel(_rconService, this);
        SettingsTab = new SettingsViewModel(this);

        RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);

        _rconService.ConnectionLost += OnConnectionLost;
        _rconService.PlayerJoined += OnPlayerJoined;
        _rconService.PlayerLeft += OnPlayerLeft;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _ = RefreshAllAsync(forceBans: true);
    }

    private void OnPlayerJoined(object? sender, PlayerModel player)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                AppLogger.Info($"[DashboardViewModel] Player joined: {player.Name} (UID: {player.Uid}) [Watchlisted: {player.IsWatchlisted}]");

                if (player.IsWatchlisted && SettingsTab.Settings.AlertOnWatchlistJoin)
                {
                    SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                    ToastNotificationService.Instance.ShowWarning(
                        "Watchlist Alert",
                        $"Watchlisted player '{player.Name}' has joined the server."
                    );
                }
                else if (SettingsTab.Settings.AlertOnJoin)
                {
                    SoundNotificationService.PlayAlert(SoundAlertType.PlayerJoined);
                    ToastNotificationService.Instance.ShowToast(
                        "Player Connected",
                        $"{player.Name} joined the server."
                    );
                }
            });
        });
    }

    private void OnPlayerLeft(object? sender, PlayerModel player)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                AppLogger.Info($"[DashboardViewModel] Player left: {player.Name} (UID: {player.Uid}) [Watchlisted: {player.IsWatchlisted}]");

                if (player.IsWatchlisted && SettingsTab.Settings.AlertOnWatchlistLeave)
                {
                    SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                    ToastNotificationService.Instance.ShowWarning(
                        "Watchlist Alert",
                        $"Watchlisted player '{player.Name}' has left the server."
                    );
                }
                else if (SettingsTab.Settings.AlertOnLeave)
                {
                    SoundNotificationService.PlayAlert(SoundAlertType.PlayerLeft);
                }
            });
        });
    }

    private void OnConnectionLost(object? sender, string reason)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                AppLogger.Warn($"[DashboardViewModel] Connection lost ({reason}). Executing full offline session teardown and presenting recovery dialog.");

                _timer.Stop();
                IsConnected = false;
                IsHeartbeatVisible = false;
                OnlinePlayersCount = 0;

                foreach (var player in PlayersTab.Players)
                {
                    player.Ping = 0;
                }

                SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);

                ShowDialog(new ConnectionLostDialogViewModel(
                    Profile,
                    reason,
                    _rconService,
                    onReconnected: () =>
                    {
                        IsConnected = true;
                        CloseDialog();
                        _timer.Start();
                        _ = RefreshAllAsync(forceBans: true);
                    },
                    onReturnToLogin: () =>
                    {
                        CloseDialog();
                        _detachedConsoleWindow?.Close();
                        _detachedConsoleWindow = null;
                        _onDisconnectRequested();
                    },
                    onDismiss: () => CloseDialog()
                ));
            });
        });
    }

    public void ShowDialog(ViewModelBase dialog)
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DashboardViewModel] Presenting dialog: {dialog.GetType().Name}");
            ActiveDialog = dialog;
            IsDialogVisible = true;
        });
    }

    [RelayCommand]
    public void CloseDialog()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[DashboardViewModel] Closing active modal dialog.");
            IsDialogVisible = false;
            ActiveDialog = null;
        });
    }

    private async void OnTimerTick(object? sender, EventArgs e)
    {
        await ExecuteSafeAsync(async () =>
        {
            if (!_rconService.IsConnected && IsConnected)
            {
                OnConnectionLost(this, "Connection timed out (No packets received)");
                return;
            }

            RefreshCountdown--;
            if (RefreshCountdown <= 0)
            {
                RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
                await RefreshAllAsync(forceBans: false);
            }

            RefreshProgress = (double)RefreshCountdown / Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds) * 100;
            var diff = (DateTime.UtcNow - _rconService.LastPacketTime).TotalSeconds;
            LastPacketTimerText = $"{(int)diff}s ago";
            Ping = _rconService.PingMs;
            IsHeartbeatVisible = !IsHeartbeatVisible;
        });
    }

    public Task<bool> RefreshAllAsync(bool forceBans = false)
    {
        return ExecuteSafeAsync(async () =>
        {
            if (!_rconService.IsConnected) return;

            var sw = Stopwatch.StartNew();
            AppLogger.Debug("[DashboardViewModel:Timing] Starting dashboard query refresh...");

            await PlayersTab.RefreshPlayersAsync();
            OnlinePlayersCount = PlayersTab.Players.Count;

            if (forceBans || SettingsTab.Settings.AutoRefreshBans)
            {
                await BansTab.RefreshBansAsync();
                ActiveBansCount = BansTab.Bans.Count;
            }

            sw.Stop();
            AppLogger.Info($"[DashboardViewModel:Timing] Dashboard refresh cycle completed in {sw.ElapsedMilliseconds} ms (Online Players: {OnlinePlayersCount}, Active Bans: {ActiveBansCount}).");
        });
    }

    partial void OnSearchQueryChanged(string value)
    {
        ExecuteSafe(() =>
        {
            PlayersTab.ApplyFilter(value, SearchType);
            BansTab.ApplyFilter(value, SearchType);
            DatabaseTab.ApplyFilter(value, SearchType);
        });
    }

    partial void OnSearchTypeChanged(string value)
    {
        ExecuteSafe(() =>
        {
            PlayersTab.ApplyFilter(SearchQuery, value);
            BansTab.ApplyFilter(SearchQuery, value);
            DatabaseTab.ApplyFilter(SearchQuery, value);
        });
    }

    [RelayCommand]
    public static void ToggleTheme() => LuminaThemeManager.ToggleThemeVariant();

    [RelayCommand]
    public void ToggleConsoleFullscreen()
    {
        ExecuteSafe(() =>
        {
            IsConsoleFullscreen = !IsConsoleFullscreen;
            ConsoleTab.IsFullscreen = IsConsoleFullscreen;
            AppLogger.Debug($"[DashboardViewModel] Console fullscreen state: {IsConsoleFullscreen}");
        });
    }

    [RelayCommand]
    public void DetachConsole()
    {
        ExecuteSafe(() =>
        {
            if (IsConsoleDetached)
            {
                _detachedConsoleWindow?.Activate();
                return;
            }

            AppLogger.Info("[DashboardViewModel] Detaching console window...");
            IsConsoleDetached = true;
            ConsoleTab.IsDetached = true;
            UpdateLayoutDimensions();

            _detachedConsoleWindow = new ConsoleWindow(ConsoleTab, () =>
            {
                IsConsoleDetached = false;
                ConsoleTab.IsDetached = false;
                _detachedConsoleWindow = null;
                UpdateLayoutDimensions();
            });

            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                _detachedConsoleWindow.Show(desktop.MainWindow);
            }
            else
            {
                _detachedConsoleWindow.Show();
            }
        });
    }

    [RelayCommand]
    public void ReattachConsole()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[DashboardViewModel] Reattaching console window...");
            _detachedConsoleWindow?.Close();
            _detachedConsoleWindow = null;
            IsConsoleDetached = false;
            ConsoleTab.IsDetached = false;
            UpdateLayoutDimensions();
        });
    }

    private void UpdateLayoutDimensions()
    {
        if (IsConsoleDetached)
        {
            TabsRowHeight = new GridLength(1, GridUnitType.Star);
            SplitterRowHeight = new GridLength(0, GridUnitType.Pixel);
            ConsoleRowHeight = new GridLength(0, GridUnitType.Pixel);
        }
        else
        {
            TabsRowHeight = new GridLength(3, GridUnitType.Star);
            SplitterRowHeight = new GridLength(8, GridUnitType.Pixel);
            ConsoleRowHeight = new GridLength(2, GridUnitType.Star);
        }
    }

    [RelayCommand]
    private Task<bool> DisconnectAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            AppLogger.Info("[DashboardViewModel] Operator initiated disconnect.");
            _timer.Stop();
            _rconService.ConnectionLost -= OnConnectionLost;
            _rconService.PlayerJoined -= OnPlayerJoined;
            _rconService.PlayerLeft -= OnPlayerLeft;
            _detachedConsoleWindow?.Close();
            _detachedConsoleWindow = null;
            await _rconService.DisconnectAsync();
            _onDisconnectRequested();
        });
    }
}