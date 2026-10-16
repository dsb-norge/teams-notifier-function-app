namespace TeamsNotificationBot.Services;

/// <summary>
/// The message is larger than the queue takes once Base64-encoded. Thrown by
/// <see cref="INotificationQueue.EnqueueAsync"/> before anything is recorded or queued, so the
/// routes whose callers write the message can answer it with a 413.
/// </summary>
public sealed class QueueMessageTooLargeException(int encodedBytes)
    : Exception($"The message would be {encodedBytes} bytes in the queue, over its limit of {NotificationQueue.MaxEncodedMessageBytes}.")
{
    public int EncodedBytes { get; } = encodedBytes;
}
