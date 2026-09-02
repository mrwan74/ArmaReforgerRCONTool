using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class AdminsDialogViewModel : ViewModelBase
{
    private readonly IRconService _rconService;
    private readonly DashboardViewModel _dashboard;

    [ObservableProperty] public partial ObservableCollection<AdminModel> Admins { get; set; } = [];
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial int AdminsCount { get; set; }

    public AdminsDialogViewModel(IRconService rconService, DashboardViewModel dashboard)
    {
        _rconService = rconService;
        _dashboard = dashboard;
        AppLogger.Debug("[AdminsDialogViewModel:Init] Initializing AdminsDialogViewModel...");
        _ = LoadAdminsAsync();
    }

    [RelayCommand]
    public Task<bool> LoadAdminsAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        IsLoading = true;
        using var timing = AppLogger.Measure("AdminsDialogViewModel.LoadAdminsAsync");
        try
        {
            var adminList = await _rconService.GetAdminsAsync().ConfigureAwait(false);

            if (adminList.Count > 0)
            {
                adminList[0].IsCurrentSession = true;
            }

            Dispatcher.UIThread.Post(() =>
            {
                Admins = new ObservableCollection<AdminModel>(adminList);
                AdminsCount = adminList.Count;
                IsEmpty = adminList.Count == 0;
                _dashboard.ConnectedAdminsCount = adminList.Count;
            });

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            AppLogger.Info($"[AdminsDialog:Load] Retrieved {adminList.Count} connected admin session(s) in {elapsedMs:F2}ms.");
        }
        finally
        {
            Dispatcher.UIThread.Post(() => IsLoading = false);
        }
    }, "Failed to retrieve connected RCON administrators.");

    [RelayCommand]
    public static async Task CopyEndpointAsync(AdminModel? admin)
    {
        if (admin == null) return;
        var start = Stopwatch.GetTimestamp();
        await ClipboardService.SetTextAsync(admin.FormattedEndpoint).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[AdminsDialog:Clipboard] Copied admin endpoint '{admin.FormattedEndpoint}' in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Copied Endpoint", $"Copied {admin.FormattedEndpoint} to clipboard.");
    }

    [RelayCommand]
    public static async Task CopyIpOnlyAsync(AdminModel? admin)
    {
        if (admin == null) return;
        var start = Stopwatch.GetTimestamp();
        await ClipboardService.SetTextAsync(admin.Ip).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[AdminsDialog:Clipboard] Copied admin IP '{admin.Ip}' in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Copied IP", $"Copied {admin.Ip} to clipboard.");
    }

    [RelayCommand]
    public static async Task CopyFullInfoAsync(AdminModel? admin)
    {
        if (admin == null) return;
        var start = Stopwatch.GetTimestamp();
        var info = admin.GetFullDiagnosticInfo();
        await ClipboardService.SetTextAsync(info).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[AdminsDialog:Clipboard] Copied details for Admin #{admin.Id} in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Copied RCON Admin Details", $"Copied full details for Admin #{admin.Id}.");
    }

    [RelayCommand]
    public async Task CopyAllAdminsAsync()
    {
        if (Admins.Count == 0)
        {
            AppLogger.Warn("[AdminsDialog:CopyAll] CopyAllAdminsAsync called with 0 admins.");
            ToastNotificationService.Instance.ShowToast("No Admins", "There are no connected RCON admins to copy.");
            return;
        }

        var start = Stopwatch.GetTimestamp();
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"=== CONNECTED RCON ADMINS ({Admins.Count}) ===");
        foreach (var admin in Admins)
        {
            sb.AppendLine(admin.GetFullDiagnosticInfo());
        }

        await ClipboardService.SetTextAsync(sb.ToString().TrimEnd()).ConfigureAwait(false);
        var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        AppLogger.Info($"[AdminsDialog:CopyAll] Copied details for {Admins.Count} admin(s) in {elapsedMs:F2}ms.");
        ToastNotificationService.Instance.ShowToast("Copied All Admins", $"Copied details for {Admins.Count} connected RCON admin(s).");
    }

    [RelayCommand]
    private void Close()
    {
        AppLogger.Debug("[AdminsDialog:Close] Dialog closed.");
        _dashboard.CloseDialog();
    }
}