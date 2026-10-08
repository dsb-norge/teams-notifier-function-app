using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

/// <summary>
/// One entry of a notify request's <c>mentions</c> array: a person (<see cref="Id"/>) or a tag
/// (<see cref="Tag"/>), placed in the message with <c>&lt;at&gt;key&lt;/at&gt;</c>. See
/// <see cref="MentionRules"/>.
/// </summary>
public sealed class MessageMention
{
    /// <summary>What the message's <c>&lt;at&gt;key&lt;/at&gt;</c> placements refer to.</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    /// <summary>A person: their Entra object ID or UPN, checked against the roster before posting.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>A tag: its ID, as Graph returns it. Not checked by the bot.</summary>
    [JsonPropertyName("tag")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tag { get; set; }

    /// <summary>
    /// The tag's display name (required for a tag), or the plain text shown for a person who isn't
    /// in the roster (optional; the <see cref="Id"/> is shown without it).
    /// </summary>
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }
}
