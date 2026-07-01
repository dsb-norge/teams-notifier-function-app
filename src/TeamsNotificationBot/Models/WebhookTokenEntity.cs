using Azure;
using Azure.Data.Tables;

namespace TeamsNotificationBot.Models;

/// <summary>
/// A self-service webhook ingress token, bound to one conversation target.
///
/// The plaintext token is NEVER stored: <see cref="RowKey"/> is its SHA-256 hex, so the ingest
/// handler can point-read by hashing the incoming token. <see cref="Id"/> is a short public
/// identifier shown to operators (list/remove/rotate) — it is not a secret and not the token.
/// Conversation coordinates mirror <see cref="AliasEntity"/> so the same direct-target delivery
/// path (SendFunction → QueueProcessor) is reused unchanged.
/// </summary>
public class WebhookTokenEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "webhook";

    /// <summary>SHA-256(token) hex. The plaintext token is never persisted.</summary>
    public string RowKey { get; set; } = string.Empty;

    /// <summary>Short public id (8 hex chars) for operator commands. Not a secret.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Webhook source, e.g. "updown" (generic-ready shape).</summary>
    public string Source { get; set; } = "updown";

    public string TargetType { get; set; } = string.Empty; // "channel" | "personal" | "groupChat"
    public string? TeamId { get; set; }
    public string? ChannelId { get; set; }
    public string? UserId { get; set; }
    public string? ChatId { get; set; }

    public string Description { get; set; } = string.Empty;

    /// <summary>Human-readable account label surfaced on cards (e.g. "prod-monitoring / ops@dsb.no").</summary>
    public string UpdownAccount { get; set; } = string.Empty;

    /// <summary>Comma-joined enabled event types. Empty means "all defaults".</summary>
    public string EnabledEvents { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty; // AAD OID
    public string CreatedByName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastReceivedAt { get; set; }

    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>Parses <see cref="EnabledEvents"/> into a set; empty string → default enabled set.</summary>
    public IReadOnlyList<string> GetEnabledEvents() =>
        string.IsNullOrWhiteSpace(EnabledEvents)
            ? UpdownEventTypes.DefaultEnabled
            : EnabledEvents.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsEventEnabled(string? eventType) =>
        eventType != null && GetEnabledEvents().Contains(eventType);
}
