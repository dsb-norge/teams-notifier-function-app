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

    /// <summary>The route that queued the message: notify, send, alert, checkin or updown.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// The calling principal's object ID, as EasyAuth validated it. Null for anonymous sources
    /// (updown) and for messages queued before this field existed.
    /// </summary>
    [JsonPropertyName("principalId")]
    public string? PrincipalId { get; set; }
}
