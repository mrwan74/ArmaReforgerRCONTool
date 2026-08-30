using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Aptabase.Avalonia;

internal sealed class EventData
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o");

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("eventName")]
    public string EventName { get; set; } = string.Empty;

    [JsonPropertyName("systemProps")]
    public SystemInfo? SystemProps { get; set; }

    [JsonPropertyName("props")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? Props { get; set; }

    public EventData()
    {
    }

    [JsonConstructor]
    public EventData(string eventName, string? sessionId = null, string? timestamp = null, SystemInfo? systemProps = null, Dictionary<string, object>? props = null)
    {
        EventName = eventName;
        SessionId = sessionId;
        if (!string.IsNullOrEmpty(timestamp))
        {
            Timestamp = timestamp;
        }
        SystemProps = systemProps;
        Props = props ?? [];
    }

    public EventData(string eventName, Dictionary<string, object>? props, Func<Dictionary<string, object>>? contextInjector)
        : this(eventName, null, null, null, props)
    {
        if (contextInjector is not null)
        {
            try
            {
                var ambientContext = contextInjector();
                if (ambientContext is not null)
                {
                    Props ??= [];
                    foreach (var (key, value) in ambientContext)
                    {
                        Props.TryAdd(key, value);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EventData] Context injector notice: {ex.Message}");
            }
        }
    }

    public override string ToString() =>
        $"[Event: '{EventName}', Timestamp: {Timestamp}, PropsCount: {Props?.Count ?? 0}, Session: {SessionId}]";
}