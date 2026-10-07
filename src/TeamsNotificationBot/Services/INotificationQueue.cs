using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// The one way onto the <c>notifications</c> queue. Every route that queues a message goes
/// through here, so per-message bookkeeping (delivery events, delivery records) happens in one
/// place for all of them.
/// </summary>
public interface INotificationQueue
{
    /// <summary>
    /// Serializes <paramref name="message"/> and queues it. Throws what the queue client throws;
    /// a message that wasn't queued must never be reported as queued.
    /// </summary>
    Task EnqueueAsync(QueueMessage message);
}
