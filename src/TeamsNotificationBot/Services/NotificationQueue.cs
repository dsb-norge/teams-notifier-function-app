using System.Text.Json;
using Azure.Storage.Queues;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class NotificationQueue : INotificationQueue
{
    private readonly QueueClient _queueClient;
    private readonly IDeliveryEvents _events;

    public NotificationQueue(QueueClient queueClient, IDeliveryEvents events)
    {
        _queueClient = queueClient;
        _events = events;
    }

    public async Task EnqueueAsync(QueueMessage message)
    {
        await _queueClient.SendMessageAsync(JsonSerializer.Serialize(message));
        _events.Queued(message);
    }
}
