using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

public class QueueMessage
{
    [JsonPropertyName("messageId")]
    public string MessageId { get; set; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("target")]
    public MessageTarget? Target { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "text";

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    [JsonPropertyName("enqueuedAt")]
    public DateTimeOffset EnqueuedAt { get; set; }
}
