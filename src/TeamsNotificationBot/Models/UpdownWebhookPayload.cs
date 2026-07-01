using System.Text.Json.Serialization;

namespace TeamsNotificationBot.Models;

/// <summary>
/// One updown.io webhook event. updown always delivers events in a JSON array
/// (deserialize to <c>List&lt;UpdownEvent&gt;</c>), even for a single event.
///
/// All fields are nullable and no field is marked required: per updown's docs, new
/// event types and fields may appear at any time, so parsing must be lenient and
/// forward-compatible. Unknown JSON properties are ignored by System.Text.Json by default.
///
/// See docs/feat-updown-io-webhook/manual-verification.md for the canonical payloads.
/// </summary>
public class UpdownEvent
{
    /// <summary>Event type, e.g. "check.down", "check.up", "check.ssl_expiration". Kept raw so
    /// unknown/future types round-trip and can be logged and skipped.</summary>
    [JsonPropertyName("event")]
    public string? Event { get; set; }

    /// <summary>ISO-8601 timestamp of the event.</summary>
    [JsonPropertyName("time")]
    public string? Time { get; set; }

    /// <summary>Plain-text summary. UNTRUSTED — treat as attacker-influenceable text.</summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("check")]
    public UpdownCheck? Check { get; set; }

    /// <summary>Present on check.down / check.up.</summary>
    [JsonPropertyName("downtime")]
    public UpdownDowntime? Downtime { get; set; }

    /// <summary>Present on the SSL-related events.</summary>
    [JsonPropertyName("ssl")]
    public UpdownSsl? Ssl { get; set; }

    /// <summary>Present on check.performance_drop, e.g. "47%".</summary>
    [JsonPropertyName("apdex_dropped")]
    public string? ApdexDropped { get; set; }
}

public class UpdownCheck
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>updown's own alias for the check (distinct from a notifier channel alias).</summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("uptime")]
    public double? Uptime { get; set; }

    [JsonPropertyName("down")]
    public bool? Down { get; set; }

    [JsonPropertyName("down_since")]
    public string? DownSince { get; set; }

    [JsonPropertyName("up_since")]
    public string? UpSince { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("last_status")]
    public int? LastStatus { get; set; }
}

public class UpdownDowntime
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>updown.io URL to view the downtime. Only rendered as a link if it is under
    /// https://updown.io/ (domain-gated) — see UpdownCardBuilder.</summary>
    [JsonPropertyName("details_url")]
    public string? DetailsUrl { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; set; }

    [JsonPropertyName("ended_at")]
    public string? EndedAt { get; set; }

    /// <summary>Downtime length in seconds (null while still down).</summary>
    [JsonPropertyName("duration")]
    public long? Duration { get; set; }

    [JsonPropertyName("partial")]
    public bool? Partial { get; set; }
}

public class UpdownSsl
{
    /// <summary>Current certificate (ssl_invalid / ssl_valid / ssl_expiration).</summary>
    [JsonPropertyName("cert")]
    public UpdownCert? Cert { get; set; }

    /// <summary>Replacement certificate (ssl_renewed).</summary>
    [JsonPropertyName("new_cert")]
    public UpdownCert? NewCert { get; set; }

    /// <summary>Previous certificate (ssl_renewed).</summary>
    [JsonPropertyName("old_cert")]
    public UpdownCert? OldCert { get; set; }

    /// <summary>Validation error (ssl_invalid only).</summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>Days until expiry (ssl_expiration only).</summary>
    [JsonPropertyName("days_before_expiration")]
    public int? DaysBeforeExpiration { get; set; }
}

public class UpdownCert
{
    [JsonPropertyName("subject")]
    public string? Subject { get; set; }

    [JsonPropertyName("issuer")]
    public string? Issuer { get; set; }

    [JsonPropertyName("from")]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    public string? To { get; set; }

    [JsonPropertyName("algorithm")]
    public string? Algorithm { get; set; }
}
