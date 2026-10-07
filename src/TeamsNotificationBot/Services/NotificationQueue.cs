using System.Text.Json;
using Azure.Storage.Queues;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class NotificationQueue : INotificationQueue
{
    private readonly QueueClient _queueClient;
    private readonly IDeliveryRecords _records;
    private readonly IDeliveryEvents _events;

    public NotificationQueue(QueueClient queueClient, IDeliveryRecords records, IDeliveryEvents events)
    {
        _queueClient = queueClient;
        _records = records;
        _events = events;
    }

    public async Task EnqueueAsync(QueueMessage message)
    {
        // The record first, so a status read right after the 202 finds the message.
        await _records.CreateAsync(message);
        try
        {
            await _queueClient.SendMessageAsync(JsonSerializer.Serialize(message));
        }
        catch
        {
            await _records.DeleteAsync(message.MessageId);
            throw;
        }
        _events.Queued(message);
    }
}
