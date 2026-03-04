using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TeamsNotificationBot.Services;

public class QueueManagementService : IQueueManagementService
{
    private static readonly Dictionary<string, string> PoisonToMainMap = new()
    {
        ["notifications-poison"] = "notifications",
        ["botoperations-poison"] = "botoperations"
    };

    private readonly QueueClient _notificationsQueue;
    private readonly QueueClient _botOperationsQueue;
    private readonly QueueClient _notificationsPoisonQueue;
    private readonly QueueClient _botOperationsPoisonQueue;
    private readonly ILogger<QueueManagementService> _logger;

    public QueueManagementService(
        QueueClient notificationsQueue,
        [FromKeyedServices("botoperations")] QueueClient botOperationsQueue,
        [FromKeyedServices("notifications-poison")] QueueClient notificationsPoisonQueue,
        [FromKeyedServices("botoperations-poison")] QueueClient botOperationsPoisonQueue,
        ILogger<QueueManagementService> logger)
    {
        _notificationsQueue = notificationsQueue;
        _botOperationsQueue = botOperationsQueue;
        _notificationsPoisonQueue = notificationsPoisonQueue;
        _botOperationsPoisonQueue = botOperationsPoisonQueue;
        _logger = logger;
    }

    public async Task<Dictionary<string, int>> GetQueueStatusAsync()
    {
        var result = new Dictionary<string, int>();

        var queues = new (string name, QueueClient client)[]
        {
            ("notifications", _notificationsQueue),
            ("botoperations", _botOperationsQueue),
            ("notifications-poison", _notificationsPoisonQueue),
            ("botoperations-poison", _botOperationsPoisonQueue)
        };

        foreach (var (name, client) in queues)
        {
            try
            {
                var props = await client.GetPropertiesAsync();
                result[name] = props.Value.ApproximateMessagesCount;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get properties for queue {Queue}", name);
                result[name] = -1;
            }
        }

        return result;
    }

    public async Task<List<PeekedMessage>> PeekMessagesAsync(string queueName, int count)
    {
        if (count < 1)
            return [];

        var client = GetPoisonClient(queueName);
        var response = await client.PeekMessagesAsync(Math.Min(count, 32));
        return [.. response.Value];
    }

    public async Task<int> RetryMessagesAsync(string queueName, int count)
    {
        var poisonClient = GetPoisonClient(queueName);
        var mainClient = GetMainClient(queueName);
        var retried = 0;

        for (var i = 0; i < count; i++)
        {
            var messages = await poisonClient.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(30));
            if (messages.Value.Length == 0) break;

            var msg = messages.Value[0];
            await mainClient.SendMessageAsync(msg.Body.ToString());
            await poisonClient.DeleteMessageAsync(msg.MessageId, msg.PopReceipt);
            retried++;
        }

        _logger.LogInformation("Retried {Count} messages from {Queue}", retried, queueName);
        return retried;
    }

    public async Task<int> RetryAllMessagesAsync(string queueName)
    {
        var poisonClient = GetPoisonClient(queueName);
        var mainClient = GetMainClient(queueName);
        var retried = 0;

        while (true)
        {
            var messages = await poisonClient.ReceiveMessagesAsync(32, TimeSpan.FromSeconds(30));
            if (messages.Value.Length == 0) break;

            foreach (var msg in messages.Value)
            {
                await mainClient.SendMessageAsync(msg.Body.ToString());
                await poisonClient.DeleteMessageAsync(msg.MessageId, msg.PopReceipt);
                retried++;
            }
        }

        _logger.LogInformation("Retried all {Count} messages from {Queue}", retried, queueName);
        return retried;
    }

    private QueueClient GetPoisonClient(string queueName)
    {
        return queueName switch
        {
            "notifications-poison" => _notificationsPoisonQueue,
            "botoperations-poison" => _botOperationsPoisonQueue,
            _ => throw new ArgumentException($"Invalid poison queue name: {queueName}. Valid: notifications-poison, botoperations-poison")
        };
    }

    private QueueClient GetMainClient(string queueName)
    {
        if (!PoisonToMainMap.TryGetValue(queueName, out var mainQueue))
            throw new ArgumentException($"No main queue mapping for: {queueName}");

        return mainQueue switch
        {
            "notifications" => _notificationsQueue,
            "botoperations" => _botOperationsQueue,
            _ => throw new InvalidOperationException($"Unknown main queue: {mainQueue}")
        };
    }
}
