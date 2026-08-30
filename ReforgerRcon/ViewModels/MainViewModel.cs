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
        CurrentView = new LoginViewModel(OnLoginSuccess, isStartup: true);
        CrashReportService.UnhandledErrorCaptured += OnUnhandledErrorCaptured;
    }

    private void OnUnhandledErrorCaptured(ErrorReportModel report)
    {
        AppLogger.Info($"MainViewModel presenting global crash dialog: #{report.ErrorId} ({report.ExceptionType})");
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
        var currentActual = Application.Current?.ActualThemeVariant;
        var newMode = currentActual == ThemeVariant.Dark ? "Dark" : "Light";

        var settings = AppSettings.LoadFromDisk();
        settings.ThemeMode = newMode;
        AppSettings.SaveToDisk(settings);

        AppLogger.Info($"[MainViewModel] Toggled theme variant (New Mode: {newMode}, Actual: {currentActual})");
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