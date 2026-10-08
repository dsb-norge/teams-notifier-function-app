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

    private QueueMessage NewMessage(string? replyTo = null) => new()
    {
        MessageId = $"msg-{Guid.NewGuid():N}",
        Alias = "Ops-Alerts",
        Source = "notify",
        PrincipalId = "caller-a",
        Message = "hi",
        ReplyTo = replyTo,
        EnqueuedAt = _time.GetUtcNow()
    };

    [Fact]
    public async Task Create_ForADirectTarget_KeepsTheTargetAsRequested()
    {
        var message = NewMessage();
        message.Alias = null;
        message.Source = "send";
        message.Target = new MessageTarget { Type = "personal", UserId = "Jane.Doe@example.com" };

        await _records.CreateAsync(message);
        var record = await _records.GetAsync(message.MessageId);

        Assert.Null(record!.Alias);
        Assert.Equal(message.Target.Key(), record.RequestedTarget);
    }

    [Fact]
    public async Task Create_ThenGet_IsQueued_WithTheCallerAndALowercaseAlias()
    {
        var message = NewMessage(replyTo: "msg-0123456789abcdef0123456789abcdef");

        await _records.CreateAsync(message);
        var record = await _records.GetAsync(message.MessageId);

        Assert.NotNull(record);
        Assert.Equal(DeliveryStatus.Queued, record.Status);
        Assert.Equal("ops-alerts", record.Alias);
        Assert.Equal("caller-a", record.PrincipalId);
        Assert.Equal("notify", record.Source);
        Assert.Equal("msg-0123456789abcdef0123456789abcdef", record.ReplyTo);
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
    public async Task MarkDelivered_RecordsTheUnresolvedMentions()
    {
        var message = NewMessage();

        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "a", "a")
            {
                UnresolvedMentions = ["sam@example.com", "dGFnLWlk"]
            });

        var status = MessageStatusResponse.From((await _records.GetAsync(message.MessageId))!);
        Assert.Equal(["sam@example.com", "dGFnLWlk"], status.UnresolvedMentions);
    }

    [Fact]
    public async Task MarkDelivered_WithEveryMentionResolved_StoresNoList()
    {
        var message = NewMessage();

        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "a", "a"));

        Assert.Null((await _records.GetAsync(message.MessageId))!.UnresolvedMentions);
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
        var created = await _records.CreateAsync(message);

        await _records.DeleteAsync(message.MessageId, created);
        await _records.DeleteAsync(message.MessageId, created);

        Assert.Null(await _records.GetAsync(message.MessageId));
    }

    [Fact]
    public async Task Delete_AfterTheMessageWasClaimed_KeepsTheRecord()
    {
        // The enqueue reported failure but had queued the message, and a processor claimed it.
        var message = NewMessage();
        var created = await _records.CreateAsync(message);
        await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId));

        await _records.DeleteAsync(message.MessageId, created);

        Assert.Equal(DeliveryStatus.Sending, (await _records.GetAsync(message.MessageId))!.Status);
    }

    [Fact]
    public async Task Delete_AfterTheMessageWasDelivered_KeepsTheRecord()
    {
        var message = NewMessage();
        var created = await _records.CreateAsync(message);
        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "a", "a"));

        await _records.DeleteAsync(message.MessageId, created);

        Assert.Equal(DeliveryStatus.Delivered, (await _records.GetAsync(message.MessageId))!.Status);
    }

    // --- Send claims ---

    [Fact]
    public async Task ClaimSend_ConcurrentCopies_ExactlyOneMaySend()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);
        var current = await _records.GetAsync(message.MessageId);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _records.ClaimSendAsync(message, current)));

        Assert.Single(claims, c => c.Status == SendClaimStatus.Claimed);
        Assert.All(claims.Where(c => c.Status != SendClaimStatus.Claimed),
            c => Assert.Equal(SendClaimStatus.InProgress, c.Status));
    }

    [Fact]
    public async Task ClaimSend_WithoutARecord_ClaimsIt()
    {
        // A message queued before delivery records existed.
        Assert.Equal(SendClaimStatus.Claimed, (await _records.ClaimSendAsync(NewMessage(), null)).Status);
    }

    [Fact]
    public async Task ClaimSend_AfterDelivery_SaysAlreadyDelivered()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);
        var stale = await _records.GetAsync(message.MessageId); // read before the other copy delivered
        await _records.MarkDeliveredAsync(message,
            new DeliveryOutcome(PostedAs.Post, ("team-1", "19:c@thread.tacv2"), "conv-1", "a", "a"));

        Assert.Equal(SendClaimStatus.AlreadyDelivered, (await _records.ClaimSendAsync(message, stale)).Status);
    }

    [Fact]
    public async Task ClaimSend_AbandonedClaim_IsTakenOverAfterTheLease()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);
        Assert.Equal(SendClaimStatus.Claimed,
            (await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId))).Status);

        Assert.Equal(SendClaimStatus.InProgress,
            (await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId))).Status);
        _time.Advance(DeliveryRecords.SendLease);
        Assert.Equal(SendClaimStatus.Claimed,
            (await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId))).Status);
    }

    [Fact]
    public async Task MarkFailed_DuringALiveSend_KeepsTheClaim()
    {
        // A leftover poison copy reaches the monitor while a retried copy is sending.
        var message = NewMessage();
        await _records.CreateAsync(message);
        Assert.Equal(SendClaimStatus.Claimed,
            (await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId))).Status);

        await _records.MarkFailedAsync(message, "moved to the poison queue");

        Assert.Equal(DeliveryStatus.Sending, (await _records.GetAsync(message.MessageId))!.Status);
        Assert.Equal(SendClaimStatus.InProgress,
            (await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId))).Status);
    }

    [Fact]
    public async Task MarkFailed_AfterAnAbandonedSend_RecordsTheFailure()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);
        await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId));
        _time.Advance(DeliveryRecords.SendLease);

        await _records.MarkFailedAsync(message, "moved to the poison queue");

        Assert.Equal(DeliveryStatus.Failed, (await _records.GetAsync(message.MessageId))!.Status);
    }

    [Fact]
    public async Task ReleaseSend_LetsTheRetryClaimAtOnce()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);
        var claim = await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId));

        await _records.ReleaseSendAsync(message, claim);

        var record = await _records.GetAsync(message.MessageId);
        Assert.Equal(DeliveryStatus.Queued, record!.Status);
        Assert.Equal(SendClaimStatus.Claimed, (await _records.ClaimSendAsync(message, record)).Status);
    }

    [Fact]
    public async Task ReleaseSend_AfterATakeover_LeavesTheNewClaimAlone()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);
        var stale = await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId));
        _time.Advance(DeliveryRecords.SendLease);
        await _records.ClaimSendAsync(message, await _records.GetAsync(message.MessageId));

        await _records.ReleaseSendAsync(message, stale);

        Assert.Equal(DeliveryStatus.Sending, (await _records.GetAsync(message.MessageId))!.Status);
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

    [Fact]
    public async Task Purge_KeepsAnExpiredRecordWithALiveSendClaim()
    {
        // A message delayed past the retention, being sent right now.
        var message = NewMessage();
        await _records.CreateAsync(message);
        _time.Advance(Retention);
        await _records.ClaimSendAsync(message, null); // GetAsync hides the expired row; the claim takes it over

        await _records.PurgeExpiredAsync(TestContext.Current.CancellationToken);

        Assert.True((await _tableClient.GetEntityIfExistsAsync<TableEntity>(message.MessageId, string.Empty,
            cancellationToken: TestContext.Current.CancellationToken)).HasValue);
    }

    [Fact]
    public async Task Purge_KeepsARecordClaimedWhileThePurgeRan()
    {
        var message = NewMessage();
        await _records.CreateAsync(message);
        _time.Advance(Retention);

        // A TableClient that lets a copy claim the expired record between the purge's query and
        // its delete.
        var racing = new Moq.Mock<TableClient>();
        racing.Setup(t => t.QueryAsync<TableEntity>(
                Moq.It.IsAny<string>(), Moq.It.IsAny<int?>(), Moq.It.IsAny<IEnumerable<string>>(), Moq.It.IsAny<CancellationToken>()))
            .Returns((string filter, int? max, IEnumerable<string> select, CancellationToken ct) =>
                _tableClient.QueryAsync<TableEntity>(filter, max, select, ct));
        racing.Setup(t => t.DeleteEntityAsync(
                Moq.It.IsAny<string>(), Moq.It.IsAny<string>(), Moq.It.IsAny<Azure.ETag>(), Moq.It.IsAny<CancellationToken>()))
            .Returns(async (string pk, string rk, Azure.ETag ifMatch, CancellationToken ct) =>
            {
                if (pk == message.MessageId)
                    Assert.Equal(SendClaimStatus.Claimed, (await _records.ClaimSendAsync(message, null)).Status);
                return await _tableClient.DeleteEntityAsync(pk, rk, ifMatch, ct);
            });
        var purging = new DeliveryRecords(racing.Object, Retention, _time, NullLogger<DeliveryRecords>.Instance);

        await purging.PurgeExpiredAsync(TestContext.Current.CancellationToken);

        Assert.True((await _tableClient.GetEntityIfExistsAsync<TableEntity>(message.MessageId, string.Empty,
            cancellationToken: TestContext.Current.CancellationToken)).HasValue);
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
