using Azure;
using Azure.Data.Tables;

namespace TeamsNotificationBot.Models;

public class ConversationReferenceEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // Team AAD GUID, "user", or "chat"
    public string RowKey { get; set; } = string.Empty;       // Channel thread ID, User AAD OID, or Chat ID
    public string ConversationReference { get; set; } = string.Empty; // JSON serialized
    public string ConversationType { get; set; } = string.Empty; // "channel", "personal", "groupChat"
    public string? TeamName { get; set; }
    public string? ChannelName { get; set; }
    public string? UserName { get; set; }
    public DateTimeOffset InstalledAt { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
}
