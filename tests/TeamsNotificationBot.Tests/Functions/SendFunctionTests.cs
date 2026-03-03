using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class SendFunctionTests
{
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly SendFunction _function;

    public SendFunctionTests()
    {
        _function = new SendFunction(
            _queueClient.Object,
            NullLogger<SendFunction>.Instance);
    }

    [Fact]
    public async Task ValidChannelTarget_Returns202()
    {
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "channel", "teamId": "team-1", "channelId": "channel-1" },
                "message": "Hello",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        var json = JsonSerializer.Serialize(objectResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());

        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("Hello") && s.Contains("channel"))), Times.Once);
    }

    [Fact]
    public async Task ValidPersonalTarget_Returns202()
    {
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "personal", "userId": "user-abc" },
                "message": "Hello user",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);
    }

    [Fact]
    public async Task MissingTargetType_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "teamId": "team-1" },
                "message": "Hello",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("target.type", problem.Detail);
    }

    [Fact]
    public async Task ChannelMissingTeamId_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "channel", "channelId": "channel-1" },
                "message": "Hello",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("teamId", problem.Detail);
    }

    [Fact]
    public async Task InvalidContentType_Returns415()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: "text", contentType: "text/plain");

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(415, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task EmptyMessage_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "target": { "type": "channel", "teamId": "team-1", "channelId": "channel-1" },
                "message": "",
                "format": "text"
            }
            """);

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("Message", problem.Detail);
    }

    [Fact]
    public async Task InvalidJson_Returns400()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: "{{invalid json}}");

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }
}
