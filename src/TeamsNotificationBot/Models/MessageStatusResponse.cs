using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

/// <summary>The body of <c>GET /v1/messages/{messageId}</c>.</summary>
public sealed class MessageStatusResponse
{
    [JsonPropertyName("messageId")]
    public required string MessageId { get; init; }

    /// <summary><c>queued</c>, <c>delivered</c> or <c>failed</c>.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary><c>post</c>, <c>reply</c> or <c>update</c>; null until delivered.</summary>
    [JsonPropertyName("postedAs")]
    public string? PostedAs { get; init; }

    /// <summary>Where the message went; null until delivered.</summary>
    [JsonPropertyName("target")]
    public MessageTarget? Target { get; init; }

    /// <summary>Mentions that weren't found in the roster. Always empty until mentions ship.</summary>
    [JsonPropertyName("unresolvedMentions")]
    public IReadOnlyList<string> UnresolvedMentions { get; init; } = [];

    [JsonPropertyName("enqueuedAt")]
    public DateTimeOffset EnqueuedAt { get; init; }

    [JsonPropertyName("deliveredAt")]
    public DateTimeOffset? DeliveredAt { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    public static MessageStatusResponse From(DeliveryRecordEntity record) => new()
    {
        MessageId = record.PartitionKey,
        Status = record.Status,
        PostedAs = record.PostedAs,
        Target = record.TargetType == null ? null : new MessageTarget
        {
            Type = record.TargetType,
            TeamId = record.TeamId,
            ChannelId = record.ChannelId,
            UserId = record.UserId,
            ChatId = record.ChatId
        },
        EnqueuedAt = record.EnqueuedAt,
        DeliveredAt = record.DeliveredAt,
        Error = record.Error
    };
}
