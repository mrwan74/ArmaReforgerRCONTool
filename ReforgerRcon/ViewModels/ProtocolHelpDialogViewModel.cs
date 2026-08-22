using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

[SuppressMessage("Minor Code Smell", "S1075:URIs should not be hardcoded", Justification = "Official Bohemia Interactive and BattlEye web documentation URLs")]
public partial class ProtocolHelpDialogViewModel(Action onClose) : ViewModelBase
{
    private readonly Action _onClose = onClose;

    public const string BattlEyeDocsUrl = "https://www.battleye.com/support/documentation/";
    public const string BattlEyeHostingWikiUrl = "https://community.bistudio.com/wiki/Arma_Reforger:Server_Hosting#BattlEye";
    public const string ReforgerConfigWikiUrl = "https://community.bistudio.com/wiki/Arma_Reforger:Server_Config?useskin=darkvector#rcon_2";
    public const string ReforgerManagementWikiUrl = "https://community.bistudio.com/wiki/Arma_Reforger:Server_Management";

    [ObservableProperty] public partial string SelectedSection { get; set; } = "Comparison";

    public static string BattlEyeConfigSample => """
    GameID armar
    MasterPort 2001
    RConPort 7117
    RConPassword your_secure_password
    """;

    public static string ReforgerConfigSample => """
    {
      "rcon": {
        "enabled": true,
        "bindAddress": "0.0.0.0",
        "port": 25575,
        "password": "your_secure_password"
      }
    }
    """;

    [RelayCommand]
    private void SetSection(string section)
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug($"[ProtocolHelpDialog] Navigating to guide section: {section}");
            SelectedSection = section;
        });
    }

    [RelayCommand]
    private Task<bool> OpenBattlEyeDocsAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Info("[ProtocolHelpDialog] Opening BattlEye documentation link...");
        await UrlLauncherService.OpenUrlAsync(BattlEyeDocsUrl);
    });

    [RelayCommand]
    private Task<bool> OpenBattlEyeWikiAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Info("[ProtocolHelpDialog] Opening Bohemia BattlEye Hosting Wiki link...");
        await UrlLauncherService.OpenUrlAsync(BattlEyeHostingWikiUrl);
    });

    [RelayCommand]
    private Task<bool> OpenReforgerConfigWikiAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Info("[ProtocolHelpDialog] Opening Reforger Server Config Wiki link...");
        await UrlLauncherService.OpenUrlAsync(ReforgerConfigWikiUrl);
    });

    [RelayCommand]
    private Task<bool> OpenReforgerManagementWikiAsync() => ExecuteSafeAsync(async () =>
    {
        AppLogger.Info("[ProtocolHelpDialog] Opening Reforger Server Management Wiki link...");
        await UrlLauncherService.OpenUrlAsync(ReforgerManagementWikiUrl);
    });

    [RelayCommand]
    private Task<bool> CopyBattlEyeConfigAsync() => ExecuteSafeAsync(async () =>
    {
        await ClipboardService.SetTextAsync(BattlEyeConfigSample);
        ToastNotificationService.Instance.ShowToast(
            "BattlEye Config Copied",
            "BEServer_x64.cfg template copied to clipboard.",
            "CLIPBOARD_BE_CONFIG"
        );
    });

    [RelayCommand]
    private Task<bool> CopyReforgerConfigAsync() => ExecuteSafeAsync(async () =>
    {
        await ClipboardService.SetTextAsync(ReforgerConfigSample);
        ToastNotificationService.Instance.ShowToast(
            "Reforger JSON Config Copied",
            "rcon configuration block copied to clipboard.",
            "CLIPBOARD_REFORGER_CONFIG"
        );
    });

    [RelayCommand]
    private void Close()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[ProtocolHelpDialog] Closing protocol guidance popup.");
            _onClose();
        });
    }
}