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
        AssertNoEmptyText(card);
        Assert.True(AlertCardBuilder.QueuedSize(queued.Message) <= AlertCardBuilder.MaxQueuedCardBytes);
    }

    [Theory]
    [MemberData(nameof(AzureMonitorAlertSamples.Files), MemberType = typeof(AzureMonitorAlertSamples))]
    public async Task DocumentedSample_ShowsItsContext_AndNothingThatIsHidden(string file)
    {
        var payload = JsonDocument.Parse(AzureMonitorAlertSamples.Payload(file)).RootElement.GetProperty("data");
        var req = HttpRequestHelper.CreatePostRequest(body: AzureMonitorAlertSamples.Payload(file));

        await _function.Run(req, "ops");

        var body = JsonDocument.Parse(JsonSerializer.Deserialize<QueueMessage>(Assert.Single(_queued))!.Message)
            .RootElement.GetProperty("body").EnumerateArray().ToList();

        // Every documented alert has a context, and it is behind the button.
        var details = Assert.Single(body, b => b.GetProperty("type").GetString() == "ActionSet")
            .GetProperty("actions")[0].GetProperty("card").GetProperty("body")[0].GetProperty("facts");
        Assert.NotEqual(0, details.GetArrayLength());

        // No title anywhere on the card names a hidden field.
        var titles = details.EnumerateArray().Select(f => f.GetProperty("title").GetString()!.Replace("\u200B", ""));
        Assert.DoesNotContain(titles, t => t.Split('.', '[').Any(segment =>
            segment is "claims" or "httpRequest" or "authorization" or "SearchResults" or "impactedServicesTableRows"
                or "defaultLanguageTitle" or "defaultLanguageContent" ||
            (segment.StartsWith("linkTo", StringComparison.OrdinalIgnoreCase) && segment.EndsWith("API", StringComparison.OrdinalIgnoreCase))));

        // HTML in a value (Service Health's communication) is shown as text.
        Assert.DoesNotContain(details.EnumerateArray().Select(f => f.GetProperty("value").GetString()!),
            v => v.Contains("<p>", StringComparison.OrdinalIgnoreCase));

        // A sample with custom properties shows them.
        var hasCustom = payload.TryGetProperty("customProperties", out var custom) && custom.ValueKind == JsonValueKind.Object;
        Assert.Equal(hasCustom, body.Any(b => b.TryGetProperty("text", out var t) && t.GetString() == "Custom properties"));
    }

    /// <summary>
    /// A field Azure sent as null must be left out, not rendered as an empty fact: budget
    /// alerts have no rule, severity or signal type, and once rendered them as null values.
    /// </summary>
    private static void AssertNoEmptyText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "text" or "value" or "title")
                    {
                        Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
                        Assert.False(string.IsNullOrWhiteSpace(property.Value.GetString()),
                            $"empty '{property.Name}' in {element}");
                    }
                    AssertNoEmptyText(property.Value);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertNoEmptyText(item);
                break;
        }
    }
}
