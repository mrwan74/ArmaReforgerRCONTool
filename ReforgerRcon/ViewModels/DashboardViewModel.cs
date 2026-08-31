using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
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

        _ = Task.Run(RefreshInitialConnectAsync);
    }

    private async Task RefreshInitialConnectAsync()
    {
        try
        {
            AppLogger.Info($"[DashboardViewModel] Initializing parallel sync on connect for {Profile.ServerIp}:{Profile.Port} ({Profile.Protocol})...");
            var playersTask = PlayersTab.RefreshPlayersAsync();
            var bansTask = SettingsTab.Settings.AutoRefreshBans ? BansTab.RefreshBansAsync() : Task.FromResult(true);
            var adminsTask = IsBattlEyeProtocol ? _rconService.GetAdminsAsync() : Task.FromResult(new List<AdminModel>());
            var dbTask = DatabaseTab.LoadDbAsync();

            await Task.WhenAll(playersTask, bansTask, adminsTask, dbTask).ConfigureAwait(false);

            Dispatcher.UIThread.Post(() =>
            {
                OnlinePlayersCount = PlayersTab.Players.Count;
                ActiveBansCount = BansTab.Bans.Count;
                if (IsBattlEyeProtocol && adminsTask.IsCompletedSuccessfully)
                {
                    ConnectedAdminsCount = adminsTask.Result.Count;
                }
                AppLogger.Info($"[DashboardViewModel] Initial sync completed: OnlinePlayers={OnlinePlayersCount}, Bans={ActiveBansCount}, Admins={ConnectedAdminsCount}");
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[DashboardViewModel] Initial connection parallel refresh error: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowWarning("Sync Notice", "Some initial server statistics could not be loaded immediately.");
        }
    }

    private void OnPlayerJoined(object? sender, PlayerModel player)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                if (string.IsNullOrWhiteSpace(player.Name))
                {
                    AppLogger.Warn($"[DashboardViewModel] Received PlayerJoined event with empty player name for ID #{player.Id}.");
                    return;
                }

                AppLogger.Debug($"[DashboardViewModel] Processing PlayerJoined event: Name='{player.Name}', ID=#{player.Id}, UID='{player.Uid}', GUID='{player.Guid}', Location='{player.DisplayLocation}', Watchlisted={player.IsWatchlisted}");

                var dedupeKey = $"{player.Id}_{player.Name.Trim()}";
                var now = Stopwatch.GetTimestamp();

                if (_activeJoinToasts.TryGetValue(dedupeKey, out var lastDispatched))
                {
                    var elapsedSec = Stopwatch.GetElapsedTime(lastDispatched).TotalSeconds;
                    if (elapsedSec < 6.0)
                    {
                        PlayersTab.AddOrUpdatePlayer(player);
                        OnlinePlayersCount = PlayersTab.Players.Count;
                        AppLogger.Trace($"[DashboardViewModel] Suppressed duplicate join alert for '{player.Name}' (Elapsed: {elapsedSec:F1}s).");
                        return;
                    }
                }

                _activeJoinToasts[dedupeKey] = now;

                PlayersTab.AddOrUpdatePlayer(player);
                OnlinePlayersCount = PlayersTab.Players.Count;

                var settings = SettingsTab.Settings;

                if (player.IsWatchlisted)
                {
                    if (settings.AlertOnWatchlistJoin)
                    {
                        AppLogger.Info($"[DashboardViewModel] Watchlist Join Alert triggered for '{player.Name}' (Audio: {settings.AudioAlerts}, Toast: {settings.ToastNotifications}, Push: {settings.PushNotifications})");

                        if (settings.AudioAlerts)
                        {
                            SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                        }

                        if (settings.ToastNotifications)
                        {
                            ToastNotificationService.Instance.ShowWarning(
                                "Watchlist Alert",
                                $"Watchlisted player '{player.Name}' has joined the server."
                            );
                        }

                        if (settings.PushNotifications)
                        {
                            PushNotificationService.SendWatchlistNotification(player.Name, isJoining: true, player.DisplayLocation);
                        }
                    }
                }
                else
                {
                    if (settings.AlertOnJoin)
                    {
                        AppLogger.Info($"[DashboardViewModel] Player Join Alert triggered for '{player.Name}' (Audio: {settings.AudioAlerts}, Toast: {settings.ToastNotifications}, Push: {settings.PushNotifications})");

                        if (settings.AudioAlerts)
                        {
                            SoundNotificationService.PlayAlert(SoundAlertType.PlayerJoined);
                        }

                        if (settings.ToastNotifications)
                        {
                            ToastNotificationService.Instance.ShowToast(
                                "Player Connected",
                                $"{player.Name} joined the server."
                            );
                        }

                        if (settings.PushNotifications)
                        {
                            PushNotificationService.SendPlayerJoinNotification(player.Name, player.DisplayLocation);
                        }
                    }
                    else if (settings.ToastNotifications)
                    {
                        ToastNotificationService.Instance.ShowToast(
                            "Player Connected",
                            $"{player.Name} joined the server."
                        );
                    }
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
                if (string.IsNullOrWhiteSpace(player.Name))
                {
                    AppLogger.Warn($"[DashboardViewModel] Received PlayerLeft event with empty player name for ID #{player.Id}.");
                    return;
                }

                AppLogger.Debug($"[DashboardViewModel] Processing PlayerLeft event: Name='{player.Name}', ID=#{player.Id}, UID='{player.Uid}', Watchlisted={player.IsWatchlisted}");

                var dedupeKey = $"{player.Id}_{player.Name.Trim()}";
                _activeJoinToasts.TryRemove(dedupeKey, out _);

                PlayersTab.RemovePlayerFromList(player);
                OnlinePlayersCount = PlayersTab.Players.Count;

                var settings = SettingsTab.Settings;

                if (player.IsWatchlisted)
                {
                    if (settings.AlertOnWatchlistLeave)
                    {
                        AppLogger.Info($"[DashboardViewModel] Watchlist Leave Alert triggered for '{player.Name}' (Audio: {settings.AudioAlerts}, Toast: {settings.ToastNotifications}, Push: {settings.PushNotifications})");

                        if (settings.AudioAlerts)
                        {
                            SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                        }

                        if (settings.ToastNotifications)
                        {
                            ToastNotificationService.Instance.ShowWarning(
                                "Watchlist Alert",
                                $"Watchlisted player '{player.Name}' has left the server."
                            );
                        }

                        if (settings.PushNotifications)
                        {
                            PushNotificationService.SendWatchlistNotification(player.Name, isJoining: false);
                        }
                    }
                }
                else
                {
                    if (settings.AlertOnLeave)
                    {
                        AppLogger.Info($"[DashboardViewModel] Player Leave Alert triggered for '{player.Name}' (Audio: {settings.AudioAlerts}, Toast: {settings.ToastNotifications}, Push: {settings.PushNotifications})");

                        if (settings.AudioAlerts)
                        {
                            SoundNotificationService.PlayAlert(SoundAlertType.PlayerLeft);
                        }

                        if (settings.ToastNotifications)
                        {
                            ToastNotificationService.Instance.ShowToast(
                                "Player Disconnected",
                                $"{player.Name} left the server."
                            );
                        }

                        if (settings.PushNotifications)
                        {
                            PushNotificationService.SendPlayerLeaveNotification(player.Name);
                        }
                    }
                    else if (settings.ToastNotifications)
                    {
                        ToastNotificationService.Instance.ShowToast(
                            "Player Disconnected",
                            $"{player.Name} left the server."
                        );
                    }
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
                AppLogger.Info($"[DashboardViewModel] Moderation event received: Player Kicked '{e.Name}' (ID: #{e.Id}, Reason: '{e.Reason}')");
                PlayersTab.RemovePlayerFromList(new PlayerModel { Id = e.Id, Name = e.Name });
                OnlinePlayersCount = PlayersTab.Players.Count;

                if (SettingsTab.Settings.ToastNotifications)
                {
                    ToastNotificationService.Instance.ShowWarning(
                        "Player Kicked",
                        $"{e.Name} was kicked from the server (Reason: {e.Reason})."
                    );
                }

                if (SettingsTab.Settings.PushNotifications)
                {
                    PushNotificationService.SendNotification("Player Kicked", $"{e.Name} was kicked from the server (Reason: {e.Reason}).", "system");
                }
            });
        });
    }

    private void OnPlayerBannedStream(object? sender, (string Name, int Id, string Guid, string Reason) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                AppLogger.Info($"[DashboardViewModel] Moderation event received: Player Banned '{e.Name}' (ID: #{e.Id}, GUID: '{e.Guid}', Reason: '{e.Reason}')");
                PlayersTab.RemovePlayerFromList(new PlayerModel { Id = e.Id, Name = e.Name, Guid = e.Guid, BattlEyeGuid = e.Guid });
                OnlinePlayersCount = PlayersTab.Players.Count;

                if (SettingsTab.Settings.ToastNotifications)
                {
                    ToastNotificationService.Instance.ShowError(
                        "Player Banned",
                        $"{e.Name} was banned from the server (Reason: {e.Reason})."
                    );
                }

                if (SettingsTab.Settings.PushNotifications)
                {
                    PushNotificationService.SendNotification("Player Banned", $"{e.Name} was banned from the server (Reason: {e.Reason}).", "system");
                }

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
                AppLogger.Info($"[DashboardViewModel] Admin connected event received: Admin #{e.AdminId} from {e.Endpoint}");
                if (IsBattlEyeProtocol)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var admins = await _rconService.GetAdminsAsync().ConfigureAwait(false);
                            Dispatcher.UIThread.Post(() => ConnectedAdminsCount = admins.Count);
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error($"[DashboardViewModel] Error refreshing admin list on stream event: {ex.Message}", ex);
                        }
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
                AppLogger.Fatal($"[DashboardViewModel] Connection lost notice received: '{reason}' for {Profile.ServerIp}:{Profile.Port}");
                _timer.Stop();
                IsConnected = false;
                IsHeartbeatVisible = false;
                OnlinePlayersCount = 0;

                foreach (var player in PlayersTab.Players)
                {
                    player.Ping = 0;
                }

                if (SettingsTab.Settings.AudioAlerts)
                {
                    SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);
                }

                if (SettingsTab.Settings.PushNotifications)
                {
                    PushNotificationService.SendNotification("Server Connection Lost", $"RCON disconnected from {Profile.ServerIp}:{Profile.Port}.", "system");
                }

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

    [RelayCommand]
    public void OpenAdminsDialog()
    {
        ExecuteSafe(() =>
        {
            AppLogger.TrackEvent("dialog_opened", new Dictionary<string, object> { ["dialog"] = "AdminsDialog" });
            ShowDialog(new AdminsDialogViewModel(_rconService, this));
        });
    }

    public void ShowDialog(ViewModelBase dialog)
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DashboardViewModel] Displaying dialog overlay: {dialog.GetType().Name}");
            ActiveDialog = dialog;
            IsDialogVisible = true;
        });
    }

    [RelayCommand]
    public void CloseDialog()
    {
        ExecuteSafe(() =>
        {
            if (ActiveDialog != null)
            {
                AppLogger.Debug($"[DashboardViewModel] Dismissing active dialog overlay: {ActiveDialog.GetType().Name}");
            }
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
                AppLogger.Warn("[DashboardViewModel] Timer detected socket disconnect state.");
                OnConnectionLost(this, "Connection timed out (No packets received)");
                return;
            }

            RefreshCountdown--;
            if (RefreshCountdown <= 0)
            {
                RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
                await RefreshAllAsync(forceBans: false).ConfigureAwait(false);
            }

            RefreshProgress = (double)RefreshCountdown / Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds) * 100;
            var diff = (DateTime.UtcNow - _rconService.LastPacketTime).TotalSeconds;
            LastPacketTimerText = $"{(int)diff}s ago";
            Ping = _rconService.PingMs;
            IsHeartbeatVisible = !IsHeartbeatVisible;
        }).ConfigureAwait(false);
    }

    public Task<bool> RefreshAllAsync(bool forceBans = false)
    {
        return ExecuteSafeAsync(async () =>
        {
            if (!_rconService.IsConnected)
            {
                AppLogger.Trace("[DashboardViewModel] RefreshAllAsync skipped: RCON service is not connected.");
                return;
            }

            using var timing = AppLogger.Measure($"DashboardViewModel.RefreshAllAsync(ForceBans: {forceBans})");

            var pTask = PlayersTab.RefreshPlayersAsync();
            var bTask = (forceBans || SettingsTab.Settings.AutoRefreshBans) ? BansTab.RefreshBansAsync() : Task.FromResult(true);
            var aTask = IsBattlEyeProtocol ? _rconService.GetAdminsAsync() : Task.FromResult(new List<AdminModel>());
            var dTask = DatabaseTab.LoadDbAsync();

            await Task.WhenAll(pTask, bTask, aTask, dTask).ConfigureAwait(false);

            Dispatcher.UIThread.Post(() =>
            {
                OnlinePlayersCount = PlayersTab.Players.Count;
                ActiveBansCount = BansTab.Bans.Count;
                if (IsBattlEyeProtocol && aTask.IsCompletedSuccessfully)
                {
                    ConnectedAdminsCount = aTask.Result.Count;
                }
            });
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
    public static void ToggleTheme()
    {
        LuminaThemeManager.ToggleThemeVariant();
        var currentActual = Application.Current?.ActualThemeVariant;
        var newMode = currentActual == ThemeVariant.Dark ? "Dark" : "Light";

        var settings = AppSettings.LoadFromDisk();
        settings.ThemeMode = newMode;
        AppSettings.SaveToDisk(settings);

        AppLogger.Info($"[DashboardViewModel] Theme variant switched to '{newMode}'.");
        AppLogger.TrackEvent("theme_toggled", new Dictionary<string, object>
        {
            ["theme_mode"] = newMode
        });
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
                return;
            }

            IsConsoleDetached = true;
            ConsoleTab.IsDetached = true;
            UpdateLayoutDimensions();

            AppLogger.Info("[DashboardViewModel] Console detached into standalone window.");
            AppLogger.TrackEvent("console_detached");

            _detachedConsoleWindow = new ConsoleWindow(ConsoleTab, () =>
            {
                IsConsoleDetached = false;
                ConsoleTab.IsDetached = false;
                _detachedConsoleWindow = null;
                UpdateLayoutDimensions();
                AppLogger.Info("[DashboardViewModel] Standalone console window closed and re-docked into main dashboard.");
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
            AppLogger.Info("[DashboardViewModel] Reattach console command invoked.");
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
            AppLogger.Info("[DashboardViewModel] Operator initiated clean disconnect...");
            _timer.Stop();
            _rconService.ConnectionLost -= OnConnectionLost;
            _rconService.PlayerJoined -= OnPlayerJoined;
            _rconService.PlayerLeft -= OnPlayerLeft;
            _rconService.PlayerKickedStream -= OnPlayerKickedStream;
            _rconService.PlayerBannedStream -= OnPlayerBannedStream;
            _rconService.AdminConnectedStream -= OnAdminConnectedStream;
            _detachedConsoleWindow?.Close();
            _detachedConsoleWindow = null;
            await _rconService.DisconnectAsync().ConfigureAwait(false);
            _onDisconnectRequested();
        });
    }
}