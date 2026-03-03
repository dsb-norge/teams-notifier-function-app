using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

public class BotOperationMessage
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("teamGuid")]
    public string TeamGuid { get; set; } = string.Empty;

    [JsonPropertyName("teamName")]
    public string? TeamName { get; set; }

    [JsonPropertyName("teamThreadId")]
    public string? TeamThreadId { get; set; }

    [JsonPropertyName("serializedReference")]
    public string? SerializedReference { get; set; }

    [JsonPropertyName("enqueuedAt")]
    public DateTimeOffset EnqueuedAt { get; set; }
}
