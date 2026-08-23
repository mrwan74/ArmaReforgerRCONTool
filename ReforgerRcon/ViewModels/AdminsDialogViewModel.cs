using System.Collections.ObjectModel;
using System.Threading.Tasks;
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

    public AdminsDialogViewModel(IRconService rconService, DashboardViewModel dashboard)
    {
        _rconService = rconService;
        _dashboard = dashboard;
        _ = LoadAdminsAsync();
    }

    [RelayCommand]
    public Task<bool> LoadAdminsAsync() => ExecuteSafeAsync(async () =>
    {
        IsLoading = true;
        try
        {
            var adminList = await _rconService.GetAdminsAsync();
            Admins = new ObservableCollection<AdminModel>(adminList);
            _dashboard.ConnectedAdminsCount = adminList.Count;
            AppLogger.Info($"[AdminsDialog] Loaded {adminList.Count} connected RCON admin(s).");
        }
        finally
        {
            IsLoading = false;
        }
    }, "Failed to retrieve connected RCON administrators.");

    [RelayCommand]
    public static async Task CopyAdminInfoAsync(AdminModel? admin)
    {
        if (admin == null) return;
        var info = $"Admin #{admin.Id} ({admin.FormattedEndpoint}) - {admin.Location}";
        await ClipboardService.SetTextAsync(info);
        ToastNotificationService.Instance.ShowToast("Copied", $"Copied Admin #{admin.Id} info.");
    }

    [RelayCommand]
    private void Close() => _dashboard.CloseDialog();
}