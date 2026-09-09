using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Theming;
using Material.Icons;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

[SuppressMessage("Major Code Smell", "S1144:Unused private types or members should be removed", Justification = "Partial callback methods are invoked by CommunityToolkit.Mvvm generated property setters")]
public partial class LoginViewModel : ViewModelBase, IDisposable
{
    public const int DefaultReforgerPort = 19999;

    private readonly Action<ServerProfile, IRconService> _onLoginSuccess;
    private readonly bool _isStartup;
    private bool _isSyncingProfile;
    private bool _isLoadingProfiles = true;
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _saveProfileDebounceCts;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private int _disposed;

    [SuppressMessage("Security", "S1313:Hardcoded IP address", Justification = "Default localhost placeholder configuration")]
    [ObservableProperty] public partial string ServerIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial string PortText { get; set; } = "19999";
    [ObservableProperty] public partial string Password { get; set; } = string.Empty;
    [ObservableProperty] public partial RconProtocol Protocol { get; set; } = RconProtocol.ReforgerBuiltIn;
    [ObservableProperty] public partial bool AutoConnect { get; set; }
    [ObservableProperty] public partial bool IsConnecting { get; set; }
    [ObservableProperty] public partial bool IsPasswordRevealed { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = string.Empty;
    [ObservableProperty] public partial ObservableCollection<ServerProfile> Profiles { get; set; } = [];
    [ObservableProperty] public partial ServerProfile? SelectedProfile { get; set; }
    [ObservableProperty] public partial string NewProfileName { get; set; } = string.Empty;

    [ObservableProperty] public partial ViewModelBase? ActiveDialog { get; set; }
    [ObservableProperty] public partial bool IsDialogVisible { get; set; }

    public int Port
    {
        get
        {
            if (int.TryParse(PortText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort) &&
                parsedPort is > 0 and <= 65535)
            {
                return parsedPort;
            }

            return Protocol == RconProtocol.ReforgerBuiltIn ? DefaultReforgerPort : 0;
        }
        set => PortText = value > 0 ? value.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    public bool IsReforgerProtocol => Protocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => Protocol == RconProtocol.BattlEye;

    public char PasswordMaskChar => IsPasswordRevealed ? '\0' : '•';
    public MaterialIconKind PasswordIconKind => IsPasswordRevealed ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public LoginViewModel(Action<ServerProfile, IRconService> onLoginSuccess, bool isStartup = false)
    {
        var start = Stopwatch.GetTimestamp();
        _onLoginSuccess = onLoginSuccess;
        _isStartup = isStartup;

        InitializeProfilesInstant();
        AppLogger.Trace($"[LoginViewModel:Init] Initialization finished in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
    }

    private void InitializeProfilesInstant()
    {
        var start = Stopwatch.GetTimestamp();
        _isLoadingProfiles = true;
        _isSyncingProfile = true;

        try
        {
            var list = ProfileStorageService.LoadProfilesFast();
            Profiles = new ObservableCollection<ServerProfile>(list);

            if (Profiles.Count > 0)
            {
                var targetProfile = Profiles.FirstOrDefault(p => p.IsLastSelected)
                                 ?? Profiles.FirstOrDefault(p => p.AutoConnect)
                                 ?? Profiles[0];

                ServerIp = targetProfile.ServerIp;
                PortText = targetProfile.Port > 0 ? targetProfile.Port.ToString(CultureInfo.InvariantCulture) : "19999";
                Password = targetProfile.Password;
                Protocol = targetProfile.Protocol;
                AutoConnect = targetProfile.AutoConnect;
                SelectedProfile = targetProfile;
                targetProfile.IsLastSelected = true;

                AppLogger.Info($"[LoginViewModel:Profiles] Selected profile: '{targetProfile.Name}' in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms (Port: {targetProfile.Port}).");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[LoginViewModel:Profiles] Error initializing profiles: {ex.Message}", ex);
        }
        finally
        {
            _isSyncingProfile = false;
            _isLoadingProfiles = false;
        }

        if (_isStartup)
        {
            ProcessStartupAutoConnectInstant();
        }
    }

    private void ProcessStartupAutoConnectInstant()
    {
        try
        {
            var currentSettings = AppSettings.LoadFromDisk();

            if (!currentSettings.HasPromptedTelemetry)
            {
                AppLogger.Info("[LoginViewModel:Telemetry] Telemetry consent dialog ready.");
                ActiveDialog = new TelemetryConsentDialogViewModel(enabled =>
                {
                    currentSettings.SendAnonymousCrashReports = enabled;
                    currentSettings.HasPromptedTelemetry = true;
                    AppSettings.SaveToDisk(currentSettings);
                    CloseDialog();

                    AppLogger.TrackEvent("telemetry_consent_decision", new Dictionary<string, object>
                    {
                        ["enabled"] = enabled
                    });

                    if (enabled)
                    {
                        ToastNotificationService.Instance.ShowToast(
                            "Telemetry Active",
                            "You can toggle this on/off anytime in Settings."
                        );
                    }
                });
                IsDialogVisible = true;
            }
            else
            {
                var autoConnectTarget = Profiles.FirstOrDefault(p => p.AutoConnect);
                if (autoConnectTarget is not null && !string.IsNullOrWhiteSpace(autoConnectTarget.ServerIp) &&
                    SelectedProfile == autoConnectTarget && !IsConnecting && !IsDialogVisible)
                {
                    AppLogger.Info($"[LoginViewModel:AutoConnect] Auto-connecting to '{autoConnectTarget.Name}' ({autoConnectTarget.ServerIp}:{autoConnectTarget.Port})...");
                    _ = ConnectInternalAsync(CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[LoginViewModel:AutoConnect] Error during startup profile evaluation: {ex.Message}", ex);
        }
    }

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        ExecuteSafe(() =>
        {
            if (value is null || _isLoadingProfiles) return;

            _isSyncingProfile = true;
            try
            {
                ServerIp = value.ServerIp;
                PortText = value.Port > 0 ? value.Port.ToString(CultureInfo.InvariantCulture) : string.Empty;
                Password = value.Password;
                Protocol = value.Protocol;
                AutoConnect = value.AutoConnect;

                foreach (var p in Profiles)
                {
                    p.IsLastSelected = (p == value);
                }

                _ = Task.Run(() => ProfileStorageService.SaveProfilesFast([.. Profiles]), CancellationToken.None);

                OnPropertyChanged(nameof(Port));
                OnPropertyChanged(nameof(IsReforgerProtocol));
                OnPropertyChanged(nameof(IsBattlEyeProtocol));
            }
            finally
            {
                _isSyncingProfile = false;
            }
        });
    }

    private void SyncCurrentFormToSelectedProfile()
    {
        if (_isSyncingProfile || _isLoadingProfiles || SelectedProfile is null) return;

        try
        {
            SelectedProfile.ServerIp = ServerIp.Trim();
            SelectedProfile.Port = Port;
            SelectedProfile.Password = Password;
            SelectedProfile.Protocol = Protocol;
            SelectedProfile.AutoConnect = AutoConnect;
            SelectedProfile.IsLastSelected = true;

            foreach (var p in Profiles.Where(p => p != SelectedProfile))
            {
                p.IsLastSelected = false;
                if (AutoConnect && p.AutoConnect)
                {
                    p.AutoConnect = false;
                }
            }

            _saveProfileDebounceCts?.Cancel();
            _saveProfileDebounceCts?.Dispose();
            _saveProfileDebounceCts = new CancellationTokenSource();
            var token = _saveProfileDebounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(400, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        ProfileStorageService.SaveProfilesFast([.. Profiles]);
                    }
                }
                catch (OperationCanceledException opEx)
                {
                    AppLogger.Trace($"[LoginViewModel:Sync] Debounce save canceled: {opEx.Message}");
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[LoginViewModel:Sync] Debounce save notice: {ex.Message}");
                }
            }, token);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[LoginViewModel:Sync] Form sync failed: {ex.Message}", ex);
        }
    }

    partial void OnServerIpChanged(string value) => SyncCurrentFormToSelectedProfile();

    partial void OnPortTextChanged(string value)
    {
        if (_isSyncingProfile || _isLoadingProfiles) return;

        if (string.IsNullOrEmpty(value))
        {
            OnPropertyChanged(nameof(Port));
            SyncCurrentFormToSelectedProfile();
            return;
        }

        var digitsOnly = new string(value.Where(char.IsDigit).Take(5).ToArray());
        if (!string.Equals(digitsOnly, value, StringComparison.Ordinal))
        {
            _isSyncingProfile = true;
            try
            {
                PortText = digitsOnly;
            }
            finally
            {
                _isSyncingProfile = false;
            }
        }

        OnPropertyChanged(nameof(Port));
        SyncCurrentFormToSelectedProfile();
    }

    partial void OnPasswordChanged(string value) => SyncCurrentFormToSelectedProfile();

    partial void OnProtocolChanged(RconProtocol value)
    {
        ExecuteSafe(() =>
        {
            if (_isLoadingProfiles) return;
            OnPropertyChanged(nameof(IsReforgerProtocol));
            OnPropertyChanged(nameof(IsBattlEyeProtocol));

            if ((string.IsNullOrWhiteSpace(PortText) || PortText == "0") && value == RconProtocol.ReforgerBuiltIn)
            {
                PortText = DefaultReforgerPort.ToString(CultureInfo.InvariantCulture);
            }

            SyncCurrentFormToSelectedProfile();
        });
    }

    partial void OnAutoConnectChanged(bool value)
    {
        ExecuteSafe(() =>
        {
            if (_isSyncingProfile || _isLoadingProfiles) return;
            SyncCurrentFormToSelectedProfile();
        });
    }

    partial void OnIsPasswordRevealedChanged(bool value)
    {
        ExecuteSafe(() =>
        {
            OnPropertyChanged(nameof(PasswordMaskChar));
            OnPropertyChanged(nameof(PasswordIconKind));
        });
    }

    [RelayCommand]
    public void OpenProtocolHelp()
    {
        ExecuteSafe(() =>
        {
            ActiveDialog = new ProtocolHelpDialogViewModel(CloseDialog);
            IsDialogVisible = true;
        });
    }

    [RelayCommand]
    public void CloseDialog()
    {
        ExecuteSafe(() =>
        {
            IsDialogVisible = false;
            ActiveDialog = null;
        });
    }

    [RelayCommand]
    public void TogglePasswordReveal() => ExecuteSafe(() => IsPasswordRevealed = !IsPasswordRevealed);

    [RelayCommand]
    public static void ToggleTheme()
    {
        LuminaThemeManager.ToggleThemeVariant();
    }

    [RelayCommand]
    private void SelectProtocol(string protocolName)
    {
        ExecuteSafe(() =>
        {
            Protocol = protocolName.Equals("BattlEye", StringComparison.OrdinalIgnoreCase)
                ? RconProtocol.BattlEye
                : RconProtocol.ReforgerBuiltIn;
        });
    }

    [RelayCommand]
    public void StartEditProfileName(ServerProfile? profile)
    {
        ExecuteSafe(() =>
        {
            profile ??= SelectedProfile;
            if (profile is null) return;

            foreach (var p in Profiles)
            {
                p.IsEditing = false;
            }

            profile.EditNameBuffer = profile.Name;
            profile.IsEditing = true;
        });
    }

    [RelayCommand]
    public Task<bool> ConfirmEditProfileNameAsync(ServerProfile? profile) => ExecuteSafeAsync(async () =>
    {
        profile ??= SelectedProfile;
        if (profile is null) return;

        if (!string.IsNullOrWhiteSpace(profile.EditNameBuffer))
        {
            profile.Name = profile.EditNameBuffer.Trim();
        }

        profile.IsEditing = false;
        await ProfileStorageService.SaveProfilesAsync([.. Profiles]).ConfigureAwait(false);
        ToastNotificationService.Instance.ShowToast("Profile Renamed", $"Renamed profile to '{profile.Name}'.");
    }, "Failed to rename profile.");

    [RelayCommand]
    public void CancelEditProfileName(ServerProfile? profile)
    {
        ExecuteSafe(() =>
        {
            profile ??= SelectedProfile;
            if (profile is null) return;
            profile.IsEditing = false;
        });
    }

    [RelayCommand]
    public Task<bool> SaveProfileChangesAsync(ServerProfile? profile) => ExecuteSafeAsync(async () =>
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            await SaveCurrentAsNewProfileAsync().ConfigureAwait(false);
            return;
        }

        profile.ServerIp = ServerIp.Trim();
        profile.Port = Port;
        profile.Password = Password;
        profile.Protocol = Protocol;
        profile.AutoConnect = AutoConnect;
        profile.IsLastSelected = true;

        foreach (var p in Profiles.Where(p => p != profile))
        {
            p.IsLastSelected = false;
            if (AutoConnect && p.AutoConnect)
            {
                p.AutoConnect = false;
            }
        }

        await ProfileStorageService.SaveProfilesAsync([.. Profiles]).ConfigureAwait(false);
        ToastNotificationService.Instance.ShowToast("Profile Saved", $"Saved changes to '{profile.Name}'.");
    }, "Failed to update profile settings.");

    [RelayCommand]
    public Task<bool> SaveCurrentAsNewProfileAsync() => ExecuteSafeAsync(async () =>
    {
        var name = string.IsNullOrWhiteSpace(NewProfileName)
            ? $"Server {ServerIp.Trim()}:{Port}"
            : NewProfileName.Trim();

        var newProfile = new ServerProfile
        {
            Name = name,
            ServerIp = ServerIp.Trim(),
            Port = Port,
            Password = Password,
            Protocol = Protocol,
            AutoConnect = AutoConnect,
            IsLastSelected = true
        };

        if (AutoConnect)
        {
            foreach (var p in Profiles.Where(p => p.AutoConnect))
            {
                p.AutoConnect = false;
            }
        }

        foreach (var p in Profiles)
        {
            p.IsLastSelected = false;
        }

        Profiles.Add(newProfile);
        SelectedProfile = newProfile;
        NewProfileName = string.Empty;

        await ProfileStorageService.SaveProfilesAsync([.. Profiles]).ConfigureAwait(false);
        ToastNotificationService.Instance.ShowToast("New Profile Added", $"Created server profile '{name}'.");
    }, "Failed to save new server profile.");

    [RelayCommand]
    public Task<bool> SaveCurrentProfileAsync() => SelectedProfile is not null
        ? SaveProfileChangesAsync(SelectedProfile)
        : SaveCurrentAsNewProfileAsync();

    [RelayCommand]
    public Task<bool> DeleteProfileAsync(ServerProfile? profile) => ExecuteSafeAsync(async () =>
    {
        profile ??= SelectedProfile;
        if (profile is null) return;

        var name = profile.Name;
        Profiles.Remove(profile);
        if (SelectedProfile == profile)
        {
            if (Profiles.Count > 0)
            {
                Profiles[0].IsLastSelected = true;
                SelectedProfile = Profiles[0];
            }
            else
            {
                SelectedProfile = null;
            }
        }

        await ProfileStorageService.SaveProfilesAsync([.. Profiles]).ConfigureAwait(false);
        ToastNotificationService.Instance.ShowToast("Profile Deleted", $"Removed '{name}'.");
    }, "Failed to delete profile.");

    [RelayCommand]
    public static void TriggerTestCrash()
    {
        AppLogger.Info("[LoginViewModel:TestCrash] Triggering synthetic diagnostic fault...");

        Task.Run(() =>
        {
            var innerSocketEx = new SocketException((int)SocketError.TimedOut);
            var testException = new TimeoutException(
                "A connection attempt failed because the connected party did not properly respond after a period of time (127.0.0.1:19999).",
                innerSocketEx
            );

            AppLogger.TrackError(testException, fatal: false);
            throw testException;
        }, CancellationToken.None).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                var ex = t.Exception.InnerException ?? t.Exception;
                CrashReportService.HandleFatalException("NetworkService.HandshakeWorker", ex, isTerminating: false);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    [RelayCommand]
    private Task ConnectAsync() => ConnectInternalAsync(CancellationToken.None);

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException ex)
        {
            AppLogger.Trace($"[LoginViewModel:Connect] Lock acquisition bypassed - instance disposed: {ex.Message}");
            return;
        }
        catch (OperationCanceledException ex)
        {
            AppLogger.Trace($"[LoginViewModel:Connect] Lock acquisition canceled: {ex.Message}");
            return;
        }

        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;

            IsConnecting = true;
            ErrorMessage = string.Empty;

            if (_connectCts != null)
            {
                try
                {
                    await _connectCts.CancelAsync().ConfigureAwait(false);
                    _connectCts.Dispose();
                }
                catch (Exception ex)
                {
                    AppLogger.Trace($"[LoginViewModel:Connect] CTS cleanup notice: {ex.Message}");
                }
            }

            _connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var ct = _connectCts.Token;

            await ExecuteSafeAsync(async () =>
            {
                var start = Stopwatch.GetTimestamp();

                var profile = new ServerProfile
                {
                    Name = SelectedProfile?.Name ?? $"Server {ServerIp.Trim()}:{Port}",
                    ServerIp = ServerIp.Trim(),
                    Port = Port,
                    Password = Password,
                    Protocol = Protocol,
                    AutoConnect = AutoConnect,
                    IsLastSelected = true
                };

                var rconService = new RconService();
                var success = await rconService.ConnectAsync(profile, ct).ConfigureAwait(false);

                ct.ThrowIfCancellationRequested();
                var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                if (success)
                {
                    if (SelectedProfile != null)
                    {
                        SelectedProfile.ServerIp = ServerIp.Trim();
                        SelectedProfile.Port = Port;
                        SelectedProfile.Password = Password;
                        SelectedProfile.Protocol = Protocol;
                        SelectedProfile.AutoConnect = AutoConnect;
                        SelectedProfile.IsLastSelected = true;

                        foreach (var p in Profiles.Where(p => p != SelectedProfile))
                        {
                            p.IsLastSelected = false;
                        }
                    }
                    else if (Profiles.Count > 0)
                    {
                        Profiles[0].ServerIp = ServerIp.Trim();
                        Profiles[0].Port = Port;
                        Profiles[0].Password = Password;
                        Profiles[0].Protocol = Protocol;
                        Profiles[0].AutoConnect = AutoConnect;
                        Profiles[0].IsLastSelected = true;
                        SelectedProfile = Profiles[0];
                    }
                    else
                    {
                        Profiles.Add(profile);
                        SelectedProfile = profile;
                    }

                    AppLogger.Info($"[LoginViewModel:Connect] Connected in {elapsedMs:F2}ms (Port: {profile.Port}, Protocol: {profile.Protocol}). Transitioning immediately...");

                    Dispatcher.UIThread.Post(() => _onLoginSuccess(profile, rconService));
                    _ = Task.Run(() => ProfileStorageService.SaveProfilesFast([.. Profiles]), CancellationToken.None);
                }
                else
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ErrorMessage = !string.IsNullOrWhiteSpace(rconService.LastConnectionError)
                            ? rconService.LastConnectionError
                            : "Failed to connect to server. Verify server IP, RCON port, and password.";
                    });
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            IsConnecting = false;

            try
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _connectLock.Release();
                }
            }
            catch (ObjectDisposedException ex)
            {
                AppLogger.Trace($"[LoginViewModel:Connect] Lock already disposed during release: {ex.Message}");
            }
            catch (SemaphoreFullException ex)
            {
                AppLogger.Trace($"[LoginViewModel:Connect] Lock already at maximum capacity: {ex.Message}");
            }
        }
    }

    [SuppressMessage("SonarQube", "S2068:Hardcoded credentials", Justification = "Simulated offline demo parameters")]
    [RelayCommand]
    private async Task LaunchDemoModeAsync()
    {
        await ExecuteSafeAsync(async () =>
        {
            var start = Stopwatch.GetTimestamp();
            IsConnecting = true;
            ErrorMessage = string.Empty;

            int demoPort = Port;
            if (demoPort <= 0)
            {
                demoPort = Protocol == RconProtocol.ReforgerBuiltIn ? DefaultReforgerPort : 20007;
            }

            var profile = new ServerProfile
            {
                Name = "Demo Server Simulation",
                ServerIp = "127.0.0.1",
                Port = demoPort,
                Password = string.Empty,
                Protocol = Protocol,
                AutoConnect = false
            };

            var mockService = new MockRconService();
            await mockService.ConnectAsync(profile, CancellationToken.None).ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[LoginViewModel:Demo] Demo mode active in {elapsedMs:F2}ms.");
            Dispatcher.UIThread.Post(() => _onLoginSuccess(profile, mockService));
        }).ConfigureAwait(false);
        IsConnecting = false;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (disposing)
        {
            try
            {
                _connectCts?.Cancel();
                _connectCts?.Dispose();
                _connectCts = null;

                _saveProfileDebounceCts?.Cancel();
                _saveProfileDebounceCts?.Dispose();
                _saveProfileDebounceCts = null;
            }
            catch (Exception ex)
            {
                AppLogger.Trace($"[LoginViewModel:Dispose] CTS notice: {ex.Message}");
            }
        }
    }
}