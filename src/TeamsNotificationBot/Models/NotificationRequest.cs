using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

public class NotificationRequest
{
    [JsonPropertyName("message")]
    public JsonElement Message { get; set; }

    [JsonPropertyName("format")]
    public string Format { get; set; } = "text";

    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; set; }

    /// <summary>Post in the thread of this earlier message (a messageId from this API).</summary>
    [JsonPropertyName("replyTo")]
    public string? ReplyTo { get; set; }

    /// <summary>Replace this earlier message (a messageId from this API) instead of posting.</summary>
    [JsonPropertyName("update")]
    public string? Update { get; set; }

    /// <summary>People and tags to mention, placed in the message with <c>&lt;at&gt;key&lt;/at&gt;</c>.</summary>
    [JsonPropertyName("mentions")]
    public List<MessageMention>? Mentions { get; set; }

    public bool IsValid(out string? error)
    {
        if (Message.ValueKind == JsonValueKind.Undefined)
        {
            error = "Message is required.";
            return false;
        }

        if (Format == "text" && Message.ValueKind != JsonValueKind.String)
        {
            error = "Message must be a string when format is 'text'.";
            return false;
        }

        if (Format == "adaptive-card" && Message.ValueKind != JsonValueKind.Object)
        {
            error = "Message must be a JSON object when format is 'adaptive-card'.";
            return false;
        }

        if (Format != "text" && Format != "adaptive-card")
        {
            error = $"Unsupported format '{Format}'. Use 'text' or 'adaptive-card'.";
            return false;
        }

        if (ReplyTo != null && Update != null)
        {
            error = "Use either 'replyTo' or 'update', not both.";
            return false;
        }

        if (ReplyTo != null && !MessageIds.IsValid(ReplyTo))
        {
            error = "'replyTo' must be a messageId returned by this API.";
            return false;
        }

        if (Update != null && !MessageIds.IsValid(Update))
        {
            error = "'update' must be a messageId returned by this API.";
            return false;
        }

        error = MetadataRules.Validate(Metadata) ?? MentionRules.Validate(Mentions, Format, Message);
        return error == null;
    }
}
