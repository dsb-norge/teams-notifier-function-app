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

public class AlertEssentials
{
    [JsonPropertyName("alertRule")]
    public string AlertRule { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("monitorCondition")]
    public string MonitorCondition { get; set; } = string.Empty;

    [JsonPropertyName("signalType")]
    public string SignalType { get; set; } = string.Empty;

    [JsonPropertyName("firedDateTime")]
    public string FiredDateTime { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("alertTargetIDs")]
    public List<string>? AlertTargetIDs { get; set; }

    [JsonPropertyName("monitoringService")]
    public string? MonitoringService { get; set; }
}
