using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

public class CommonAlertPayload
{
    [JsonPropertyName("schemaId")]
    public string SchemaId { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public AlertData? Data { get; set; }
}

public class AlertData
{
    [JsonPropertyName("essentials")]
    public AlertEssentials? Essentials { get; set; }

    [JsonPropertyName("customProperties")]
    public Dictionary<string, string>? CustomProperties { get; set; }
}

/// <summary>
/// The common alert schema's essentials. Every field is nullable because Azure sends nulls:
/// a budget alert has no alertRule, severity or signalType. The card leaves out what's missing.
/// </summary>
public class AlertEssentials
{
    [JsonPropertyName("alertRule")]
    public string? AlertRule { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("monitorCondition")]
    public string? MonitorCondition { get; set; }

    [JsonPropertyName("signalType")]
    public string? SignalType { get; set; }

    [JsonPropertyName("firedDateTime")]
    public string? FiredDateTime { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("alertTargetIDs")]
    public List<string>? AlertTargetIDs { get; set; }

    [JsonPropertyName("monitoringService")]
    public string? MonitoringService { get; set; }
}
