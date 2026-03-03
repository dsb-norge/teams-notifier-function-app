using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class AlertFunctionTests
{
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly AlertFunction _function;

    private const string ValidAlertPayload = """
        {
            "schemaId": "azureMonitorCommonAlertSchema",
            "data": {
                "essentials": {
                    "alertRule": "HighCPU",
                    "severity": "Sev2",
                    "monitorCondition": "Fired",
                    "signalType": "Metric",
                    "firedDateTime": "2026-02-14T12:00:00Z",
                    "description": "CPU usage exceeded 90%",
                    "alertTargetIDs": ["/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Web/sites/func-test"],
                    "monitoringService": "Platform"
                }
            }
        }
        """;

    public AlertFunctionTests()
    {
        _function = new AlertFunction(
            _aliasService.Object,
            _queueClient.Object,
            NullLogger<AlertFunction>.Instance);
    }

    [Fact]
    public async Task ValidAlert_Returns202AndEnqueues()
    {
        _aliasService.Setup(s => s.GetAliasAsync("devops-test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());

        var req = HttpRequestHelper.CreatePostRequest(body: ValidAlertPayload);

        var result = await _function.Run(req, "devops-test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(202, objectResult.StatusCode);

        var json = JsonSerializer.Serialize(objectResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());

        // Verify the enqueued message contains adaptive-card format
        _queueClient.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => s.Contains("adaptive-card") && s.Contains("HighCPU"))), Times.Once);
    }

    [Fact]
    public async Task UnknownAlias_Returns404()
    {
        _aliasService.Setup(s => s.GetAliasAsync("unknown")).ReturnsAsync((AliasEntity?)null);
        var req = HttpRequestHelper.CreatePostRequest(body: ValidAlertPayload);

        var result = await _function.Run(req, "unknown");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(404, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Not Found", problem.Title);
    }

    [Fact]
    public async Task InvalidSchema_Returns400()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var req = HttpRequestHelper.CreatePostRequest(body: """
            {
                "schemaId": "someOtherSchema",
                "data": { "essentials": { "alertRule": "test" } }
            }
            """);

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Contains("someOtherSchema", problem.Detail);
    }

    [Fact]
    public async Task NonJsonContentType_Returns415()
    {
        var req = HttpRequestHelper.CreatePostRequest(body: "text", contentType: "text/plain");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(415, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }

    [Fact]
    public async Task InvalidJson_Returns400()
    {
        _aliasService.Setup(s => s.GetAliasAsync("test")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });

        var req = HttpRequestHelper.CreatePostRequest(body: "{{invalid json}}");

        var result = await _function.Run(req, "test");

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, objectResult.StatusCode);
        Assert.IsType<ProblemDetails>(objectResult.Value);
    }
}
