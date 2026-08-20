using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class ConsoleViewModel : ViewModelBase
{
    private readonly IRconService _rconService;
    private readonly DashboardViewModel? _dashboard;
    private readonly ObservableCollection<LogEntryModel> _allLogs = [];

    [ObservableProperty] public partial ObservableCollection<LogEntryModel> FilteredLogs { get; set; } = [];
    [ObservableProperty] public partial string CommandInput { get; set; } = string.Empty;
    [ObservableProperty] public partial bool AutoScroll { get; set; } = true;
    [ObservableProperty] public partial bool IsFullscreen { get; set; }
    [ObservableProperty] public partial bool IsDetached { get; set; }
    [ObservableProperty] public partial LogCategory SelectedTab { get; set; } = LogCategory.All;

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CommandPlaceholder => IsReforgerProtocol
        ? "Enter Reforger command (e.g. #players, #ban list, #kick 1, #restart, #shutdown, #say -1 Hello)..."
        : "Enter BattlEye command (e.g. players, bans, admins, kick [id], loadBans, writeBans)...";

    public ConsoleViewModel(IRconService rconService, DashboardViewModel? dashboard = null)
    {
        _rconService = rconService;
        _dashboard = dashboard;
        _rconService.OutputReceived += OnOutputReceived;

        AddLog(LogCategory.System, LogType.System, $"RCON console initialized for {_rconService.CurrentProtocol}.");
    }

    private void OnOutputReceived(object? sender, string rawMessage)
    {
        var category = LogCategory.System;
        var type = LogType.System;
        var cleanMessage = rawMessage;

        if (rawMessage.StartsWith("[RCON OUT]", StringComparison.OrdinalIgnoreCase))
        {
            category = LogCategory.Rcon;
            type = LogType.RconOut;
            cleanMessage = rawMessage["[RCON OUT]".Length..].Trim();
        }
        else if (rawMessage.StartsWith("[RCON IN]", StringComparison.OrdinalIgnoreCase))
        {
            category = LogCategory.Rcon;
            type = LogType.RconIn;
            cleanMessage = rawMessage["[RCON IN]".Length..].Trim();
        }
        else if (rawMessage.StartsWith("[RCON]", StringComparison.OrdinalIgnoreCase))
        {
            category = LogCategory.Rcon;
            type = LogType.RconIn;
            cleanMessage = rawMessage["[RCON]".Length..].Trim();
        }
        else if (rawMessage.StartsWith("[SYSTEM]", StringComparison.OrdinalIgnoreCase))
        {
            category = LogCategory.System;
            type = LogType.System;
            cleanMessage = rawMessage["[SYSTEM]".Length..].Trim();
        }
        else if (rawMessage.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase))
        {
            category = LogCategory.System;
            type = LogType.Error;
            cleanMessage = rawMessage["[ERROR]".Length..].Trim();
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => AddLog(category, type, cleanMessage));
    }

    private void AddLog(LogCategory category, LogType type, string message)
    {
        var entry = new LogEntryModel
        {
            Category = category,
            Type = type,
            Message = message,
            Timestamp = DateTime.Now
        };
        _allLogs.Add(entry);
        ApplyTabFilter();
    }

    partial void OnSelectedTabChanged(LogCategory value) => ApplyTabFilter();

    [RelayCommand]
    private void SetCategory(string category)
    {
        SelectedTab = category switch
        {
            "RCON" => LogCategory.Rcon,
            "System" => LogCategory.System,
            _ => LogCategory.All
        };
    }

    private void ApplyTabFilter()
    {
        if (SelectedTab == LogCategory.All)
            FilteredLogs = new ObservableCollection<LogEntryModel>(_allLogs);
        else
            FilteredLogs = new ObservableCollection<LogEntryModel>(_allLogs.Where(l => l.Category == SelectedTab));
    }

    [RelayCommand]
    private async Task SendCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(CommandInput)) return;
        var cmd = CommandInput.Trim();
        CommandInput = string.Empty;
        await _rconService.SendCommandAsync(cmd);
    }

    [RelayCommand]
    private void ToggleFullscreen()
    {
        _dashboard?.ToggleConsoleFullscreen();
    }

    [RelayCommand]
    private void Detach()
    {
        _dashboard?.DetachConsole();
    }

    [RelayCommand]
    private void Reattach()
    {
        _dashboard?.ReattachConsole();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        _allLogs.Clear();
        FilteredLogs.Clear();
    }

    [RelayCommand]
    private async Task CopyLogsAsync()
    {
        var text = string.Join(Environment.NewLine, FilteredLogs.Select(l => $"[{l.FormattedTime}] [{l.BadgeText}] {l.Message}"));
        await ClipboardService.SetTextAsync(text);
        ToastNotificationService.Instance.ShowToast("Logs Copied", "Copied terminal buffer to clipboard.");
    }
}