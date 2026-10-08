using System.Security.Claims;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Agents.Hosting.AspNetCore.BackgroundQueue;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

/// <summary>
/// The proactive send and update paths, up to the Bot Framework call. The CloudAdapter is mocked
/// (its constructor takes interfaces, and ContinueConversationAsync is virtual) and runs the
/// callback against a mocked turn context, so these tests see the conversation reference and the
/// activities BotService builds. What Teams does with them is for the manual pass on dev.
/// </summary>
// Joins the "Azurite" collection only to serialize with the classes that set
// TEAMS_INTEGRATION_DISABLED, which BotService's constructor captures.
[Collection("Azurite")]
public class BotServiceSendTests : IDisposable
{
    private const string Channel = "19:channel@thread.tacv2";

    private readonly Mock<TableClient> _tableClient = new();
    private readonly Mock<CloudAdapter> _adapter =
        new(Mock.Of<IChannelServiceClientFactory>(), Mock.Of<IActivityTaskQueue>(), null!, null!, null!, null!, null!);
    private readonly Mock<ITurnContext> _turnContext = new();

    private ConversationReference? _usedReference;
    private readonly List<IActivity> _sent = [];
    private readonly List<IActivity> _updated = [];

    public BotServiceSendTests()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);

        _adapter
            .Setup(a => a.ContinueConversationAsync(
                It.IsAny<ClaimsIdentity>(), It.IsAny<ConversationReference>(),
                It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()))
            .Returns((ClaimsIdentity _, ConversationReference reference, AgentCallbackHandler callback, CancellationToken ct) =>
            {
                _usedReference = reference;
                return callback(_turnContext.Object, ct);
            });
        _turnContext
            .Setup(t => t.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .Callback<IActivity, CancellationToken>((a, _) => _sent.Add(a))
            .ReturnsAsync(new ResourceResponse("activity-123"));
        _turnContext
            .Setup(t => t.UpdateActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .Callback<IActivity, CancellationToken>((a, _) => _updated.Add(a))
            .ReturnsAsync(new ResourceResponse("activity-root"));

        StoreReference("team-1", "channel-1", Channel);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);

    private BotService NewService() =>
        new(_adapter.Object, _tableClient.Object, NullLogger<BotService>.Instance, null!, null!);

    private void StoreReference(string pk, string rk, string conversationId)
    {
        var entity = new ConversationReferenceEntity
        {
            PartitionKey = pk,
            RowKey = rk,
            ConversationReference = JsonSerializer.Serialize(new ConversationReference
            {
                ServiceUrl = "https://smba.trafficmanager.net/emea/",
                ChannelId = "msteams",
                Conversation = new ConversationAccount { Id = conversationId, ConversationType = "channel", IsGroup = true }
            })
        };
        _tableClient
            .Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(pk, rk, null, default))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));
    }

    [Fact]
    public async Task Send_Text_ReturnsTheConversationAndTheActivityTeamsAssigned()
    {
        var sent = await NewService().SendAsync("team-1", "channel-1", "text", "Hello");

        Assert.Equal(Channel, sent.ConversationId);
        Assert.Equal("activity-123", sent.ActivityId);
        Assert.Equal("Hello", Assert.Single(_sent).Text);
    }

    [Fact]
    public async Task Send_Card_SendsAnAdaptiveCardAttachment()
    {
        await NewService().SendAsync("team-1", "channel-1", "adaptive-card",
            """{"type":"AdaptiveCard","version":"1.4","body":[]}""");

        var attachment = Assert.Single(Assert.Single(_sent).Attachments);
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.ContentType);
    }

    [Fact]
    public async Task Send_InAThread_AddressesTheThreadRootInTheChannel()
    {
        var sent = await NewService().SendAsync("team-1", "channel-1", "text", "Hello", threadActivityId: "1712345678901");

        Assert.Equal($"{Channel};messageid=1712345678901", _usedReference!.Conversation.Id);
        Assert.Equal($"{Channel};messageid=1712345678901", sent.ConversationId);
    }

    [Fact]
    public async Task Send_InAThread_ReplacesAThreadSuffixTheStoredReferenceAlreadyHas()
    {
        StoreReference("team-1", "channel-1", $"{Channel};messageid=999");

        await NewService().SendAsync("team-1", "channel-1", "text", "Hello", threadActivityId: "1712345678901");

        Assert.Equal($"{Channel};messageid=1712345678901", _usedReference!.Conversation.Id);
    }

    [Fact]
    public async Task Update_ReplacesTheActivity_InTheConversationItWasPostedTo()
    {
        await NewService().UpdateAsync("team-1", "channel-1", $"{Channel};messageid=1712345678901",
            "activity-root", "text", "Resolved");

        Assert.Equal($"{Channel};messageid=1712345678901", _usedReference!.Conversation.Id);
        var activity = Assert.Single(_updated);
        Assert.Equal("activity-root", activity.Id);
        Assert.Equal("Resolved", activity.Text);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Send_WithoutAStoredReference_Throws_SoTheQueueRetries()
    {
        _tableClient
            .Setup(t => t.GetEntityAsync<ConversationReferenceEntity>("team-1", "gone", null, default))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService().SendAsync("team-1", "gone", "text", "Hello"));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Update_WithoutAStoredReference_Throws()
    {
        _tableClient
            .Setup(t => t.GetEntityAsync<ConversationReferenceEntity>("team-1", "gone", null, default))
            .ThrowsAsync(new RequestFailedException(404, "not found"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService().UpdateAsync("team-1", "gone", Channel, "activity-root", "text", "Resolved"));
    }

    [Fact]
    public async Task TeamsDisabled_SendsNothing()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", "true");
        var service = NewService();

        var sent = await service.SendAsync("team-1", "channel-1", "text", "Hello");
        await service.UpdateAsync("team-1", "channel-1", Channel, "activity-root", "text", "Resolved");

        Assert.Null(sent.ActivityId);
        _adapter.Verify(a => a.ContinueConversationAsync(It.IsAny<ClaimsIdentity>(), It.IsAny<ConversationReference>(),
            It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
