using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
    private readonly ConcurrentDictionary<string, long> _activeJoinToasts = new(StringComparer.OrdinalIgnoreCase);
    private ConsoleWindow? _detachedConsoleWindow;

    [ObservableProperty] public partial ServerProfile Profile { get; set; }
    [ObservableProperty] public partial int OnlinePlayersCount { get; set; }
    [ObservableProperty] public partial int ActiveBansCount { get; set; }
    [ObservableProperty] public partial int ConnectedAdminsCount { get; set; }
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

    public SettingsViewModel SettingsTab { get; }
    public PlayersViewModel PlayersTab { get; }
    public BansViewModel BansTab { get; }
    public DatabaseViewModel DatabaseTab { get; }
    public ConsoleViewModel ConsoleTab { get; }

    public bool IsReforgerProtocol => Profile.Protocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => Profile.Protocol == RconProtocol.BattlEye;

    public DashboardViewModel(ServerProfile profile, IRconService rconService, Action onDisconnectRequested)
    {
        Profile = profile;
        _rconService = rconService;
        _onDisconnectRequested = onDisconnectRequested;

        AppLogger.Info($"[DashboardViewModel] Initializing for {Profile.ServerIp}:{Profile.Port} ({Profile.Protocol})");

        SettingsTab = new SettingsViewModel(this);
        PlayersTab = new PlayersViewModel(_rconService, this);
        BansTab = new BansViewModel(_rconService, this);
        DatabaseTab = new DatabaseViewModel(_rconService, this);
        ConsoleTab = new ConsoleViewModel(_rconService, this);

        RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);

        _rconService.ConnectionLost += OnConnectionLost;
        _rconService.PlayerJoined += OnPlayerJoined;
        _rconService.PlayerLeft += OnPlayerLeft;
        _rconService.PlayerKickedStream += OnPlayerKickedStream;
        _rconService.PlayerBannedStream += OnPlayerBannedStream;
        _rconService.AdminConnectedStream += OnAdminConnectedStream;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();
        AppLogger.Debug($"[DashboardViewModel] Heartbeat and auto-refresh timer started (Interval: {SettingsTab.Settings.RefreshIntervalSeconds}s).");

        _ = RefreshAllAsync(forceBans: true);
    }

    private void OnPlayerJoined(object? sender, PlayerModel player)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                if (string.IsNullOrWhiteSpace(player.Name)) return;

                var dedupeKey = $"{player.Id}_{player.Name.Trim()}";
                var now = Stopwatch.GetTimestamp();

                if (_activeJoinToasts.TryGetValue(dedupeKey, out var lastDispatched))
                {
                    var elapsedSec = Stopwatch.GetElapsedTime(lastDispatched).TotalSeconds;
                    if (elapsedSec < 6.0)
                    {
                        AppLogger.Debug($"[DashboardViewModel] UI suppressed duplicate join toast for '{player.Name}' (ID: #{player.Id}, Elapsed: {elapsedSec:F1}s).");
                        PlayersTab.AddOrUpdatePlayer(player);
                        OnlinePlayersCount = PlayersTab.Players.Count;
                        return;
                    }
                }

                _activeJoinToasts[dedupeKey] = now;

                AppLogger.Info($"[DashboardViewModel] Player joined: '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}, Location: '{player.DisplayLocation}') [Watchlisted: {player.IsWatchlisted}]");

                PlayersTab.AddOrUpdatePlayer(player);
                OnlinePlayersCount = PlayersTab.Players.Count;

                if (player.IsWatchlisted && SettingsTab.Settings.AlertOnWatchlistJoin)
                {
                    SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                    ToastNotificationService.Instance.ShowWarning(
                        "Watchlist Alert",
                        $"Watchlisted player '{player.Name}' has joined the server."
                    );
                }
                else if (SettingsTab.Settings.AlertOnJoin || SettingsTab.Settings.ToastNotifications)
                {
                    if (SettingsTab.Settings.AudioAlerts)
                    {
                        SoundNotificationService.PlayAlert(SoundAlertType.PlayerJoined);
                    }
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
                if (string.IsNullOrWhiteSpace(player.Name)) return;

                var dedupeKey = $"{player.Id}_{player.Name.Trim()}";
                _activeJoinToasts.TryRemove(dedupeKey, out _);

                AppLogger.Info($"[DashboardViewModel] Player left: '{player.Name}' (ID: #{player.Id}, UID: {player.Uid}) [Watchlisted: {player.IsWatchlisted}]");

                PlayersTab.RemovePlayerFromList(player);
                OnlinePlayersCount = PlayersTab.Players.Count;

                if (player.IsWatchlisted && SettingsTab.Settings.AlertOnWatchlistLeave)
                {
                    SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                    ToastNotificationService.Instance.ShowWarning(
                        "Watchlist Alert",
                        $"Watchlisted player '{player.Name}' has left the server."
                    );
                }
                else if (SettingsTab.Settings.AlertOnLeave || SettingsTab.Settings.ToastNotifications)
                {
                    if (SettingsTab.Settings.AudioAlerts)
                    {
                        SoundNotificationService.PlayAlert(SoundAlertType.PlayerLeft);
                    }
                    ToastNotificationService.Instance.ShowToast(
                        "Player Disconnected",
                        $"{player.Name} left the server."
                    );
                }
            });
        });
    }

    private void OnPlayerKickedStream(object? sender, (string Name, int Id, string Reason) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                AppLogger.Info($"[DashboardViewModel] Live stream kick notice: '{e.Name}' (ID: #{e.Id}) - Reason: '{e.Reason}'");
                PlayersTab.RemovePlayerFromList(new PlayerModel { Id = e.Id, Name = e.Name });
                OnlinePlayersCount = PlayersTab.Players.Count;

                ToastNotificationService.Instance.ShowWarning(
                    "Player Kicked",
                    $"{e.Name} was kicked from the server (Reason: {e.Reason})."
                );
            });
        });
    }

    private void OnPlayerBannedStream(object? sender, (string Name, int Id, string Guid, string Reason) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                AppLogger.Info($"[DashboardViewModel] Live stream ban notice: '{e.Name}' (ID: #{e.Id}, GUID: {e.Guid}) - Reason: '{e.Reason}'");
                PlayersTab.RemovePlayerFromList(new PlayerModel { Id = e.Id, Name = e.Name, Guid = e.Guid, BattlEyeGuid = e.Guid });
                OnlinePlayersCount = PlayersTab.Players.Count;

                ToastNotificationService.Instance.ShowError(
                    "Player Banned",
                    $"{e.Name} was banned from the server (Reason: {e.Reason})."
                );
                _ = BansTab.RefreshBansAsync();
            });
        });
    }

    private void OnAdminConnectedStream(object? sender, (int AdminId, string Endpoint) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                AppLogger.Info($"[DashboardViewModel] Live stream RCon admin connect: Admin #{e.AdminId} ({e.Endpoint})");
                ToastNotificationService.Instance.ShowToast(
                    "RCon Admin Connected",
                    $"Administrator #{e.AdminId} ({e.Endpoint}) logged in to RCON.",
                    "ADMIN_CONNECTED"
                );

                if (IsBattlEyeProtocol)
                {
                    _ = Task.Run(async () =>
                    {
                        var admins = await _rconService.GetAdminsAsync();
                        Dispatcher.UIThread.Post(() => ConnectedAdminsCount = admins.Count);
                    });
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
                AppLogger.Warn($"[DashboardViewModel] Connection lost notice: '{reason}'. Halting timers and presenting reconnection dialog.");

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
                        AppLogger.Info("[DashboardViewModel] Reconnected successfully. Resuming refresh timer.");
                        IsConnected = true;
                        CloseDialog();
                        _timer.Start();
                        _ = RefreshAllAsync(forceBans: true);
                    },
                    onReturnToLogin: () =>
                    {
                        AppLogger.Info("[DashboardViewModel] Returning to LoginView from disconnect dialog.");
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

    [RelayCommand]
    public void OpenAdminsDialog()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[DashboardViewModel] Opening AdminsDialog...");
            ShowDialog(new AdminsDialogViewModel(_rconService, this));
        });
    }

    public void ShowDialog(ViewModelBase dialog)
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DashboardViewModel] Presenting modal dialog: {dialog.GetType().Name}");
            ActiveDialog = dialog;
            IsDialogVisible = true;
        });
    }

    [RelayCommand]
    public void CloseDialog()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DashboardViewModel] Closing modal dialog (Current: {ActiveDialog?.GetType().Name ?? "None"}).");
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
                AppLogger.Warn("[DashboardViewModel] Disconnect detected during timer tick.");
                OnConnectionLost(this, "Connection timed out (No packets received)");
                return;
            }

            RefreshCountdown--;
            if (RefreshCountdown <= 0)
            {
                RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
                AppLogger.Trace($"[DashboardViewModel] Auto-refresh triggered on interval tick ({RefreshCountdown}s).");
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
            if (!_rconService.IsConnected)
            {
                AppLogger.Trace("[DashboardViewModel] RefreshAllAsync skipped: Socket not connected.");
                return;
            }

            using var timing = AppLogger.Measure($"DashboardViewModel.RefreshAllAsync(ForceBans: {forceBans})");
            AppLogger.Debug($"[DashboardViewModel] Starting refresh cycle (Force Bans: {forceBans})...");

            await PlayersTab.RefreshPlayersAsync();
            OnlinePlayersCount = PlayersTab.Players.Count;

            if (forceBans || SettingsTab.Settings.AutoRefreshBans)
            {
                await BansTab.RefreshBansAsync();
                ActiveBansCount = BansTab.Bans.Count;
            }

            if (IsBattlEyeProtocol)
            {
                var admins = await _rconService.GetAdminsAsync();
                ConnectedAdminsCount = admins.Count;
            }

            await DatabaseTab.LoadDbAsync();

            AppLogger.Info($"[DashboardViewModel] Refresh cycle completed (Online: {OnlinePlayersCount}, Bans: {ActiveBansCount}, Admins: {ConnectedAdminsCount}).");
        });
    }

    partial void OnSearchQueryChanged(string value)
    {
        ExecuteSafe(() =>
        {
            using var timing = AppLogger.Measure($"DashboardViewModel.OnSearchQueryChanged('{value}')");
            PlayersTab.ApplyFilter(value, SearchType);
            BansTab.ApplyFilter(value, SearchType);
            DatabaseTab.ApplyFilter(value, SearchType);
            AppLogger.Trace($"[DashboardViewModel] Applied search query '{value}' ({SearchType}).");
        });
    }

    partial void OnSearchTypeChanged(string value)
    {
        ExecuteSafe(() =>
        {
            using var timing = AppLogger.Measure($"DashboardViewModel.OnSearchTypeChanged('{value}')");
            PlayersTab.ApplyFilter(SearchQuery, value);
            BansTab.ApplyFilter(SearchQuery, value);
            DatabaseTab.ApplyFilter(SearchQuery, value);
            AppLogger.Trace($"[DashboardViewModel] Switched search type to '{value}' with query '{SearchQuery}'.");
        });
    }

    [RelayCommand]
    public static void ToggleTheme()
    {
        LuminaThemeManager.ToggleThemeVariant();
        var currentVariant = Application.Current?.ActualThemeVariant?.ToString() ?? "Unknown";
        AppLogger.Info($"[DashboardViewModel] Toggled LuminaUI theme variant (Active: {currentVariant}).");
    }

    [RelayCommand]
    public void ToggleConsoleFullscreen()
    {
        ExecuteSafe(() =>
        {
            IsConsoleFullscreen = !IsConsoleFullscreen;
            ConsoleTab.IsFullscreen = IsConsoleFullscreen;
            AppLogger.Debug($"[DashboardViewModel] Console fullscreen toggled: {IsConsoleFullscreen}");
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
                AppLogger.Debug("[DashboardViewModel] Activated already detached console window.");
                return;
            }

            AppLogger.Info("[DashboardViewModel] Detaching console into separate window...");
            IsConsoleDetached = true;
            ConsoleTab.IsDetached = true;
            UpdateLayoutDimensions();

            _detachedConsoleWindow = new ConsoleWindow(ConsoleTab, () =>
            {
                AppLogger.Info("[DashboardViewModel] Console window closed. Docking back into main view.");
                IsConsoleDetached = false;
                ConsoleTab.IsDetached = false;
                _detachedConsoleWindow = null;
                UpdateLayoutDimensions();
            });

            if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
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
        AppLogger.Trace($"[DashboardViewModel] Updated layout dimensions (IsConsoleDetached: {IsConsoleDetached}).");
    }

    [RelayCommand]
    private Task<bool> DisconnectAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            AppLogger.Info("[DashboardViewModel] Operator initiated manual disconnection.");
            _timer.Stop();
            _rconService.ConnectionLost -= OnConnectionLost;
            _rconService.PlayerJoined -= OnPlayerJoined;
            _rconService.PlayerLeft -= OnPlayerLeft;
            _rconService.PlayerKickedStream -= OnPlayerKickedStream;
            _rconService.PlayerBannedStream -= OnPlayerBannedStream;
            _rconService.AdminConnectedStream -= OnAdminConnectedStream;
            _detachedConsoleWindow?.Close();
            _detachedConsoleWindow = null;
            await _rconService.DisconnectAsync();
            _onDisconnectRequested();
        });
    }
}