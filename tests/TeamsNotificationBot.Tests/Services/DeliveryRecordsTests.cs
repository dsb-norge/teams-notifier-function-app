using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

/// <summary>The never-throw contract of the record writes, with failures a storage call can't easily produce.</summary>
public class DeliveryRecordsTests
{
    private readonly Mock<TableClient> _tableClient = new();
    private readonly DeliveryRecords _records;

    public DeliveryRecordsTests()
    {
        _records = new DeliveryRecords(_tableClient.Object, TimeSpan.FromDays(180), TimeProvider.System,
            NullLogger<DeliveryRecords>.Instance);
    }

    public static TheoryData<Exception> Failures => new()
    {
        new RequestFailedException(503, "unavailable"),
        new Azure.Identity.AuthenticationFailedException("token acquisition failed"),
        new OperationCanceledException()
    };

    private static QueueMessage Message() => new() { MessageId = "msg-0123456789abcdef0123456789abcdef" };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task MarkDelivered_NeverThrows_BecauseTeamsAlreadyHasTheMessage(Exception failure)
    {
        _tableClient.Setup(t => t.UpsertEntityAsync(It.IsAny<DeliveryRecordEntity>(), It.IsAny<TableUpdateMode>(), default))
            .ThrowsAsync(failure);

        await _records.MarkDeliveredAsync(Message(),
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "a", "a"));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task MarkFailed_NeverThrows(Exception failure)
    {
        _tableClient.Setup(t => t.GetEntityAsync<DeliveryRecordEntity>(It.IsAny<string>(), It.IsAny<string>(), null, default))
            .ThrowsAsync(failure);

        await _records.MarkFailedAsync(Message(), "boom");
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Delete_NeverThrows_SoTheEnqueueFailurePropagates(Exception failure)
    {
        _tableClient.Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), default))
            .ThrowsAsync(failure);

        await _records.DeleteAsync("msg-0123456789abcdef0123456789abcdef");
    }
}
