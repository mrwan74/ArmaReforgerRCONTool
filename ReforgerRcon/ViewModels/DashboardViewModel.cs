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

    // Skeleton loading properties
    [ObservableProperty] public partial bool IsOnlinePlayersLoading { get; set; } = true;
    [ObservableProperty] public partial bool IsActiveBansLoading { get; set; } = true;
    [ObservableProperty] public partial bool IsAdminsLoading { get; set; } = true;

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

    private SettingsViewModel? _settingsTab;
    private BansViewModel? _bansTab;
    private DatabaseViewModel? _databaseTab;

    public PlayersViewModel PlayersTab { get; }
    public ConsoleViewModel ConsoleTab { get; }

    public SettingsViewModel SettingsTab => _settingsTab ??= new SettingsViewModel(this);
    public BansViewModel BansTab => _bansTab ??= new BansViewModel(_rconService, this);
    public DatabaseViewModel DatabaseTab => _databaseTab ??= new DatabaseViewModel(_rconService, this);

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
        AppLogger.Debug("[DashboardViewModel:Init] Constructing active tab ViewModels for session...", context);

        PlayersTab = new PlayersViewModel(_rconService, this);
        ConsoleTab = new ConsoleViewModel(_rconService, this);

        RefreshCountdown = Math.Max(1, AppSettings.LoadFromDisk().RefreshIntervalSeconds);

        _rconService.ConnectionLost += OnConnectionLost;
        _rconService.PlayerJoined += OnPlayerJoined;
        _rconService.PlayerLeft += OnPlayerLeft;
        _rconService.PlayerKickedStream += OnPlayerKickedStream;
        _rconService.PlayerBannedStream += OnPlayerBannedStream;
        _rconService.AdminConnectedStream += OnAdminConnectedStream;
        _rconService.ProtocolMismatchDetected += OnProtocolMismatchDetected;

        UpdateService.Instance.UpdateFound += OnUpdateFound;
        UpdateService.BeforeRestartAsync += OnBeforeRestartAsync;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[DashboardViewModel:Init] Dashboard initialized in {elapsedMs:F2}ms for {profile.ServerIp}:{profile.Port} ({profile.Protocol}).", context);
    }

    public void Initialize()
    {
        AppLogger.Debug("[DashboardViewModel:Initialize] Executing post-construction initialization...");
        if (_rconService.DetectedProtocolMismatch.HasValue)
        {
            AppLogger.Warn($"[DashboardViewModel:Initialize] Initial protocol mismatch detected: {_rconService.DetectedProtocolMismatch.Value}");
            HandleProtocolMismatch(_rconService.DetectedProtocolMismatch.Value);
        }

        if (UpdateService.Instance.CurrentStatus == UpdateStatus.UpdateAvailable &&
            UpdateService.Instance.TargetVersionString != null)
        {
            OnUpdateFound(UpdateService.Instance.CurrentVersionString, UpdateService.Instance.TargetVersionString);
        }

        _ = Task.Run(RefreshInitialConnectAsync);
    }

    private void OnUpdateFound(string currentVer, string targetVer)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ToastNotificationService.Instance.ShowToast(
                "Update Available",
                $"ARRT v{targetVer} is available."
            );

            if (!IsDialogVisible && ActiveDialog == null)
            {
                ShowDialog(new UpdateDialogViewModel(currentVer, targetVer, CloseDialog));
            }
        });
    }

    private Task<bool> OnBeforeRestartAsync()
    {
        AppLogger.Info("[DashboardViewModel:Update] Disconnecting active RCON session before applying update...");
        return DisconnectAsync();
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsOnlinePlayersLoading = true;
                IsActiveBansLoading = true;
                IsAdminsLoading = IsBattlEyeProtocol;
            });

            await PlayersTab.RefreshPlayersAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnlinePlayersCount = PlayersTab.Players.Count;
                IsOnlinePlayersLoading = false;
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(50).ConfigureAwait(false);

                    if (SettingsTab.Settings.AutoRefreshBans)
                    {
                        await BansTab.RefreshBansAsync().ConfigureAwait(false);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            ActiveBansCount = BansTab.Bans.Count;
                            IsActiveBansLoading = false;
                        });
                    }
                    else
                    {
                        await Dispatcher.UIThread.InvokeAsync(() => IsActiveBansLoading = false);
                    }

                    if (IsBattlEyeProtocol)
                    {
                        var admins = await _rconService.GetAdminsAsync().ConfigureAwait(false);
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            ConnectedAdminsCount = admins.Count;
                            IsAdminsLoading = false;
                        });
                    }
                }
                catch (Exception bgEx)
                {
                    AppLogger.Warn($"[DashboardViewModel:InitSync] Background tab sync notice: {bgEx.Message}", bgEx);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsActiveBansLoading = false;
                        IsAdminsLoading = false;
                    });
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
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsOnlinePlayersLoading = false;
                IsActiveBansLoading = false;
                IsAdminsLoading = false;
            });
        }
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
                        PlayersTab.AddOrUpdatePlayer(player);
                        OnlinePlayersCount = PlayersTab.Players.Count;
                        IsOnlinePlayersLoading = false;
                        return;
                    }
                }

                _activeJoinToasts[dedupeKey] = now;
                PlayersTab.AddOrUpdatePlayer(player);
                OnlinePlayersCount = PlayersTab.Players.Count;
                IsOnlinePlayersLoading = false;

                var settings = SettingsTab.Settings;

                if (player.IsWatchlisted)
                {
                    if (settings.AlertOnWatchlistJoin)
                    {
                        if (settings.AudioAlerts) SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                        if (settings.ToastNotifications) ToastNotificationService.Instance.ShowWarning("Watchlist Alert", $"Watchlisted player '{player.Name}' has joined the server.");
                        if (settings.PushNotifications) PushNotificationService.SendWatchlistNotification(player.Name, isJoining: true, player.DisplayLocation);
                    }
                }
                else
                {
                    if (settings.AlertOnJoin)
                    {
                        if (settings.AudioAlerts) SoundNotificationService.PlayAlert(SoundAlertType.PlayerJoined);
                        if (settings.ToastNotifications) ToastNotificationService.Instance.ShowToast("Player Connected", $"{player.Name} joined the server.");
                        if (settings.PushNotifications) PushNotificationService.SendPlayerJoinNotification(player.Name, player.DisplayLocation);
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
                if (string.IsNullOrWhiteSpace(player.Name)) return;

                var dedupeKey = $"{player.Id}_{player.Name.Trim()}";
                _activeJoinToasts.TryRemove(dedupeKey, out _);

                PlayersTab.RemovePlayerFromList(player);
                OnlinePlayersCount = PlayersTab.Players.Count;
                IsOnlinePlayersLoading = false;

                var settings = SettingsTab.Settings;

                if (player.IsWatchlisted)
                {
                    if (settings.AlertOnWatchlistLeave)
                    {
                        if (settings.AudioAlerts) SoundNotificationService.PlayAlert(SoundAlertType.WatchlistAlert);
                        if (settings.ToastNotifications) ToastNotificationService.Instance.ShowWarning("Watchlist Alert", $"Watchlisted player '{player.Name}' has left the server.");
                        if (settings.PushNotifications) PushNotificationService.SendWatchlistNotification(player.Name, isJoining: false);
                    }
                }
                else
                {
                    if (settings.AlertOnLeave)
                    {
                        if (settings.AudioAlerts) SoundNotificationService.PlayAlert(SoundAlertType.PlayerLeft);
                        if (settings.ToastNotifications) ToastNotificationService.Instance.ShowToast("Player Disconnected", $"{player.Name} left the server.");
                        if (settings.PushNotifications) PushNotificationService.SendPlayerLeaveNotification(player.Name);
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
                PlayersTab.RemovePlayerFromList(new PlayerModel { Id = e.Id, Name = e.Name });
                OnlinePlayersCount = PlayersTab.Players.Count;
                IsOnlinePlayersLoading = false;

                if (SettingsTab.Settings.ToastNotifications)
                    ToastNotificationService.Instance.ShowWarning("Player Kicked", $"{e.Name} was kicked from the server (Reason: {e.Reason}).");

                if (SettingsTab.Settings.PushNotifications)
                    PushNotificationService.SendNotification("Player Kicked", $"{e.Name} was kicked from the server (Reason: {e.Reason}).", "system");
            });
        });
    }

    private void OnPlayerBannedStream(object? sender, (string Name, int Id, string Guid, string Reason) e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ExecuteSafe(() =>
            {
                PlayersTab.RemovePlayerFromList(new PlayerModel { Id = e.Id, Name = e.Name, Guid = e.Guid, BattlEyeGuid = e.Guid });
                OnlinePlayersCount = PlayersTab.Players.Count;
                IsOnlinePlayersLoading = false;

                if (SettingsTab.Settings.ToastNotifications)
                    ToastNotificationService.Instance.ShowError("Player Banned", $"{e.Name} was banned from the server (Reason: {e.Reason}).");

                if (SettingsTab.Settings.PushNotifications)
                    PushNotificationService.SendNotification("Player Banned", $"{e.Name} was banned from the server (Reason: {e.Reason}).", "system");

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
                if (IsBattlEyeProtocol)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var admins = await _rconService.GetAdminsAsync().ConfigureAwait(false);
                            Dispatcher.UIThread.Post(() =>
                            {
                                ConnectedAdminsCount = admins.Count;
                                IsAdminsLoading = false;
                            });
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
                _timer.Stop();
                IsConnected = false;
                IsHeartbeatVisible = false;
                OnlinePlayersCount = 0;
                IsOnlinePlayersLoading = false;
                IsActiveBansLoading = false;
                IsAdminsLoading = false;

                foreach (var player in PlayersTab.Players)
                {
                    player.Ping = 0;
                }

                if (SettingsTab.Settings.AudioAlerts)
                    SoundNotificationService.PlayAlert(SoundAlertType.CriticalError);

                if (SettingsTab.Settings.PushNotifications)
                    PushNotificationService.SendNotification("Server Connection Lost", $"RCON disconnected from {Profile.ServerIp}:{Profile.Port}.", "system");

                ShowDialog(new ConnectionLostDialogViewModel(
                    Profile,
                    reason,
                    _rconService,
                    onReconnected: () =>
                    {
                        IsConnected = true;
                        CloseDialog();
                        _timer.Start();
                        _ = RefreshAllInternalAsync(forceBans: true);
                    },
                    onReturnToLogin: () =>
                    {
                        CloseDialog();
                        _detachedConsoleWindow?.Close();
                        _detachedConsoleWindow = null;
                        Dispose();
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
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowDialog(dialog));
            return;
        }

        ExecuteSafe(() =>
        {
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
                    RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
                    await RefreshAllInternalAsync(forceBans: false).ConfigureAwait(false);
                }

                RefreshProgress = (double)RefreshCountdown / Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds) * 100;
            }

            var diff = (DateTime.UtcNow - _rconService.LastPacketTime).TotalSeconds;
            LastPacketTimerText = $"{(int)diff}s ago";
            Ping = _rconService.PingMs;
            IsHeartbeatVisible = !IsHeartbeatVisible;
        }, userFriendlyErrorMessage: null, trackCloudTelemetry: false).ConfigureAwait(false);
    }

    [RelayCommand]
    public Task<bool> RefreshAllAsync()
    {
        RefreshCountdown = Math.Max(1, SettingsTab.Settings.RefreshIntervalSeconds);
        RefreshProgress = 100;
        return RefreshAllInternalAsync(forceBans: true);
    }

    public Task<bool> RefreshAllInternalAsync(bool forceBans = false)
    {
        return ExecuteSafeAsync(async () =>
        {
            if (!_rconService.IsConnected) return;

            var start = Stopwatch.GetTimestamp();
            var context = new Dictionary<string, object?>
            {
                ["force_bans"] = forceBans,
                [ProtocolTelemetryKey] = Profile.Protocol.ToString()
            };

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsOnlinePlayersLoading = true;
                if (forceBans || SettingsTab.Settings.AutoRefreshBans) IsActiveBansLoading = true;
                if (IsBattlEyeProtocol) IsAdminsLoading = true;
            });

            await PlayersTab.RefreshPlayersAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                OnlinePlayersCount = PlayersTab.Players.Count;
                IsOnlinePlayersLoading = false;
            });

            if (forceBans || SettingsTab.Settings.AutoRefreshBans)
            {
                await BansTab.RefreshBansAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ActiveBansCount = BansTab.Bans.Count;
                    IsActiveBansLoading = false;
                });
            }

            if (IsBattlEyeProtocol)
            {
                var admins = await _rconService.GetAdminsAsync().ConfigureAwait(false);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ConnectedAdminsCount = admins.Count;
                    IsAdminsLoading = false;
                });
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
            PlayersTab.ApplyFilter(value, SearchType);
            _bansTab?.ApplyFilter(value, SearchType);
            _databaseTab?.ApplyFilter(value, SearchType);
        });
    }

    partial void OnSearchTypeChanged(string value)
    {
        ExecuteSafe(() =>
        {
            PlayersTab.ApplyFilter(SearchQuery, value);
            _bansTab?.ApplyFilter(SearchQuery, value);
            _databaseTab?.ApplyFilter(SearchQuery, value);
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
                _detachedConsoleWindow?.Activate();
                return;
            }

            IsConsoleDetached = true;
            ConsoleTab.IsDetached = true;
            _consoleDetachedStartTimestamp = Stopwatch.GetTimestamp();
            UpdateLayoutDimensions();

            _detachedConsoleWindow = new ConsoleWindow(ConsoleTab, () =>
            {
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
    }

    [RelayCommand]
    private Task<bool> DisconnectAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            var start = Stopwatch.GetTimestamp();
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
                _timer.Stop();
                _timer.Tick -= OnTimerTick;

                UpdateService.Instance.UpdateFound -= OnUpdateFound;
                UpdateService.BeforeRestartAsync -= OnBeforeRestartAsync;

                _rconService.ConnectionLost -= OnConnectionLost;
                _rconService.PlayerJoined -= OnPlayerJoined;
                _rconService.PlayerLeft -= OnPlayerLeft;
                _rconService.PlayerKickedStream -= OnPlayerKickedStream;
                _rconService.PlayerBannedStream -= OnPlayerBannedStream;
                _rconService.AdminConnectedStream -= OnAdminConnectedStream;
                _rconService.ProtocolMismatchDetected -= OnProtocolMismatchDetected;

                _databaseTab?.Dispose();

                if (_detachedConsoleWindow != null)
                {
                    try
                    {
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