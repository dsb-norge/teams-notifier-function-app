using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

public class SendRequest
{
    [JsonPropertyName("target")]
    public MessageTarget Target { get; set; } = new();

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = "text";

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }
}

public class MessageTarget
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty; // "channel", "personal", "groupChat"

    [JsonPropertyName("teamId")]
    public string? TeamId { get; set; }

    [JsonPropertyName("channelId")]
    public string? ChannelId { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("chatId")]
    public string? ChatId { get; set; }
}
