using System;
using System.Globalization;
using Avalonia.Media;

namespace ReforgerRcon.Models;

public enum LogCategory
{
    All,
    Rcon,
    System
}

public enum LogType
{
    System,
    RconIn,
    RconOut,
    Error
}

public class LogEntryModel
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogCategory Category { get; set; } = LogCategory.System;
    public LogType Type { get; set; } = LogType.System;
    public string Message { get; set; } = string.Empty;
    public string FormattedTime => Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string BadgeText => Type switch
    {
        LogType.RconIn => "IN",
        LogType.RconOut => "OUT",
        LogType.Error => "ERR",
        _ => "SYS"
    };

    public IBrush BadgeBackground => Type switch
    {
        LogType.RconIn => Brush.Parse("#2010B981"),
        LogType.RconOut => Brush.Parse("#203B82F6"),
        LogType.Error => Brush.Parse("#20EF4444"),
        _ => Brush.Parse("#20F59E0B")
    };

    public IBrush BadgeForeground => Type switch
    {
        LogType.RconIn => Brush.Parse("#10B981"),
        LogType.RconOut => Brush.Parse("#3B82F6"),
        LogType.Error => Brush.Parse("#EF4444"),
        _ => Brush.Parse("#F59E0B")
    };
}