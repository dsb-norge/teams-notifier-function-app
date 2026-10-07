using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Services;

[Collection("Azurite")]
public class DeliveryRecordsIntegrationTests
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(180);

    private readonly TableClient _tableClient;
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly DeliveryRecords _records;

    public DeliveryRecordsIntegrationTests(AzuriteFixture azurite)
    {
        _tableClient = azurite.CreateTableClient("deliveryrecords");
        _records = new DeliveryRecords(_tableClient, Retention, _time, NullLogger<DeliveryRecords>.Instance);
    }

    private QueueMessage NewMessage() => new()
    {
        MessageId = $"msg-{Guid.NewGuid():N}",
        Alias = "Ops-Alerts",
        Source = "notify",
        PrincipalId = "caller-a",
        Message = "hi",
        EnqueuedAt = _time.GetUtcNow()
    };

    [Fact]
    public async Task Create_ThenGet_IsQueued_WithTheCallerAndALowercaseAlias()
    {
        var message = NewMessage();

        await _records.CreateAsync(message);
        var record = await _records.GetAsync(message.MessageId);

        Assert.NotNull(record);
        Assert.Equal(DeliveryStatus.Queued, record.Status);
        Assert.Equal("ops-alerts", record.Alias);
        Assert.Equal("caller-a", record.PrincipalId);
        Assert.Equal("notify", record.Source);
        Assert.Null(record.TargetType);
    }

    [Fact]
    public async Task Get_Unknown_IsNull()
    {
        Assert.Null(await _records.GetAsync($"msg-{Guid.NewGuid():N}"));
    }

    [Theory]
    [InlineData("team-1", "19:c@thread.tacv2", "channel")]
    [InlineData("user", "oid-1", "personal")]
    [InlineData("chat", "19:chat@thread.v2", "groupChat")]
    public async Task MarkDelivered_RecordsWhereItWent(string pk, string rk, string expectedType)
    {
        var message = NewMessage();
        await _records.CreateAsync(message);

        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, (pk, rk), "conv-1", "activity-1", "activity-1"));
        var record = await _records.GetAsync(message.MessageId);

        Assert.Equal(DeliveryStatus.Delivered, record!.Status);
        Assert.Equal(expectedType, record.TargetType);
        Assert.Equal((pk, rk), record.ConversationKey());
        Assert.Equal("conv-1", record.ConversationId);
        Assert.Equal("activity-1", record.ActivityId);
        Assert.Equal(PostedAs.Post, record.PostedAs);
        Assert.Equal(_time.GetUtcNow(), record.DeliveredAt);
    }

    [Fact]
    public async Task MarkDelivered_AfterAFailure_ClearsTheError()
    {
        var message = NewMessage();
        await _records.MarkFailedAsync(message, "boom");

        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "a", "a"));

        Assert.Null((await _records.GetAsync(message.MessageId))!.Error);
    }

    [Fact]
    public async Task MarkDelivered_WithoutAQueuedRecord_StillRecordsIt()
    {
        // A message queued before delivery records existed.
        var message = NewMessage();

        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "a", "a"));

        Assert.Equal(DeliveryStatus.Delivered, (await _records.GetAsync(message.MessageId))!.Status);
    }

    [Fact]
    public async Task MarkFailed_AfterADelivery_LeavesItDelivered()
    {
        // A retried poison message delivered, then its leftover poison copy reached the monitor.
        var message = NewMessage();
        await _records.CreateAsync(message);
        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "activity-1", "activity-1"));

        await _records.MarkFailedAsync(message, "moved to the poison queue");

        var record = await _records.GetAsync(message.MessageId);
        Assert.Equal(DeliveryStatus.Delivered, record!.Status);
        Assert.Equal("activity-1", record.ActivityId);
        Assert.Null(record.Error);
    }

    [Fact]
    public async Task MarkFailed_WithoutARecord_RecordsTheFailure()
    {
        var message = NewMessage();

        await _records.MarkFailedAsync(message, "boom");

        Assert.Equal(DeliveryStatus.Failed, (await _records.GetAsync(message.MessageId))!.Status);
    }

    [Fact]
    public async Task MarkFailed_KeepsATruncatedError()
    {
        var message = NewMessage();

        await _records.MarkFailedAsync(message, new string('x', 5000));
        var record = await _records.GetAsync(message.MessageId);

        Assert.Equal(DeliveryStatus.Failed, record!.Status);
        Assert.Equal(1024, record.Error!.Length);
    }

    [Fact]
    public async Task Delete_RemovesTheRecord_AndToleratesAMissingOne()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);

        await _records.DeleteAsync(message.MessageId);
        await _records.DeleteAsync(message.MessageId);

        Assert.Null(await _records.GetAsync(message.MessageId));
    }

    [Fact]
    public async Task Get_AfterRetention_IsNull_AndPurgeDeletesIt()
    {
        var old = NewMessage();
        await _records.CreateAsync(old);
        _time.Advance(Retention);
        var fresh = NewMessage();
        await _records.CreateAsync(fresh);

        Assert.Null(await _records.GetAsync(old.MessageId));

        await _records.PurgeExpiredAsync(TestContext.Current.CancellationToken);

        Assert.False((await _tableClient.GetEntityIfExistsAsync<TableEntity>(old.MessageId, string.Empty,
            cancellationToken: TestContext.Current.CancellationToken)).HasValue);
        Assert.NotNull(await _records.GetAsync(fresh.MessageId));
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
