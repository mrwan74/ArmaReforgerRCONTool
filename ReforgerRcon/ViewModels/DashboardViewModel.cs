using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
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

[SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "Partial callback methods are invoked by CommunityToolkit.Mvvm generated property setters")]
public partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private const string ProtocolTelemetryKey = "protocol";

    private readonly IRconService _rconService;
    private readonly Action _onDisconnectRequested;
    private readonly Func<ServerProfile, RconProtocol, Task>? _onSwitchProtocolRequested;
    private readonly DispatcherTimer _timer;
    private readonly ConcurrentDictionary<string, long> _activeJoinToasts = new(StringComparer.OrdinalIgnoreCase);
    private ConsoleWindow? _detachedConsoleWindow;
    private long _consoleDetachedStartTimestamp;
    private bool _hasPromptedProtocolMismatch;
    private bool _isDisposed;

    [ObservableProperty] public partial ServerProfile Profile { get; set; }
    [ObservableProperty] public partial int OnlinePlayersCount { get; set; }
    [ObservableProperty] public partial int ActiveBansCount { get; set; }
    [ObservableProperty] public partial int ConnectedAdminsCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshCountdownText))]
    public partial int RefreshCountdown { get; set; }

    [ObservableProperty] public partial double RefreshProgress { get; set; } = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshCountdownText))]
    public partial bool IsAutoRefreshEnabled { get; set; } = true;

    public string RefreshCountdownText => IsAutoRefreshEnabled
        ? $"Refresh in {RefreshCountdown}s"
        : "Auto-refresh paused";

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

    public DashboardViewModel(
        ServerProfile profile,
        IRconService rconService,
        Action onDisconnectRequested,
        Func<ServerProfile, RconProtocol, Task>? onSwitchProtocolRequested = null)
    {
        var start = Stopwatch.GetTimestamp();
        Profile = profile;
        _rconService = rconService;
        _onDisconnectRequested = onDisconnectRequested;
        _onSwitchProtocolRequested = onSwitchProtocolRequested;

        var context = new Dictionary<string, object?>
        {
            ["host"] = profile.ServerIp,
            ["port"] = profile.Port,
            [ProtocolTelemetryKey] = profile.Protocol.ToString()
        };
        AppLogger.Debug("[DashboardViewModel:Init] Constructing child tab ViewModels for session...", context);

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
        _rconService.ProtocolMismatchDetected += OnProtocolMismatchDetected;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[DashboardViewModel:Init] Dashboard initialized in {elapsedMs:F2}ms for {profile.ServerIp}:{profile.Port} ({profile.Protocol}). TimerInterval=1s, RefreshPeriod={RefreshCountdown}s.", context);
    }

    public void Initialize()
    {
        AppLogger.Debug("[DashboardViewModel:Initialize] Executing post-construction initialization...");
        if (_rconService.DetectedProtocolMismatch.HasValue)
        {
            AppLogger.Warn($"[DashboardViewModel:Initialize] Initial protocol mismatch detected: {_rconService.DetectedProtocolMismatch.Value}");
            HandleProtocolMismatch(_rconService.DetectedProtocolMismatch.Value);
        }

        _ = Task.Run(RefreshInitialConnectAsync);
    }

    partial void OnIsAutoRefreshEnabledChanged(bool value)
    {
        AppLogger.Debug($"[DashboardViewModel:AutoRefresh] Auto-refresh state mutated: {value}");
        if (ConsoleTab != null && ConsoleTab.IsAutoRefreshEnabled != value)
        {
            ConsoleTab.IsAutoRefreshEnabled = value;
        }

        if (value)
        {
            RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
            RefreshProgress = 100;
        }
    }

    private void OnProtocolMismatchDetected(object? sender, RconProtocol detectedProtocol) =>
        Dispatcher.UIThread.Post(() => ExecuteSafe(() => HandleProtocolMismatch(detectedProtocol)));

    public void HandleProtocolMismatch(RconProtocol detectedProtocol)
    {
        if (_hasPromptedProtocolMismatch || IsDialogVisible)
        {
            AppLogger.Trace("[DashboardViewModel:ProtocolMismatch] Prompt suppressed: already prompted or dialog currently active.");
            return;
        }
        _hasPromptedProtocolMismatch = true;

        var detectedName = detectedProtocol == RconProtocol.ReforgerBuiltIn ? "Reforger Built-in RCON" : "BattlEye RCON";
        var currentName = Profile.Protocol == RconProtocol.ReforgerBuiltIn ? "Reforger Built-in RCON" : "BattlEye RCON";

        AppLogger.Warn($"[DashboardViewModel:ProtocolMismatch] Displaying protocol switch prompt: Current='{currentName}', Detected='{detectedName}'.");

        ShowDialog(new ConfirmDialogViewModel(
            "Protocol Mismatch Detected",
            $"The server at {Profile.ServerIp}:{Profile.Port} responded with {detectedName} format, but ARRT is currently connected using {currentName}.\n\nWould you like to switch to {detectedName} and reconnect automatically?",
            $"Switch to {detectedName}",
            false,
            async () =>
            {
                AppLogger.Info($"[DashboardViewModel:ProtocolMismatch] User accepted automatic protocol switch to '{detectedProtocol}'.");
                AppLogger.TrackEvent("rcon_protocol_mismatch_decision", new Dictionary<string, object>
                {
                    ["configured_protocol"] = Profile.Protocol.ToString(),
                    ["detected_protocol"] = detectedProtocol.ToString(),
                    ["auto_switch_accepted"] = true
                });

                CloseDialog();
                await DisconnectAsync().ConfigureAwait(false);

                if (_onSwitchProtocolRequested != null)
                {
                    await _onSwitchProtocolRequested(Profile, detectedProtocol).ConfigureAwait(false);
                }
            },
            () =>
            {
                AppLogger.Info("[DashboardViewModel:ProtocolMismatch] User declined automatic protocol switch.");
                AppLogger.TrackEvent("rcon_protocol_mismatch_decision", new Dictionary<string, object>
                {
                    ["configured_protocol"] = Profile.Protocol.ToString(),
                    ["detected_protocol"] = detectedProtocol.ToString(),
                    ["auto_switch_accepted"] = false
                });

                CloseDialog();
                _hasPromptedProtocolMismatch = false;
            }
        ));
    }

    private async Task RefreshInitialConnectAsync()
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?> { [ProtocolTelemetryKey] = Profile.Protocol.ToString() };
        AppLogger.Debug("[DashboardViewModel:InitSync] Executing initial background synchronization...", context);

        try
        {
            await PlayersTab.RefreshPlayersAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => OnlinePlayersCount = PlayersTab.Players.Count);
            AppLogger.Debug($"[DashboardViewModel:InitSync] Live players tab populated: {PlayersTab.Players.Count} players online.");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (SettingsTab.Settings.AutoRefreshBans)
                    {
                        AppLogger.Trace("[DashboardViewModel:InitSync] Waiting 1200ms before initial ban refresh...");
                        await Task.Delay(1200).ConfigureAwait(false);
                        await BansTab.RefreshBansAsync().ConfigureAwait(false);
                        Dispatcher.UIThread.Post(() => ActiveBansCount = BansTab.Bans.Count);
                        AppLogger.Debug($"[DashboardViewModel:InitSync] Bans tab synchronized: {BansTab.Bans.Count} active bans.");
                    }

                    if (IsBattlEyeProtocol)
                    {
                        await Task.Delay(200).ConfigureAwait(false);
                        var admins = await _rconService.GetAdminsAsync().ConfigureAwait(false);
                        Dispatcher.UIThread.Post(() => ConnectedAdminsCount = admins.Count);
                        AppLogger.Debug($"[DashboardViewModel:InitSync] Admins list synchronized: {admins.Count} connected admins.");
                    }
                }
                catch (Exception bgEx)
                {
                    AppLogger.Warn($"[DashboardViewModel:InitSync] Background tab sync notice: {bgEx.Message}", bgEx);
                }
            });

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[DashboardViewModel:InitSync] Initial player list sync finalized in {elapsedMs:F2}ms (OnlineCount={OnlinePlayersCount}).", context);
        }
        catch (Exception ex)
        {
            context["error_message"] = ex.Message;
            AppLogger.Error("[DashboardViewModel:InitSync] Error during initial connection sync: " + ex.Message, ex, context);
            ToastNotificationService.Instance.ShowError("Sync Error", $"Failed initial server synchronization: {ex.Message}");
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
                    AppLogger.Trace("[DashboardViewModel:OnPlayerJoined] Ignored player with empty name.");
                    return;
                }

                var dedupeKey = $"{player.Id}_{player.Name.Trim()}";
                var now = Stopwatch.GetTimestamp();

                if (_activeJoinToasts.TryGetValue(dedupeKey, out var lastDispatched))
                {
                    var elapsedSec = Stopwatch.GetElapsedTime(lastDispatched).TotalSeconds;
                    if (elapsedSec < 6.0)
                    {
                        AppLogger.Trace($"[DashboardViewModel:OnPlayerJoined] Debounced rapid duplicate alert for '{player.Name}' ({elapsedSec:F1}s).");
                        PlayersTab.AddOrUpdatePlayer(player);
                        OnlinePlayersCount = PlayersTab.Players.Count;
                        return;
                    }
                }

                _activeJoinToasts[dedupeKey] = now;
                PlayersTab.AddOrUpdatePlayer(player);
                OnlinePlayersCount = PlayersTab.Players.Count;

                var settings = SettingsTab.Settings;
                AppLogger.Debug($"[DashboardViewModel:OnPlayerJoined] Handling join event: Name='{player.Name}', Watchlisted={player.IsWatchlisted}, AudioAlerts={settings.AudioAlerts}, ToastAlerts={settings.ToastNotifications}.");

                if (player.IsWatchlisted)
                {
                    if (settings.AlertOnWatchlistJoin)
                    {
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
                    AppLogger.Trace("[DashboardViewModel:OnPlayerLeft] Ignored player with empty name.");
                    return;
                }

                var dedupeKey = $"{player.Id}_{player.Name.Trim()}";
                _activeJoinToasts.TryRemove(dedupeKey, out _);

                PlayersTab.RemovePlayerFromList(player);
                OnlinePlayersCount = PlayersTab.Players.Count;

                var settings = SettingsTab.Settings;
                AppLogger.Debug($"[DashboardViewModel:OnPlayerLeft] Handling leave event: Name='{player.Name}', Watchlisted={player.IsWatchlisted}.");

                if (player.IsWatchlisted)
                {
                    if (settings.AlertOnWatchlistLeave)
                    {
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
                AppLogger.Info($"[DashboardViewModel:Stream] Player kicked stream notification: '{e.Name}' (ID: #{e.Id}, Reason: '{e.Reason}').");
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
                AppLogger.Info($"[DashboardViewModel:Stream] Player banned stream notification: '{e.Name}' (ID: #{e.Id}, GUID: '{e.Guid}', Reason: '{e.Reason}').");
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
                AppLogger.Info($"[DashboardViewModel:Stream] Admin connected stream notification: Admin #{e.AdminId} from {e.Endpoint}.");
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
                            AppLogger.Error($"[DashboardViewModel:Stream] Error refreshing admins on stream event: {ex.Message}", ex);
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
                AppLogger.Warn($"[DashboardViewModel:ConnectionLost] Remote connection lost. Reason: '{reason}'");
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
                        AppLogger.Info("[DashboardViewModel:ConnectionLost] Reconnection successful. Resuming dashboard timer...");
                        IsConnected = true;
                        CloseDialog();
                        _timer.Start();
                        _ = RefreshAllInternalAsync(forceBans: true);
                    },
                    onReturnToLogin: () =>
                    {
                        AppLogger.Info("[DashboardViewModel:ConnectionLost] User selected return to login screen.");
                        CloseDialog();
                        _detachedConsoleWindow?.Close();
                        _detachedConsoleWindow = null;
                        Dispose();
                        _onDisconnectRequested();
                    },
                    onDismiss: () =>
                    {
                        AppLogger.Debug("[DashboardViewModel:ConnectionLost] Connection lost dialog dismissed by user.");
                        CloseDialog();
                    }
                ));
            });
        });
    }

    [RelayCommand]
    public void OpenAdminsDialog()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info("[DashboardViewModel:Dialog] Opening connected RCON admins dialog...");
            AppLogger.TrackEvent("dialog_opened", new Dictionary<string, object> { ["dialog"] = "AdminsDialog" });
            ShowDialog(new AdminsDialogViewModel(_rconService, this));
        });
    }

    public void ShowDialog(ViewModelBase dialog)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowDialog(dialog));
            return;
        }

        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DashboardViewModel:Dialog] Presenting dialog: {dialog.GetType().Name}.");
            ActiveDialog = dialog;
            IsDialogVisible = true;
        });
    }

    [RelayCommand]
    public void CloseDialog()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(CloseDialog);
            return;
        }

        ExecuteSafe(() =>
        {
            AppLogger.Debug("[DashboardViewModel:Dialog] Closing active dialog.");
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

            if (IsAutoRefreshEnabled)
            {
                RefreshCountdown--;
                if (RefreshCountdown <= 0)
                {
                    AppLogger.Debug("[DashboardViewModel:Timer] Auto-refresh countdown expired. Triggering periodic refresh...");
                    RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
                    await RefreshAllInternalAsync(forceBans: false).ConfigureAwait(false);
                }

                RefreshProgress = (double)RefreshCountdown / Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds) * 100;
            }

            var diff = (DateTime.UtcNow - _rconService.LastPacketTime).TotalSeconds;
            LastPacketTimerText = $"{(int)diff}s ago";
            Ping = _rconService.PingMs;
            IsHeartbeatVisible = !IsHeartbeatVisible;
        }).ConfigureAwait(false);
    }

    [RelayCommand]
    public Task<bool> RefreshAllAsync()
    {
        AppLogger.Info("[DashboardViewModel:ManualRefresh] Manual refresh requested by user.");
        RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
        RefreshProgress = 100;
        return RefreshAllInternalAsync(forceBans: true);
    }

    public Task<bool> RefreshAllInternalAsync(bool forceBans = false)
    {
        return ExecuteSafeAsync(async () =>
        {
            if (!_rconService.IsConnected)
            {
                AppLogger.Warn("[DashboardViewModel:RefreshAll] Refresh aborted: RconService is not connected.");
                return;
            }

            var start = Stopwatch.GetTimestamp();
            var context = new Dictionary<string, object?>
            {
                ["force_bans"] = forceBans,
                [ProtocolTelemetryKey] = Profile.Protocol.ToString()
            };
            using var timing = AppLogger.Measure($"DashboardViewModel.RefreshAllAsync(ForceBans: {forceBans})");

            AppLogger.Debug("[DashboardViewModel:RefreshAll] Refreshing active players tab...", context);
            await PlayersTab.RefreshPlayersAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => OnlinePlayersCount = PlayersTab.Players.Count);

            if (forceBans || SettingsTab.Settings.AutoRefreshBans)
            {
                AppLogger.Debug("[DashboardViewModel:RefreshAll] Refreshing bans tab...", context);
                await BansTab.RefreshBansAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ActiveBansCount = BansTab.Bans.Count);
            }

            if (IsBattlEyeProtocol)
            {
                AppLogger.Debug("[DashboardViewModel:RefreshAll] Refreshing connected admins list...", context);
                var admins = await _rconService.GetAdminsAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() => ConnectedAdminsCount = admins.Count);
            }

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["duration_ms"] = elapsedMs;
            AppLogger.Info($"[DashboardViewModel:RefreshAll] Global refresh cycle completed in {elapsedMs:F2}ms (Players={OnlinePlayersCount}, Bans={ActiveBansCount}).", context);
        });
    }

    partial void OnSearchQueryChanged(string value)
    {
        ExecuteSafe(() =>
        {
            var sanitized = AppLogger.SanitizeSensitiveData(value);
            AppLogger.Trace($"[DashboardViewModel:Search] Search query changed to '{sanitized}' (Type='{SearchType}').");
            PlayersTab.ApplyFilter(value, SearchType);
            BansTab.ApplyFilter(value, SearchType);
            DatabaseTab.ApplyFilter(value, SearchType);
        });
    }

    partial void OnSearchTypeChanged(string value)
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[DashboardViewModel:Search] Search target type switched to '{value}'.");
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

        AppLogger.Info($"[DashboardViewModel:Theme] Theme toggled to: {newMode}");
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
            AppLogger.Info($"[DashboardViewModel:Layout] Console fullscreen toggled: {IsConsoleFullscreen}");
            UpdateLayoutDimensions();
        });
    }

    [RelayCommand]
    public void DetachConsole()
    {
        ExecuteSafe(() =>
        {
            if (IsConsoleDetached)
            {
                AppLogger.Debug("[DashboardViewModel:Layout] Activating already detached console window.");
                _detachedConsoleWindow?.Activate();
                return;
            }

            IsConsoleDetached = true;
            ConsoleTab.IsDetached = true;
            _consoleDetachedStartTimestamp = Stopwatch.GetTimestamp();
            UpdateLayoutDimensions();

            AppLogger.Info("[DashboardViewModel:Layout] Detaching console to standalone window.");
            AppLogger.TrackEvent("console_detached", new Dictionary<string, object>
            {
                [ProtocolTelemetryKey] = Profile.Protocol.ToString()
            });

            _detachedConsoleWindow = new ConsoleWindow(ConsoleTab, () =>
            {
                AppLogger.Info("[DashboardViewModel:Layout] Reattaching console window via closing event callback.");
                var durationSec = _consoleDetachedStartTimestamp > 0
                    ? (int)Stopwatch.GetElapsedTime(_consoleDetachedStartTimestamp).TotalSeconds
                    : 0;

                AppLogger.TrackEvent("console_reattached", new Dictionary<string, object>
                {
                    [ProtocolTelemetryKey] = Profile.Protocol.ToString(),
                    ["duration_detached_seconds"] = durationSec
                });

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
            AppLogger.Info("[DashboardViewModel:Layout] User manually requested console reattachment.");
            var durationSec = _consoleDetachedStartTimestamp > 0
                ? (int)Stopwatch.GetElapsedTime(_consoleDetachedStartTimestamp).TotalSeconds
                : 0;

            AppLogger.TrackEvent("console_reattached", new Dictionary<string, object>
            {
                [ProtocolTelemetryKey] = Profile.Protocol.ToString(),
                ["duration_detached_seconds"] = durationSec
            });

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
        else if (IsConsoleFullscreen)
        {
            TabsRowHeight = new GridLength(0, GridUnitType.Pixel);
            SplitterRowHeight = new GridLength(0, GridUnitType.Pixel);
            ConsoleRowHeight = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            TabsRowHeight = new GridLength(3, GridUnitType.Star);
            SplitterRowHeight = new GridLength(8, GridUnitType.Pixel);
            ConsoleRowHeight = new GridLength(2, GridUnitType.Star);
        }
        AppLogger.Trace($"[DashboardViewModel:Layout] Layout row dimensions updated (Detached={IsConsoleDetached}, Fullscreen={IsConsoleFullscreen}).");
    }

    [RelayCommand]
    private Task<bool> DisconnectAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            var start = Stopwatch.GetTimestamp();
            AppLogger.Info($"[DashboardViewModel:Disconnect] Manual disconnection requested by administrator for {Profile.ServerIp}:{Profile.Port}...");
            Dispose();
            await _rconService.DisconnectAsync().ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[DashboardViewModel:Disconnect] Disconnection complete in {elapsedMs:F2}ms. Transitioning to login view.");
            _onDisconnectRequested();
        });
    }

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
                AppLogger.Debug("[DashboardViewModel:Dispose] Disposing timer, subscriptions, and sub-tabs...");
                _timer.Stop();
                _timer.Tick -= OnTimerTick;

                _rconService.ConnectionLost -= OnConnectionLost;
                _rconService.PlayerJoined -= OnPlayerJoined;
                _rconService.PlayerLeft -= OnPlayerLeft;
                _rconService.PlayerKickedStream -= OnPlayerKickedStream;
                _rconService.PlayerBannedStream -= OnPlayerBannedStream;
                _rconService.AdminConnectedStream -= OnAdminConnectedStream;
                _rconService.ProtocolMismatchDetected -= OnProtocolMismatchDetected;

                DatabaseTab.Dispose();

                if (_detachedConsoleWindow != null)
                {
                    try
                    {
                        AppLogger.Trace("[DashboardViewModel:Dispose] Closing detached console window...");
                        _detachedConsoleWindow.Close();
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Trace($"[DashboardViewModel:Dispose] Notice closing detached console: {ex.Message}");
                    }
                    _detachedConsoleWindow = null;
                }
            }
            _isDisposed = true;
        }
    }
}