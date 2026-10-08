using System.Text.Json;
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

    /// <summary>Replace this earlier message (a messageId from <c>/v1/send</c> to the same target).</summary>
    [JsonPropertyName("update")]
    public string? Update { get; set; }
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

    /// <summary>
    /// Who the target is, for checking that an update goes where the message it replaces went: the
    /// type and the IDs that name the conversation (for a person, the <c>userId</c> as given, so an
    /// object ID and a UPN for the same person differ). <c>teamId</c> only narrows the search for a
    /// person, so it isn't part of it. A JSON array, so no ID can collide with another by
    /// containing a delimiter.
    /// </summary>
    public string Key() => JsonSerializer.Serialize(Type switch
    {
        "channel" => new[] { Type, TeamId, ChannelId },
        "personal" => [Type, UserId?.ToLowerInvariant()],
        "groupChat" => [Type, ChatId],
        _ => [Type]
    });
}
