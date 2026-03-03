using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class CheckInFunctionTests
{
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly CheckInFunction _function;

    public CheckInFunctionTests()
    {
        _function = new CheckInFunction(
            _aliasService.Object,
            _queueClient.Object,
            NullLogger<CheckInFunction>.Instance);
    }

    [Fact]
    public async Task ValidAlias_Returns202AndQueuesMessage()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(body: """{"source": "unit-test"}""");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        var json = JsonSerializer.Serialize(objectResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(AppInfo.Version, doc.RootElement.GetProperty("version").GetString());
        Assert.True(doc.RootElement.TryGetProperty("messageId", out _));
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));

        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("Check-in") && s.Contains("unit-test"))), Times.Once);
    }

    [Fact]
    public async Task UnknownAlias_Returns404ProblemDetails()
    {
        _aliasService.Setup(s => s.GetAliasAsync("unknown")).ReturnsAsync((AliasEntity?)null);
        var req = HttpRequestHelper.CreatePostRequest(body: """{"source": "test"}""");

        var result = await _function.Run(req, "unknown");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Not Found", problem.Title);
        Assert.Contains("unknown", problem.Detail);
    }

    [Fact]
    public async Task EmptyBody_Returns202WithDefaultSource()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest();

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("unknown"))), Times.Once);
    }

    [Fact]
    public async Task MalformedBody_Returns202WithDefaultSource()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(body: "not-json");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
    }
}
