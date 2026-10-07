using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class GetMessageFunctionTests
{
    private const string MessageId = "msg-0123456789abcdef0123456789abcdef";

    private readonly Mock<IDeliveryRecords> _records = new();
    private readonly GetMessageFunction _function;

    public GetMessageFunctionTests()
    {
        _function = new GetMessageFunction(_records.Object, NullLogger<GetMessageFunction>.Instance);
    }

    [Fact]
    public async Task Delivered_ReturnsStatusPostedAsAndTarget()
    {
        var enqueued = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        _records.Setup(r => r.GetAsync(MessageId)).ReturnsAsync(new DeliveryRecordEntity
        {
            PartitionKey = MessageId,
            Status = DeliveryStatus.Delivered,
            PostedAs = PostedAs.Reply,
            TargetType = "channel",
            TeamId = "team-1",
            ChannelId = "19:c@thread.tacv2",
            ActivityId = "internal-activity-id",
            EnqueuedAt = enqueued,
            DeliveredAt = enqueued.AddSeconds(3)
        });

        var result = await _function.Run(HttpRequestHelper.CreateGetRequest(), MessageId);

        var ok = Assert.IsType<OkObjectResult>(result);
        var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        Assert.Equal(MessageId, json.GetProperty("messageId").GetString());
        Assert.Equal("delivered", json.GetProperty("status").GetString());
        Assert.Equal("reply", json.GetProperty("postedAs").GetString());
        Assert.Equal("channel", json.GetProperty("target").GetProperty("type").GetString());
        Assert.Equal("19:c@thread.tacv2", json.GetProperty("target").GetProperty("channelId").GetString());
        Assert.Equal(0, json.GetProperty("unresolvedMentions").GetArrayLength());
        Assert.DoesNotContain("internal-activity-id", json.GetRawText());
    }

    [Fact]
    public async Task Queued_HasNoTargetYet()
    {
        _records.Setup(r => r.GetAsync(MessageId)).ReturnsAsync(
            new DeliveryRecordEntity { PartitionKey = MessageId, Status = DeliveryStatus.Queued });

        var ok = Assert.IsType<OkObjectResult>(await _function.Run(HttpRequestHelper.CreateGetRequest(), MessageId));

        var json = JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
        Assert.Equal("queued", json.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("target").ValueKind);
    }

    [Fact]
    public async Task UnknownOrExpired_Returns404()
    {
        var result = await _function.Run(HttpRequestHelper.CreateGetRequest(), MessageId);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Theory]
    [InlineData("not-a-message-id")]
    [InlineData("msg-0123/../x")]
    [InlineData("msg-0123456789ABCDEF0123456789ABCDEF")]
    public async Task MalformedId_Returns404_WithoutTouchingStorage(string id)
    {
        var result = await _function.Run(HttpRequestHelper.CreateGetRequest(), id);

        Assert.Equal(404, Assert.IsType<ObjectResult>(result).StatusCode);
        _records.VerifyNoOtherCalls();
    }
}
