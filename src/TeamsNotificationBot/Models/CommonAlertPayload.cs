using System.Text.Json;
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

    /// <summary>
    /// What caused the alert; its shape depends on the monitoring service (a metric's value
    /// and threshold, a log query's dimensions, an activity log event). Kept as raw JSON.
    /// </summary>
    [JsonPropertyName("alertContext")]
    public JsonElement? AlertContext { get; set; }

    /// <summary>
    /// The alert rule's own key/value pairs, often with values Azure fills in from the payload
    /// (<c>${data.alertContext...}</c>). Read as raw JSON: a value that isn't a string must not
    /// turn the whole alert into a 400, which Azure doesn't retry.
    /// </summary>
    [JsonPropertyName("customProperties")]
    public JsonElement? CustomProperties { get; set; }
}

/// <summary>
/// The common alert schema's essentials. Every field is nullable because Azure sends nulls:
/// a budget alert has no alertRule, severity or signalType. The card leaves out what's missing.
/// </summary>
public class AlertEssentials
{
    [JsonPropertyName("alertId")]
    public string? AlertId { get; set; }

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
