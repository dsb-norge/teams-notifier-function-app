using Azure.Storage.Queues.Models;

namespace TeamsNotificationBot.Services;

public interface IQueueManagementService
{
    Task<Dictionary<string, int>> GetQueueStatusAsync();
    Task<List<PeekedMessage>> PeekMessagesAsync(string queueName, int count);
    Task<int> RetryMessagesAsync(string queueName, int count);
    Task<int> RetryAllMessagesAsync(string queueName);
}
