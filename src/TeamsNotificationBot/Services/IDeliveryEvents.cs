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

    /// <summary>
    /// Sends what has been tracked and logged so far, before the instance can go away. Telemetry
    /// is buffered and sent every few seconds, and Flex Consumption can scale a queue trigger's
    /// instance in right after an invocation, losing what is still buffered: the delivery events
    /// above, and the warnings that explain a failure. Bounded to a few seconds; never throws.
    /// </summary>
    Task FlushAsync();
}
