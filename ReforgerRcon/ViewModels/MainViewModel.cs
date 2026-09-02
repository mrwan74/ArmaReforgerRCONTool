using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LuminaUI.Theming;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

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
        var dashboardVm = new DashboardViewModel(profile, rconService, OnDisconnect);
        CurrentView = dashboardVm;
        dashboardVm.Initialize();
    }

    private void OnDisconnect()
    {
        AppLogger.Info("[MainViewModel:Navigation] Transitioning from Dashboard back to Login screen...");
        CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: false);
    }
}