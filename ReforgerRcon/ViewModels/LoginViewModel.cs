using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
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

    public bool IsReforgerProtocol => Protocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => Protocol == RconProtocol.BattlEye;

    public char PasswordMaskChar => IsPasswordRevealed ? '\0' : '•';
    public MaterialIconKind PasswordIconKind => IsPasswordRevealed ? MaterialIconKind.EyeOff : MaterialIconKind.Eye;

    public LoginViewModel(Action<ServerProfile, IRconService> onLoginSuccess)
    {
        _onLoginSuccess = onLoginSuccess;
        _ = LoadProfiles();
    }

    private Task<bool> LoadProfiles()
    {
        return ExecuteSafeAsync(async () =>
        {
            List<ServerProfile> list = await ProfileStorageService.LoadProfilesAsync();
            Profiles = new ObservableCollection<ServerProfile>(list);
            SelectedProfile = Profiles.FirstOrDefault();
        }, "Failed to load saved server profiles.");
    }

    partial void OnSelectedProfileChanged(ServerProfile? value)
    {
        ExecuteSafe(() =>
        {
            if (value == null) return;
            ServerIp = value.ServerIp;
            Port = value.Port;
            Password = value.Password;
            Protocol = value.Protocol;
            AutoConnect = value.AutoConnect;
            OnPropertyChanged(nameof(IsReforgerProtocol));
            OnPropertyChanged(nameof(IsBattlEyeProtocol));
        });
    }

    partial void OnProtocolChanged(RconProtocol value)
    {
        ExecuteSafe(() =>
        {
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
    private Task<bool> SaveCurrentProfileAsync()
    {
        return ExecuteSafeAsync(async () =>
        {
            var name = string.IsNullOrWhiteSpace(NewProfileName) ? $"Server {ServerIp}:{Port}" : NewProfileName;
            ServerProfile? existing = Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.ServerIp = ServerIp;
                existing.Port = Port;
                existing.Password = Password;
                existing.Protocol = Protocol;
                existing.AutoConnect = AutoConnect;
            }
            else
            {
                var p = new ServerProfile
                {
                    Name = name,
                    ServerIp = ServerIp,
                    Port = Port,
                    Password = Password,
                    Protocol = Protocol,
                    AutoConnect = AutoConnect
                };
                Profiles.Add(p);
                SelectedProfile = p;
            }
            NewProfileName = string.Empty;
            List<ServerProfile> profileList = [.. Profiles];
            await ProfileStorageService.SaveProfilesAsync(profileList);
            ToastNotificationService.Instance.ShowToast("Profile Saved", $"Saved connection profile '{name}'.");
        });
    }

    [RelayCommand]
    public Task<bool> DeleteProfileAsync(ServerProfile profile)
    {
        return ExecuteSafeAsync(async () =>
        {
            Profiles.Remove(profile);
            List<ServerProfile> profileList = [.. Profiles];
            await ProfileStorageService.SaveProfilesAsync(profileList);
            ToastNotificationService.Instance.ShowToast("Profile Deleted", $"Removed '{profile.Name}'.");
        });
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        await ExecuteSafeAsync(async () =>
        {
            IsConnecting = true;
            ErrorMessage = string.Empty;

            var profile = new ServerProfile
            {
                ServerIp = ServerIp.Trim(),
                Port = Port,
                Password = Password,
                Protocol = Protocol,
                AutoConnect = AutoConnect
            };

            AppLogger.Info($"[LoginViewModel] Connecting to server {profile.ServerIp}:{profile.Port} ({profile.Protocol})...");
            var rconService = new RconService();
            var success = await rconService.ConnectAsync(profile);

            if (success)
            {
                AppLogger.Info("[LoginViewModel] Live connection verified.");
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

            AppLogger.Info($"[LoginViewModel] Launching demo simulation for {profile.Protocol}...");
            var mockService = new MockRconService();
            await mockService.ConnectAsync(profile);
            _onLoginSuccess(profile, mockService);
        });
        IsConnecting = false;
    }
}