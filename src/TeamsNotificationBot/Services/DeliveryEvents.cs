using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;
using static TeamsNotificationBot.Helpers.LogSanitizer;

namespace TeamsNotificationBot.Services;

public class DeliveryEvents : IDeliveryEvents
{
    public const string QueuedEventName = "NotificationQueued";
    public const string DeliveredEventName = "NotificationDelivered";
    public const string DeliveryFailedEventName = "NotificationDeliveryFailed";

    /// <summary>Prefix for the caller's <c>metadata</c> entries among the event properties.</summary>
    public const string MetadataPropertyPrefix = "meta.";

    // App Insights caps a custom property value at 8192 characters; an exception message has no
    // such bound of its own.
    private const int MaxErrorLength = 1024;

    /// <summary>How long <see cref="FlushAsync"/> may hold up the invocation that calls it.</summary>
    public static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(5);

    private readonly TelemetryClient _telemetry;
    private readonly ILogger<DeliveryEvents> _logger;

    public DeliveryEvents(TelemetryClient telemetry, ILogger<DeliveryEvents> logger)
    {
        _telemetry = telemetry;
        _logger = logger;
    }

    public void Queued(QueueMessage message) => Track(QueuedEventName, message, null);

    public void Delivered(QueueMessage message, string partitionKey, string rowKey, long dequeueCount) =>
        Track(DeliveredEventName, message, p =>
        {
            p["PartitionKey"] = partitionKey;
            p["RowKey"] = rowKey;
            p["DequeueCount"] = dequeueCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            p["LatencyMs"] = ((long)(DateTimeOffset.UtcNow - message.EnqueuedAt).TotalMilliseconds)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

    public void DeliveryFailed(QueueMessage message, long dequeueCount, string errorType, string error) =>
        Track(DeliveryFailedEventName, message, p =>
        {
            p["DequeueCount"] = dequeueCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            p["ErrorType"] = errorType;
            p["Error"] = Sanitize(error.Length > MaxErrorLength ? error[..MaxErrorLength] : error);
        });

    /// <summary>Builds the event without sending it. <c>internal</c> for the tests.</summary>
    internal static EventTelemetry Build(
        string name, QueueMessage message, Action<IDictionary<string, string>>? addProperties)
    {
        var evt = new EventTelemetry(name);

        // A sampling percentage pinned to 100 keeps the item out of SDK sampling: the App
        // Insights SDK documents this as the way to always keep chosen items, and telemetry
        // initializers and sampling processors run after it is set here. Portal ingestion
        // sampling, if anyone turns it on, still applies.
        ((ISupportSampling)evt).SamplingPercentage = 100;

        var p = evt.Properties;
        p["MessageId"] = message.MessageId;
        p["Source"] = message.Source ?? string.Empty;
        p["PrincipalId"] = Sanitize(message.PrincipalId);
        p["Format"] = Sanitize(message.Format);
        if (!string.IsNullOrEmpty(message.Alias))
            p["Alias"] = Sanitize(message.Alias);
        if (message.Target is { } target)
        {
            p["TargetType"] = Sanitize(target.Type);
            AddIfPresent(p, "TeamId", target.TeamId);
            AddIfPresent(p, "ChannelId", target.ChannelId);
            AddIfPresent(p, "UserId", target.UserId);
            AddIfPresent(p, "ChatId", target.ChatId);
        }
        if (message.Metadata != null)
        {
            foreach (var (key, value) in message.Metadata)
                p[MetadataPropertyPrefix + Sanitize(key)] = Sanitize(value);
        }

        addProperties?.Invoke(p);
        return evt;
    }

    public async Task FlushAsync()
    {
        using var timeout = new CancellationTokenSource(FlushTimeout);
        try
        {
            await _telemetry.FlushAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): this runs at the end of every delivery attempt,
            // after the outcome is decided. Throwing would turn a delivered message into a failed
            // attempt, and the queue would post it again.
            _logger.LogDebug(ex, "Could not flush telemetry");
        }
    }

    private void Track(string name, QueueMessage message, Action<IDictionary<string, string>>? addProperties)
    {
        try
        {
            _telemetry.TrackEvent(Build(name, message, addProperties));
        }
        catch (Exception ex)
        {
            // Side concern (docs/contributing.md §5): these calls come right after a message was
            // queued or delivered. Throwing would make the caller treat a queued message as failed
            // (and release its idempotency claim) or make the queue post a delivered one again.
            _logger.LogWarning(ex, "Could not track {EventName}. MessageId={MessageId}", name, message.MessageId);
        }
    }

    private static void AddIfPresent(IDictionary<string, string> properties, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            properties[name] = Sanitize(value);
    }
}
