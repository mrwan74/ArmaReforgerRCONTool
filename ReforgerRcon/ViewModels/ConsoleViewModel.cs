using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ReforgerRcon.Models;
using ReforgerRcon.Services;

namespace ReforgerRcon.ViewModels;

public partial class ConsoleViewModel : ViewModelBase
{
    private const int MaxLogHistoryCount = 2500;
    private const int TrimToLogCount = 2000;

    private readonly IRconService _rconService;
    private readonly DashboardViewModel? _dashboard;
    private readonly List<LogEntryModel> _allLogs = [];

    [ObservableProperty] public partial ObservableCollection<LogEntryModel> FilteredLogs { get; set; } = [];
    [ObservableProperty] public partial string CommandInput { get; set; } = string.Empty;
    [ObservableProperty] public partial bool AutoScroll { get; set; } = true;
    [ObservableProperty] public partial bool IsFullscreen { get; set; }
    [ObservableProperty] public partial bool IsDetached { get; set; }
    [ObservableProperty] public partial LogCategory SelectedTab { get; set; } = LogCategory.All;

    public event Action? ScrollToEndRequested;

    public bool IsReforgerProtocol => _rconService.CurrentProtocol == RconProtocol.ReforgerBuiltIn;
    public bool IsBattlEyeProtocol => _rconService.CurrentProtocol == RconProtocol.BattlEye;

    public string CommandPlaceholder => IsReforgerProtocol
        ? "Enter Reforger command (e.g. #players, #kick 1, #ban create 1 0, #restart, #shutdown) or custom in-game @command..."
        : "Enter BattlEye command (e.g. players, bans, admins, kick [id], addBan [guid] [minutes], loadBans)...";

    public ConsoleViewModel(IRconService rconService, DashboardViewModel? dashboard = null)
    {
        _rconService = rconService;
        _dashboard = dashboard;
        _rconService.OutputReceived += OnOutputReceived;

        AddLog(LogCategory.System, LogType.System, $"RCON console initialized for {_rconService.CurrentProtocol}.");
        AppLogger.Debug($"[ConsoleViewModel:Init] Initialized for protocol: {_rconService.CurrentProtocol}");
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

        if (_allLogs.Count > MaxLogHistoryCount)
        {
            int removeCount = _allLogs.Count - TrimToLogCount;
            for (int i = 0; i < removeCount && _allLogs.Count > 0; i++)
            {
                var oldest = _allLogs[0];
                _allLogs.RemoveAt(0);
                if (FilteredLogs.Count > 0 && FilteredLogs[0] == oldest)
                {
                    FilteredLogs.RemoveAt(0);
                }
            }
            AppLogger.Debug($"[ConsoleViewModel:Buffer] Truncated {removeCount} old log line(s) to maintain buffer limit ({TrimToLogCount}).");
        }

        if (SelectedTab == LogCategory.All || SelectedTab == category)
        {
            FilteredLogs.Add(entry);
            if (AutoScroll)
            {
                RequestScrollToEnd();
            }
        }
    }

    partial void OnSelectedTabChanged(LogCategory value)
    {
        AppLogger.Debug($"[ConsoleViewModel:Filter] Category filter switched to: {value}");
        ApplyTabFilter();
    }

    partial void OnAutoScrollChanged(bool value)
    {
        AppLogger.Trace($"[ConsoleViewModel:AutoScroll] AutoScroll toggled: {value}");
        if (value)
        {
            RequestScrollToEnd();
        }
    }

    public void RequestScrollToEnd()
    {
        ScrollToEndRequested?.Invoke();
    }

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
        using var timing = AppLogger.Measure($"ConsoleViewModel.ApplyTabFilter('{SelectedTab}')");
        FilteredLogs.Clear();
        var matching = SelectedTab == LogCategory.All
            ? _allLogs
            : _allLogs.Where(l => l.Category == SelectedTab);

        foreach (var log in matching)
        {
            FilteredLogs.Add(log);
        }

        AppLogger.Trace($"[ConsoleViewModel:Filter] Filtered {FilteredLogs.Count}/{_allLogs.Count} entries for '{SelectedTab}'.");

        if (AutoScroll)
        {
            RequestScrollToEnd();
        }
    }

    [RelayCommand]
    private Task<bool> SendCommandAsync() => ExecuteSafeAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(CommandInput)) return;
        var cmd = CommandInput.Trim();
        CommandInput = string.Empty;
        AppLogger.Info($"[ConsoleViewModel:Command] Submitted command: '{AppLogger.SanitizeSensitiveData(cmd)}'");
        await _rconService.SendCommandAsync(cmd).ConfigureAwait(false);
    });

    [RelayCommand]
    private void ToggleFullscreen()
    {
        AppLogger.Debug("[ConsoleViewModel:Fullscreen] Fullscreen toggled.");
        _dashboard?.ToggleConsoleFullscreen();
    }

    [RelayCommand]
    private void Detach()
    {
        AppLogger.Info("[ConsoleViewModel:Detach] Detaching console.");
        _dashboard?.DetachConsole();
    }

    [RelayCommand]
    private void Reattach()
    {
        AppLogger.Info("[ConsoleViewModel:Reattach] Reattaching console.");
        _dashboard?.ReattachConsole();
    }

    [RelayCommand]
    private void ClearLogs()
    {
        int count = _allLogs.Count;
        _allLogs.Clear();
        FilteredLogs.Clear();
        AppLogger.Info($"[ConsoleViewModel:Clear] Cleared {count} log entries.");
    }

    [RelayCommand]
    private Task<bool> CopyLogsAsync() => ExecuteSafeAsync(async () =>
    {
        var text = string.Join(Environment.NewLine, FilteredLogs.Select(l => $"[{l.FormattedTime}] [{l.BadgeText}] {l.Message}"));
        await ClipboardService.SetTextAsync(text).ConfigureAwait(false);
        AppLogger.Info($"[ConsoleViewModel:Clipboard] Copied {FilteredLogs.Count} console entries ({text.Length} chars) to clipboard.");
        ToastNotificationService.Instance.ShowToast("Logs Copied", "Copied terminal buffer to clipboard.");
    });
}