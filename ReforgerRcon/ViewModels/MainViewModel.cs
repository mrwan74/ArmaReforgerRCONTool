using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Theming;
using ReforgerRcon.Models;
using ReforgerRcon.Services;
using System.Linq;
using System.Threading.Tasks;

namespace ReforgerRcon.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty] public partial ViewModelBase CurrentView { get; set; }
    [ObservableProperty] public partial ErrorDetailsDialogViewModel? CurrentErrorViewModel { get; set; }
    [ObservableProperty] public partial bool IsErrorDialogVisible { get; set; }

    public MainViewModel()
    {
        AppLogger.Debug("[MainViewModel:Init] Initializing MainViewModel...");
        CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: true);
        CrashReportService.UnhandledErrorCaptured += OnUnhandledErrorCaptured;

        CheckForPendingCrashReports();
        AppLogger.Trace("[MainViewModel:Init] MainViewModel ready.");
    }

    private void CheckForPendingCrashReports()
    {
        var pending = CrashReportService.GetAndClearPendingReports();
        if (pending.Count > 0)
        {
            var latest = pending[^1];
            AppLogger.Info($"[MainViewModel:Crash] Displaying pending startup error report #{latest.ErrorId}");
            OnUnhandledErrorCaptured(latest);
        }
    }

    private void OnUnhandledErrorCaptured(ErrorReportModel report)
    {
        AppLogger.Info($"[MainViewModel:Crash] Presenting global crash dialog: #{report.ErrorId} ({report.ExceptionType})");
        CurrentErrorViewModel = new ErrorDetailsDialogViewModel(report, CloseErrorDialog);
        IsErrorDialogVisible = true;
    }

    [RelayCommand]
    public void CloseErrorDialog()
    {
        AppLogger.Debug("[MainViewModel:Crash] Dismissing crash dialog.");
        IsErrorDialogVisible = false;
        CurrentErrorViewModel = null;
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

        AppLogger.Info($"[MainViewModel:Theme] Theme switched to: {newMode}");
    }

    private void OnLoginSuccess(ServerProfile profile, IRconService rconService)
    {
        AppLogger.Info($"[MainViewModel:Navigation] Transitioning from Login to Dashboard for {profile.ServerIp}:{profile.Port} ({profile.Protocol})...");
        var dashboardVm = new DashboardViewModel(profile, rconService, OnDisconnect, OnSwitchProtocolAsync);
        CurrentView = dashboardVm;
        dashboardVm.Initialize();
    }

    private async Task OnSwitchProtocolAsync(ServerProfile profile, RconProtocol newProtocol)
    {
        AppLogger.Info($"[MainViewModel:SwitchProtocol] Switching protocol from {profile.Protocol} to {newProtocol} for {profile.ServerIp}:{profile.Port}...");
        profile.Protocol = newProtocol;

        var profiles = ProfileStorageService.LoadProfilesFast();
        if (profiles.FirstOrDefault(p => p.Id == profile.Id || (p.ServerIp == profile.ServerIp && p.Port == profile.Port)) is { } match)
        {
            match.Protocol = newProtocol;
            ProfileStorageService.SaveProfilesFast(profiles);
        }

        var newRconService = new RconService();
        var success = await newRconService.ConnectAsync(profile).ConfigureAwait(false);
        if (success)
        {
            Dispatcher.UIThread.Post(() => OnLoginSuccess(profile, newRconService));
        }
        else
        {
            Dispatcher.UIThread.Post(OnDisconnect);
        }
    }

    private void OnDisconnect()
    {
        AppLogger.Info("[MainViewModel:Navigation] Transitioning from Dashboard back to Login screen...");
        CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: false);
    }
}