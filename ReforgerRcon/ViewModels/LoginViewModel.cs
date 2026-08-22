using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Theming;
using Material.Icons;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class LoginViewModel : ViewModelBase
{
    private readonly Action<ServerProfile, IRconService> _onLoginSuccess;
    private readonly bool _isStartup;
    private bool _isSyncingProfile;

    [SuppressMessage("Security", "S1313:Hardcoded IP address", Justification = "Default localhost placeholder configuration")]
    [ObservableProperty] public partial string ServerIp { get; set; } = "127.0.0.1";
    [ObservableProperty] public partial int Port { get; set; } = 19999;
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

    public bool IsReforgerProtocol => Protocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => Protocol == RconProtocol.BattlEye;

    public char PasswordMaskChar => IsPasswordRevealed ? '\0' : '•';
    public MaterialIconKind PasswordIconKind => IsPasswordRevealed ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public AsyncRelayCommand<ServerProfile?> SaveProfileChangesCommand { get; }
    public AsyncRelayCommand SaveCurrentAsNewProfileCommand { get; }
    public AsyncRelayCommand SaveCurrentProfileCommand { get; }
    public RelayCommand<ServerProfile?> StartEditProfileNameCommand { get; }
    public AsyncRelayCommand<ServerProfile?> ConfirmEditProfileNameCommand { get; }
    public RelayCommand<ServerProfile?> CancelEditProfileNameCommand { get; }
    public AsyncRelayCommand<ServerProfile?> DeleteProfileCommand { get; }

    public LoginViewModel(Action<ServerProfile, IRconService> onLoginSuccess, bool isStartup = false)
    {
        _onLoginSuccess = onLoginSuccess;
        _isStartup = isStartup;

        SaveProfileChangesCommand = new AsyncRelayCommand<ServerProfile?>(SaveProfileChangesAsync);
        SaveCurrentAsNewProfileCommand = new AsyncRelayCommand(SaveCurrentAsNewProfileAsync);
        SaveCurrentProfileCommand = new AsyncRelayCommand(SaveCurrentProfileAsync);
        StartEditProfileNameCommand = new RelayCommand<ServerProfile?>(StartEditProfileName);
        ConfirmEditProfileNameCommand = new AsyncRelayCommand<ServerProfile?>(ConfirmEditProfileNameAsync);
        CancelEditProfileNameCommand = new RelayCommand<ServerProfile?>(CancelEditProfileName);
        DeleteProfileCommand = new AsyncRelayCommand<ServerProfile?>(DeleteProfileAsync);

        _ = LoadProfilesAsync();
    }

    private Task<bool> LoadProfilesAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Info("[LoginViewModel] Loading stored server profiles from disk...");
        List<ServerProfile> list = await ProfileStorageService.LoadProfilesAsync();

        // Enforce single auto-connect profile exclusivity
        var autoConnectProfiles = list.Where(p => p.AutoConnect).ToList();
        if (autoConnectProfiles.Count > 1)
        {
            AppLogger.Warn($"[LoginViewModel] Multiple profiles ({autoConnectProfiles.Count}) had AutoConnect enabled. Retaining only '{autoConnectProfiles[0].Name}'.");
            foreach (var p in autoConnectProfiles.Skip(1))
            {
                p.AutoConnect = false;
            }
            await ProfileStorageService.SaveProfilesAsync(list);
        }

        Profiles = new ObservableCollection<ServerProfile>(list);

        if (Profiles.Count > 0)
        {
            var targetProfile = Profiles.FirstOrDefault(p => p.IsLastSelected)
                             ?? Profiles.FirstOrDefault(p => p.AutoConnect)
                             ?? Profiles[0];

            SelectedProfile = targetProfile;
        }
        else
        {
            SelectedProfile = null;
        }

        // Trigger automatic connection only on initial startup if configured
        var autoConnectTarget = Profiles.FirstOrDefault(p => p.AutoConnect);
        if (_isStartup && autoConnectTarget is not null && !string.IsNullOrWhiteSpace(autoConnectTarget.ServerIp))
        {
            AppLogger.Info($"[LoginViewModel] Initial startup: Auto-connect scheduled for '{autoConnectTarget.Name}' ({autoConnectTarget.ServerIp}:{autoConnectTarget.Port}).");
            _ = Task.Run(async () =>
            {
                await Task.Delay(350);
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (SelectedProfile == autoConnectTarget && !IsConnecting)
                    {
                        await ConnectAsync();
                    }
                });
            });
        }
    }, "Failed to load saved server profiles.");

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        ExecuteSafe(() =>
        {
            if (value is null) return;
            AppLogger.Debug($"[LoginViewModel] Server profile selection changed to: '{value.Name}' ({value.ServerIp}:{value.Port}, {value.Protocol}) [AutoConnect: {value.AutoConnect}]");

            _isSyncingProfile = true;
            try
            {
                ServerIp = value.ServerIp;
                Port = value.Port;
                Password = value.Password;
                Protocol = value.Protocol;
                AutoConnect = value.AutoConnect;

                foreach (var p in Profiles)
                {
                    p.IsLastSelected = (p == value);
                }

                OnPropertyChanged(nameof(IsReforgerProtocol));
                OnPropertyChanged(nameof(IsBattlEyeProtocol));
            }
            finally
            {
                _isSyncingProfile = false;
            }

            _ = ProfileStorageService.SaveProfilesAsync([.. Profiles]);
        });
    }

    private void SyncCurrentFormToSelectedProfile()
    {
        if (_isSyncingProfile || SelectedProfile is null) return;

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

        _ = ProfileStorageService.SaveProfilesAsync([.. Profiles]);
    }

    partial void OnServerIpChanged(string value) => SyncCurrentFormToSelectedProfile();

    partial void OnPortChanged(int value) => SyncCurrentFormToSelectedProfile();

    partial void OnPasswordChanged(string value) => SyncCurrentFormToSelectedProfile();

    partial void OnProtocolChanged(RconProtocol value)
    {
        ExecuteSafe(() =>
        {
            AppLogger.Info($"[LoginViewModel] RCON Protocol switched to: {value}");
            OnPropertyChanged(nameof(IsReforgerProtocol));
            OnPropertyChanged(nameof(IsBattlEyeProtocol));

            if (value == RconProtocol.BattlEye && (Port == 19999 || Port <= 0))
            {
                Port = 20007;
            }
            else if (value == RconProtocol.ReforgerBuiltIn && (Port == 20007 || Port <= 0))
            {
                Port = 19999;
            }

            SyncCurrentFormToSelectedProfile();
        });
    }

    partial void OnAutoConnectChanged(bool value)
    {
        ExecuteSafe(() =>
        {
            if (_isSyncingProfile) return;

            AppLogger.Info($"[LoginViewModel] AutoConnect checkbox changed to: {value} on '{SelectedProfile?.Name ?? "Active Form"}'");
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
            AppLogger.Info("[LoginViewModel] Opening RCON Protocol Guidance modal.");
            ActiveDialog = new ProtocolHelpDialogViewModel(CloseDialog);
            IsDialogVisible = true;
        });
    }

    [RelayCommand]
    public void CloseDialog()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[LoginViewModel] Closing modal dialog.");
            IsDialogVisible = false;
            ActiveDialog = null;
        });
    }

    [RelayCommand]
    public void TogglePasswordReveal() => ExecuteSafe(() => IsPasswordRevealed = !IsPasswordRevealed);

    [RelayCommand]
    public static void ToggleTheme() => LuminaThemeManager.ToggleThemeVariant();

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
            AppLogger.Debug($"[LoginViewModel] Inline edit started for profile '{profile.Name}'.");
        });
    }

    public Task ConfirmEditProfileNameAsync(ServerProfile? profile) => ExecuteSafeAsync(async () =>
    {
        profile ??= SelectedProfile;
        if (profile is null) return;

        var oldName = profile.Name;
        if (!string.IsNullOrWhiteSpace(profile.EditNameBuffer))
        {
            profile.Name = profile.EditNameBuffer.Trim();
        }

        profile.IsEditing = false;
        await ProfileStorageService.SaveProfilesAsync([.. Profiles]);
        AppLogger.Info($"[LoginViewModel] Renamed profile '{oldName}' to '{profile.Name}'.");
        ToastNotificationService.Instance.ShowToast("Profile Renamed", $"Renamed profile to '{profile.Name}'.");
    }, "Failed to rename profile.");

    public void CancelEditProfileName(ServerProfile? profile)
    {
        ExecuteSafe(() =>
        {
            profile ??= SelectedProfile;
            if (profile is null) return;
            profile.IsEditing = false;
            AppLogger.Debug($"[LoginViewModel] Inline rename cancelled for '{profile.Name}'.");
        });
    }

    public Task SaveProfileChangesAsync(ServerProfile? profile) => ExecuteSafeAsync(async () =>
    {
        profile ??= SelectedProfile;
        if (profile is null)
        {
            await SaveCurrentAsNewProfileAsync();
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

        await ProfileStorageService.SaveProfilesAsync([.. Profiles]);
        AppLogger.Info($"[LoginViewModel] Saved updated parameters to profile '{profile.Name}' ({profile.ServerIp}:{profile.Port}) [AutoConnect: {profile.AutoConnect}].");
        ToastNotificationService.Instance.ShowToast("Profile Saved", $"Saved changes to '{profile.Name}'.");
    }, "Failed to update profile settings.");

    public Task SaveCurrentAsNewProfileAsync() => ExecuteSafeAsync(async () =>
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

        Profiles.Add(newProfile);
        SelectedProfile = newProfile;
        NewProfileName = string.Empty;

        await ProfileStorageService.SaveProfilesAsync([.. Profiles]);
        AppLogger.Info($"[LoginViewModel] Created new profile '{name}' ({newProfile.ServerIp}:{newProfile.Port}) [AutoConnect: {newProfile.AutoConnect}].");
        ToastNotificationService.Instance.ShowToast("New Profile Added", $"Created server profile '{name}'.");
    }, "Failed to save new server profile.");

    public Task SaveCurrentProfileAsync() => SelectedProfile is not null
        ? SaveProfileChangesAsync(SelectedProfile)
        : SaveCurrentAsNewProfileAsync();

    public Task DeleteProfileAsync(ServerProfile? profile) => ExecuteSafeAsync(async () =>
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

        await ProfileStorageService.SaveProfilesAsync([.. Profiles]);
        AppLogger.Info($"[LoginViewModel] Deleted profile '{name}'.");
        ToastNotificationService.Instance.ShowToast("Profile Deleted", $"Removed '{name}'.");
    }, "Failed to delete profile.");

    [RelayCommand]
    public static void TriggerTestCrash()
    {
        AppLogger.Info("Selected Server Connection Profile: 'Reforger Dedicated (Local)'");
        AppLogger.Debug("Connecting UDP Socket to endpoint: 127.0.0.1:19999...");
        AppLogger.Trace("Outgoing Login Packet (20 bytes): 424500000000FF0064656D6F5F70617373776F7264");
        AppLogger.Warn("Handshake timeout after 5000ms: No acknowledge received from remote endpoint.");
        AppLogger.Fatal("Fatal connection breakdown: UDP socket timed out and host connection lost.");

        Task.Run(() =>
        {
            var innerSocketEx = new SocketException((int)SocketError.TimedOut);
            throw new TimeoutException(
                "A connection attempt failed because the connected party did not properly respond after a period of time, " +
                "or established connection failed because connected host has failed to respond (127.0.0.1:19999).",
                innerSocketEx
            );
        }).ContinueWith(t =>
        {
            if (t.Exception != null)
            {
                var ex = t.Exception.InnerException ?? t.Exception;
                CrashReportService.HandleFatalException("NetworkService.HandshakeWorker", ex, isTerminating: false);
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await ExecuteSafeAsync(async () =>
        {
            IsConnecting = true;
            ErrorMessage = string.Empty;

            // Ensure current active form values are written to SelectedProfile and persisted to disk
            if (SelectedProfile is not null)
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

                await ProfileStorageService.SaveProfilesAsync([.. Profiles]);
            }
            else
            {
                // If no profile was selected, automatically create and persist a profile for this endpoint
                var newProfile = new ServerProfile
                {
                    Name = $"Server {ServerIp.Trim()}:{Port}",
                    ServerIp = ServerIp.Trim(),
                    Port = Port,
                    Password = Password,
                    Protocol = Protocol,
                    AutoConnect = AutoConnect,
                    IsLastSelected = true
                };

                Profiles.Add(newProfile);
                SelectedProfile = newProfile;
                await ProfileStorageService.SaveProfilesAsync([.. Profiles]);
            }

            var profile = new ServerProfile
            {
                Name = SelectedProfile?.Name ?? "Direct Connection",
                ServerIp = ServerIp.Trim(),
                Port = Port,
                Password = Password,
                Protocol = Protocol,
                AutoConnect = AutoConnect
            };

            AppLogger.Info($"[LoginViewModel] Connecting to server {profile.ServerIp}:{profile.Port} ({profile.Protocol}) [AutoConnect: {profile.AutoConnect}]...");
            var rconService = new RconService();
            var success = await rconService.ConnectAsync(profile);

            if (success)
            {
                AppLogger.Info("[LoginViewModel] Live connection verified successfully.");
                _onLoginSuccess(profile, rconService);
            }
            else
            {
                ErrorMessage = "Failed to connect to server. Verify server IP, RCON port, and password.";
                AppLogger.Warn($"[LoginViewModel] Failed establishing connection to {profile.ServerIp}:{profile.Port}");
            }
        });
        IsConnecting = false;
    }

    [SuppressMessage("SonarQube", "S2068:Hardcoded credentials", Justification = "Simulated offline demo parameters")]
    [RelayCommand]
    private async Task LaunchDemoModeAsync()
    {
        await ExecuteSafeAsync(async () =>
        {
            IsConnecting = true;
            ErrorMessage = string.Empty;

            var profile = new ServerProfile
            {
                Name = "Demo Server Simulation",
                ServerIp = "127.0.0.1",
                Port = Protocol == RconProtocol.ReforgerBuiltIn ? 19999 : 20007,
                Password = string.Empty,
                Protocol = Protocol,
                AutoConnect = false
            };

            AppLogger.Info($"[LoginViewModel] Launching demo simulation mode for {profile.Protocol}...");
            var mockService = new MockRconService();
            await mockService.ConnectAsync(profile);
            _onLoginSuccess(profile, mockService);
        });
        IsConnecting = false;
    }
}