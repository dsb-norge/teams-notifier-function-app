using System.Text;
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

    /// <summary>
    /// Storage's limit for a queue message. The client Base64-encodes messages (Program.cs), so
    /// the encoded size is what counts: four bytes for every three of the JSON.
    /// </summary>
    public const int MaxEncodedMessageBytes = 64 * 1024;

    internal static int EncodedSize(string json) => (Encoding.UTF8.GetByteCount(json) + 2) / 3 * 4;

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
        // Measured before the record is written, so a message the queue would refuse leaves
        // nothing behind, and the caller hears why instead of getting Storage's 413 as a 500.
        var json = JsonSerializer.Serialize(message, EnvelopeOptions);
        var size = EncodedSize(json);
        if (size > MaxEncodedMessageBytes)
            throw new QueueMessageTooLargeException(size);

        // The record first, so a status read right after the 202 finds the message.
        var created = await _records.CreateAsync(message);
        try
        {
            await _queueClient.SendMessageAsync(json);
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
