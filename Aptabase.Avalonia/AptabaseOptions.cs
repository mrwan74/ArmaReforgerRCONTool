using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace Aptabase.Avalonia;

public sealed class AptabaseOptions
{
    public string? Host { get; set; }
    public bool? IsDebugMode { get; set; }
    public bool? EnablePersistence { get; set; }
    public bool? EnableCrashReporting { get; set; }
    public bool SuppressUIThreadCrashes { get; set; } = true;
    public bool CaptureAvaloniaFrameworkLogs { get; set; } = true;
    public LogEventLevel AvaloniaLogEventLevel { get; set; } = LogEventLevel.Warning;
    public string? StoragePath { get; set; }
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Information;
    public Func<Dictionary<string, object>>? ContextInjector { get; set; }
    public Action<string, Exception, bool>? OnUserFacingNotification { get; set; }
    public Func<bool>? ConsentCheck { get; set; }
}