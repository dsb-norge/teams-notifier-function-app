using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// The one way onto the <c>notifications</c> queue. Every route that queues a message goes
/// through here, so per-message bookkeeping (delivery records, delivery events) happens in one
/// place for all of them.
/// </summary>
public interface INotificationQueue
{
    /// <summary>
    /// Records <paramref name="message"/> as queued, then serializes and queues it. Throws what
    /// the record store or the queue client throws; a message that wasn't queued must never be
    /// reported as queued.
    /// </summary>
    Task EnqueueAsync(QueueMessage message);
}
