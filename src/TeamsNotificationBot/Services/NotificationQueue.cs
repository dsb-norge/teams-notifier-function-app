using System.Text.Encodings.Web;
using System.Text.Json;
using Azure.Storage.Queues;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class NotificationQueue : INotificationQueue
{
    /// <summary>
    /// The envelope escapes only what JSON requires. The default encoder also writes every
    /// quote as <c>\u0022</c> and every non-ASCII character as <c>\uXXXX</c>, so an Adaptive
    /// Card within the request limit grew to three times its size in the queue, and so did
    /// Norwegian text. That extra escaping guards JSON embedded in HTML; nothing renders the
    /// envelope, and every reader parses either form, so messages already queued still work.
    /// </summary>
    internal static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

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
        var created = await _records.CreateAsync(message);
        try
        {
            await _queueClient.SendMessageAsync(JsonSerializer.Serialize(message, EnvelopeOptions));
        }
        catch
        {
            await _records.DeleteAsync(message.MessageId, created);
            throw;
        }
        _events.Queued(message);
    }

    public async Task RequeueAsync(QueueMessage message, TimeSpan delay)
    {
        await _queueClient.SendMessageAsync(JsonSerializer.Serialize(message, EnvelopeOptions), visibilityTimeout: delay);
    }
}
