using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

[SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Reads generated partial properties")]
public partial class OfflineBanDialogViewModel(string uid, string ip, IRconService rconService, DatabaseViewModel parent) : ViewModelBase
{
    private readonly string _uid = uid;
    private readonly string _ip = ip;
    private readonly IRconService _rconService = rconService;
    private readonly DatabaseViewModel _parent = parent;

    [ObservableProperty] public partial string TargetIdentifier { get; set; } = $"{uid} / {ip}";
    [ObservableProperty] public partial string TargetType { get; set; } = "Both";
    [ObservableProperty] public partial string SelectedPreset { get; set; } = "1 Month";
    [ObservableProperty] public partial int CustomYears { get; set; }
    [ObservableProperty] public partial int CustomMonths { get; set; }
    [ObservableProperty] public partial int CustomWeeks { get; set; }
    [ObservableProperty] public partial int CustomDays { get; set; }
    [ObservableProperty] public partial int CustomHours { get; set; }
    [ObservableProperty] public partial int CustomMinutes { get; set; }
    [ObservableProperty] public partial int CustomSeconds { get; set; }
    [ObservableProperty] public partial string Reason { get; set; } = "Offline Ban - Exploiting/Rule Violation";
    [ObservableProperty] public partial string TotalCalculatedTimeText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ExpiryDateText { get; set; } = string.Empty;

    public bool IsCustomSelected => SelectedPreset == "Custom Duration";

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomSelected));
        UpdateCalculations();
    }

    partial void OnCustomYearsChanged(int value) => UpdateCalculations();
    partial void OnCustomMonthsChanged(int value) => UpdateCalculations();
    partial void OnCustomWeeksChanged(int value) => UpdateCalculations();
    partial void OnCustomDaysChanged(int value) => UpdateCalculations();
    partial void OnCustomHoursChanged(int value) => UpdateCalculations();
    partial void OnCustomMinutesChanged(int value) => UpdateCalculations();
    partial void OnCustomSecondsChanged(int value) => UpdateCalculations();

    [RelayCommand]
    private void SetPreset(string preset) => SelectedPreset = preset;

    private long CalculateTotalSeconds() => SelectedPreset switch
    {
        "1 Hour" => 3600,
        "6 Hours" => 21600,
        "1 Day" => 86400,
        "3 Days" => 259200,
        "1 Week" => 604800,
        "1 Month" => 2592000,
        "Permanent" => 0,
        "Custom Duration" =>
            (CustomYears * 31536000L) +
            (CustomMonths * 2592000L) +
            (CustomWeeks * 604800L) +
            (CustomDays * 86400L) +
            (CustomHours * 3600L) +
            (CustomMinutes * 60L) +
            CustomSeconds,
        _ => 2592000
    };

    private void UpdateCalculations()
    {
        var totalSec = CalculateTotalSeconds();
        if (totalSec <= 0)
        {
            TotalCalculatedTimeText = "Total: Permanent";
            ExpiryDateText = "Expires: Never";
        }
        else
        {
            var span = TimeSpan.FromSeconds(totalSec);
            TotalCalculatedTimeText = $"Total: {(int)span.TotalDays} days, {span.Hours} hours, {span.Minutes} mins ({totalSec} seconds)";
            ExpiryDateText = $"Expires: {DateTime.Now.AddSeconds(totalSec):MMM dd, yyyy HH:mm}";
        }
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var totalSec = CalculateTotalSeconds();

        if (TargetType is "GUID/UID" or "Both")
            await _rconService.OfflineBanAsync(_uid, totalSec, Reason, isIp: false);
        if (TargetType is "IP" or "Both")
            await _rconService.OfflineBanAsync(_ip, totalSec, Reason, isIp: true);

        ToastNotificationService.Instance.ShowToast("Offline Ban", $"Offline ban dispatched for {_uid} / {_ip}", "ban");
        _parent.CloseDialog();
    }

    [RelayCommand]
    private void Close() => _parent.CloseDialog();
}