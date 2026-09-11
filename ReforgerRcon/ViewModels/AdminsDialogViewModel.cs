using System;
using System.Collections.Generic;
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
    private const string ClipboardErrorTitle = "Clipboard Error";
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

        AppLogger.Debug($"[AdminsDialog:Init] Instantiating AdminsDialogViewModel for {_rconService.CurrentProtocol}...");
        _ = LoadAdminsAsync();
    }

    [RelayCommand]
    public Task<bool> LoadAdminsAsync() => ExecuteSafeAsync(async () =>
    {
        var start = Stopwatch.GetTimestamp();
        var context = new Dictionary<string, object?>
        {
            ["protocol"] = _rconService.CurrentProtocol.ToString(),
            ["thread_id"] = Environment.CurrentManagedThreadId
        };

        AppLogger.Debug("[AdminsDialog:Load] Querying connected RCON admin sessions from server...", context);
        IsLoading = true;

        using var timing = AppLogger.Measure("AdminsDialogViewModel.LoadAdminsAsync");
        try
        {
            var adminList = await _rconService.GetAdminsAsync().ConfigureAwait(false);

            if (adminList.Count > 0)
            {
                adminList[0].IsCurrentSession = true;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Admins = new ObservableCollection<AdminModel>(adminList);
                AdminsCount = adminList.Count;
                IsEmpty = adminList.Count == 0;
                _dashboard.ConnectedAdminsCount = adminList.Count;
            });

            var elapsedMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            context["admins_count"] = adminList.Count;
            context["elapsed_ms"] = elapsedMs;

            AppLogger.Info($"[AdminsDialog:Load] Successfully parsed {adminList.Count} connected admin session(s) in {elapsedMs:F2}ms.", context);
        }
        catch (Exception ex)
        {
            context["error"] = ex.Message;
            AppLogger.Error($"[AdminsDialog:Load] Error loading admin sessions: {ex.Message}", ex, context);
            ToastNotificationService.Instance.ShowError("Query Error", $"Failed retrieving admin sessions: {ex.Message}");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }, "Failed to retrieve connected RCON administrators.");

    [RelayCommand]
    public static async Task CopyEndpointAsync(AdminModel? admin)
    {
        if (admin == null) return;
        var start = Stopwatch.GetTimestamp();

        try
        {
            await ClipboardService.SetTextAsync(admin.FormattedEndpoint).ConfigureAwait(false);
            AppLogger.Debug($"[AdminsDialog:Clipboard] Copied admin endpoint: '{admin.FormattedEndpoint}' in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            ToastNotificationService.Instance.ShowToast("Copied Endpoint", $"Copied {admin.FormattedEndpoint} to clipboard.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[AdminsDialog:Clipboard] Failed copying endpoint: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError(ClipboardErrorTitle, "Unable to copy admin endpoint.");
        }
    }

    [RelayCommand]
    public static async Task CopyIpOnlyAsync(AdminModel? admin)
    {
        if (admin == null) return;
        var start = Stopwatch.GetTimestamp();

        try
        {
            await ClipboardService.SetTextAsync(admin.Ip).ConfigureAwait(false);
            AppLogger.Debug($"[AdminsDialog:Clipboard] Copied admin IP: '{admin.Ip}' in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            ToastNotificationService.Instance.ShowToast("Copied IP", $"Copied {admin.Ip} to clipboard.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[AdminsDialog:Clipboard] Failed copying IP: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError(ClipboardErrorTitle, "Unable to copy admin IP.");
        }
    }

    [RelayCommand]
    public static async Task CopyFullInfoAsync(AdminModel? admin)
    {
        if (admin == null) return;
        var start = Stopwatch.GetTimestamp();

        try
        {
            var info = admin.GetFullDiagnosticInfo();
            await ClipboardService.SetTextAsync(info).ConfigureAwait(false);
            AppLogger.Info($"[AdminsDialog:Clipboard] Copied diagnostic details for Admin #{admin.Id} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:F2}ms.");
            ToastNotificationService.Instance.ShowToast("Copied RCON Admin Details", $"Copied full details for Admin #{admin.Id}.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"[AdminsDialog:Clipboard] Failed copying admin diagnostic info: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError(ClipboardErrorTitle, "Unable to copy full admin information.");
        }
    }

    [RelayCommand]
    public async Task CopyAllAdminsAsync()
    {
        if (Admins.Count == 0)
        {
            AppLogger.Warn("[AdminsDialog:CopyAll] CopyAllAdminsAsync bypassed: 0 admins active.");
            ToastNotificationService.Instance.ShowToast("No Admins", "There are no connected RCON admins to copy.");
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
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
        catch (Exception ex)
        {
            AppLogger.Error($"[AdminsDialog:CopyAll] Failed copying all admins: {ex.Message}", ex);
            ToastNotificationService.Instance.ShowError(ClipboardErrorTitle, "Unable to copy admin list.");
        }
    }

    [RelayCommand]
    private void Close()
    {
        ExecuteSafe(() =>
        {
            AppLogger.Debug("[AdminsDialog:Close] Admins dialog closed.");
            _dashboard.CloseDialog();
        });
    }
}