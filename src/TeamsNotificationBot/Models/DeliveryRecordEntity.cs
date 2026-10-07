using Azure;
using Azure.Data.Tables;

namespace TeamsNotificationBot.Models;

/// <summary>
/// One row per queued message in the <c>deliveryrecords</c> table: what happened to it and where
/// it went in Teams. Read by <c>GET /v1/messages/{messageId}</c>, and by replies and updates to
/// find the message they refer to.
/// </summary>
public class DeliveryRecordEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty; // messageId
    public string RowKey { get; set; } = string.Empty;       // always empty: one row per message

    public string Status { get; set; } = DeliveryStatus.Queued;
    public string? Source { get; set; }
    public string? PrincipalId { get; set; }
    public string? Alias { get; set; }       // lowercase; null for /v1/send and updown
    public string? ReplyTo { get; set; }     // the messageId the request asked to reply to
    public string? Update { get; set; }      // the messageId the request asked to replace

    // Set on delivery.
    public string? PostedAs { get; set; }    // post | reply | update
    public string? TargetType { get; set; }  // channel | personal | groupChat
    public string? TeamId { get; set; }
    public string? ChannelId { get; set; }
    public string? UserId { get; set; }
    public string? ChatId { get; set; }
    public string? ConversationId { get; set; }   // the Teams conversation the activity lives in
    public string? ActivityId { get; set; }       // the Teams activity ID
    public string? ThreadActivityId { get; set; } // the activity that roots this message's thread
    public string? Error { get; set; }

    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset? SendingAt { get; set; } // when a processor claimed the send (status sending)
    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>
    /// The <c>conversationreferences</c> key of the conversation the message went to, or null
    /// before delivery. A method, not a property, so Table Storage doesn't persist it.
    /// </summary>
    public (string PartitionKey, string RowKey)? ConversationKey() => TargetType switch
    {
        "channel" when !string.IsNullOrEmpty(TeamId) && !string.IsNullOrEmpty(ChannelId) => (TeamId, ChannelId),
        "personal" when !string.IsNullOrEmpty(UserId) => ("user", UserId),
        "groupChat" when !string.IsNullOrEmpty(ChatId) => ("chat", ChatId),
        _ => null
    };
}

public static class DeliveryStatus
{
    public const string Queued = "queued";

    /// <summary>
    /// Internal: a processor has claimed the send and is talking to Teams. Shown as <c>queued</c>
    /// by the status endpoint.
    /// </summary>
    public const string Sending = "sending";

    public const string Delivered = "delivered";
    public const string Failed = "failed";
}

public static class PostedAs
{
    public const string Post = "post";
    public const string Reply = "reply";
    public const string Update = "update";
}
