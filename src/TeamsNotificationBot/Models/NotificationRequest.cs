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

        error = null;
        return true;
    }
}
