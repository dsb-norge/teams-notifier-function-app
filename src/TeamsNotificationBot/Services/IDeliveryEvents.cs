using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// One App Insights custom event per queued, delivered and failed notification, never sampled,
/// so every delivery can be traced to its caller and its <c>metadata</c> even when the rest of
/// the telemetry is sampled. Fire-and-forget: tracking never throws into the delivery path.
/// </summary>
public interface IDeliveryEvents
{
    void Queued(QueueMessage message);

    void Delivered(QueueMessage message, string partitionKey, string rowKey, long dequeueCount);

    /// <summary>
    /// A delivery attempt that failed. The queue retries transient failures, so a message can
    /// have several of these before it is delivered or lands in the poison queue.
    /// </summary>
    void DeliveryFailed(QueueMessage message, long dequeueCount, string errorType, string error);
}
