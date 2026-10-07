using Azure;
using Azure.Data.Tables;

namespace TeamsNotificationBot.Models;

public class AliasEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "alias";
    public string RowKey { get; set; } = string.Empty; // alias name (lowercase)
    public string TargetType { get; set; } = string.Empty; // "channel", "personal", "groupChat"
    public string? TeamId { get; set; }
    public string? ChannelId { get; set; }
    public string? UserId { get; set; }
    public string? ChatId { get; set; }
    public string Description { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty; // AAD OID
    public string CreatedByName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>
    /// The <c>conversationreferences</c> key the alias points to, or null when the row is malformed
    /// (unknown target type, or the ID its type needs is missing). A method, not a property, so
    /// Table Storage doesn't persist it.
    /// </summary>
    public (string PartitionKey, string RowKey)? ConversationKey() => TargetType switch
    {
        "channel" when !string.IsNullOrEmpty(TeamId) && !string.IsNullOrEmpty(ChannelId) => (TeamId, ChannelId),
        "personal" when !string.IsNullOrEmpty(UserId) => ("user", UserId),
        "groupChat" when !string.IsNullOrEmpty(ChatId) => ("chat", ChatId),
        _ => null
    };
}
