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

/// <summary>
/// Every payload Microsoft documents for the common alert schema goes through the alert
/// function and comes out as a queued card. Azure doesn't retry a 400 or a 500, so a
/// documented payload the function refuses is an alert lost without a trace.
/// </summary>
public class AlertFunctionSampleTests
{
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<QueueClient> _queueClient = new();
    private readonly List<string> _queued = [];
    private readonly AlertFunction _function;

    public AlertFunctionSampleTests()
    {
        _function = new AlertFunction(
            _aliasService.Object,
            _botService.Object,
            new NotificationQueue(_queueClient.Object, Mock.Of<IDeliveryRecords>(), Mock.Of<IDeliveryEvents>()),
            NullLogger<AlertFunction>.Instance);

        _aliasService.Setup(s => s.GetAliasAsync("ops")).ReturnsAsync(
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "channel-1" });
        _botService.Setup(b => b.HasConversationAsync(It.IsAny<AliasEntity>())).ReturnsAsync(true);
        _queueClient
            .Setup(q => q.SendMessageAsync(It.IsAny<string>()))
            .Callback<string>(_queued.Add)
            .ReturnsAsync(Mock.Of<Azure.Response<Azure.Storage.Queues.Models.SendReceipt>>());
    }

    [Theory]
    [MemberData(nameof(AzureMonitorAlertSamples.Files), MemberType = typeof(AzureMonitorAlertSamples))]
    public async Task DocumentedSample_IsQueuedAsAValidCard(string file)
    {
        var req = HttpRequestHelper.CreatePostRequest(body: AzureMonitorAlertSamples.Payload(file));

        var result = await _function.Run(req, "ops");

        Assert.Equal(202, Assert.IsType<ObjectResult>(result).StatusCode);
        var queued = JsonSerializer.Deserialize<QueueMessage>(Assert.Single(_queued))!;
        Assert.Equal("adaptive-card", queued.Format);

        var card = JsonDocument.Parse(queued.Message).RootElement;
        var (isValid, error) = AdaptiveCardValidator.Validate(card);
        Assert.True(isValid, error);
    }
}
