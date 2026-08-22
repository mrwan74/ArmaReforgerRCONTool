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
        CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: true);
        CrashReportService.UnhandledErrorCaptured += OnUnhandledErrorCaptured;
    }

    private void OnUnhandledErrorCaptured(ErrorReportModel report)
    {
        AppLogger.Info($"MainViewModel presenting global crash dialog: {report.ErrorId}");
        CurrentErrorViewModel = new ErrorDetailsDialogViewModel(report, CloseErrorDialog);
        IsErrorDialogVisible = true;
    }

    [RelayCommand]
    public void CloseErrorDialog()
    {
        IsErrorDialogVisible = false;
        CurrentErrorViewModel = null;
    }

    [RelayCommand]
    public static void ToggleTheme()
    {
        LuminaThemeManager.ToggleThemeVariant();
    }

    private void OnLoginSuccess(ServerProfile profile, IRconService rconService)
    {
        CurrentView = new DashboardViewModel(profile, rconService, OnDisconnect);
    }

    private void OnDisconnect()
    {
        CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: false);
    }
}