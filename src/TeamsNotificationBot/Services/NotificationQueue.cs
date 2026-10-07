using System.Text.Json;
using Azure.Storage.Queues;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class NotificationQueue : INotificationQueue
{
    private readonly QueueClient _queueClient;

    public NotificationQueue(QueueClient queueClient)
    {
        _queueClient = queueClient;
    }

    public async Task EnqueueAsync(QueueMessage message)
    {
        await _queueClient.SendMessageAsync(JsonSerializer.Serialize(message));
    }
}
