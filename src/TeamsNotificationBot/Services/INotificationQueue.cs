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

    /// <summary>
    /// Puts a message the processor isn't ready to deliver back on the queue, invisible for
    /// <paramref name="delay"/>. Unlike throwing, this doesn't use up one of the message's
    /// delivery attempts. No record or event: the message is already recorded.
    /// </summary>
    Task RequeueAsync(QueueMessage message, TimeSpan delay);
}
