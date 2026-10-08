using System.Security.Claims;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Connector.Types;
using Microsoft.Agents.Core.Errors;
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
    private readonly Mock<TableClient> _teamLookup = new();
    private readonly Mock<CloudAdapter> _adapter =
        new(Mock.Of<IChannelServiceClientFactory>(), Mock.Of<IActivityTaskQueue>(), null!, null!, null!, null!, null!);
    private readonly Mock<ITurnContext> _turnContext = new();
    private readonly Mock<IConversations> _conversations = new();
    private readonly TurnContextStateCollection _services = new();

    private ConversationReference? _usedReference;
    private readonly List<Exception> _turnErrors = [];
    private readonly List<IActivity> _sent = [];
    private readonly List<IActivity> _updated = [];

    public BotServiceSendTests()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);

        TeamsNotificationBot.Tests.Helpers.ProactiveTurns.RunLikeTheSdk(
            _adapter, _turnContext.Object, _turnErrors, reference => _usedReference = reference);
        _turnContext
            .Setup(t => t.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .Callback<IActivity, CancellationToken>((a, _) => _sent.Add(a))
            .ReturnsAsync(new ResourceResponse("activity-123"));
        _turnContext
            .Setup(t => t.UpdateActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .Callback<IActivity, CancellationToken>((a, _) => _updated.Add(a))
            .ReturnsAsync(new ResourceResponse("activity-root"));

        // The connector client the adapter puts in a proactive turn, which roster reads go through.
        var connector = new Mock<IConnectorClient>();
        connector.Setup(c => c.Conversations).Returns(_conversations.Object);
        _services.Set(connector.Object);
        _turnContext.Setup(t => t.Services).Returns(_services);

        StoreReference("team-1", "channel-1", Channel);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);
        _services.Dispose();
    }

    private readonly TeamsNotificationBot.Tests.Helpers.ListLogger<BotService> _log = new();

    private BotService NewService() =>
        new(_adapter.Object, _tableClient.Object, _log, null!, null!, _teamLookup.Object);

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

    // --- Failures inside the turn ---
    // The adapter's default turn-error handler would post these into the conversation and report
    // success; they must reach the caller instead, so the queue retries and the record stays honest.

    [Fact]
    public async Task Send_RefusedByTeams_Throws_AndNothingIsPostedAboutIt()
    {
        _turnContext
            .Setup(t => t.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(403));

        var ex = await Assert.ThrowsAsync<ErrorResponseException>(() =>
            NewService().SendAsync("team-1", "channel-1", "text", "Hello"));

        Assert.Equal(403, ex.StatusCode);
        Assert.Empty(_turnErrors);
    }

    [Fact]
    public async Task Update_NotFoundByTeams_Throws_SoTheQueueRetriesIt()
    {
        // Seen on dev: Teams answered 404 to an update sent at the same moment as a reply in the
        // updated message's thread. A retry a little later finds the message.
        _turnContext
            .Setup(t => t.UpdateActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(404));

        var ex = await Assert.ThrowsAsync<ErrorResponseException>(() =>
            NewService().UpdateAsync("team-1", "channel-1", Channel, "activity-root", "text", "Resolved"));

        Assert.Equal(404, ex.StatusCode);
        Assert.Empty(_turnErrors);
    }

    [Fact]
    public async Task Send_Throttled_IsRetried_InANewTurn()
    {
        _turnContext
            .SetupSequence(t => t.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(429))
            .ReturnsAsync(new ResourceResponse("activity-123"));

        var sent = await NewService().SendAsync("team-1", "channel-1", "text", "Hello");

        Assert.Equal("activity-123", sent.ActivityId);
        _adapter.Verify(a => a.ContinueConversationAsync(It.IsAny<ClaimsIdentity>(), It.IsAny<ConversationReference>(),
            It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Empty(_turnErrors);
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

    // --- Mentions ---

    private const string JaneUpn = "jane.doe@example.com";
    private const string JaneObjectId = "0b5f8a8e-1c1e-4f43-9a37-2f6b1b0c9d11";

    private static readonly MessageMention Jane = new() { Key = "jane", Id = JaneUpn, Name = "Jane" };
    private static readonly MessageMention Sam = new() { Key = "sam", Id = "sam@example.com", Name = "Sam" };
    private static readonly MessageMention OnCall = new() { Key = "oncall", Tag = "dGFnLWlk", Name = "On call" };

    private static ChannelAccount Member(string id, string name, string objectId, string upn)
    {
        var member = new ChannelAccount { Id = id, Name = name, AadObjectId = objectId };
        member.Properties["userPrincipalName"] = JsonSerializer.SerializeToElement(upn);
        return member;
    }

    private static readonly ChannelAccount JaneInRoster = Member("29:jane", "Jane Doe", JaneObjectId, JaneUpn);

    /// <summary>The team roster, in two pages, with Jane on the second.</summary>
    private void SetUpTeamRoster()
    {
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync(Channel, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedMembersResult
            {
                Members = [Member("29:alex", "Alex", "11111111-1111-1111-1111-111111111111", "alex@example.com")],
                ContinuationToken = "page-2"
            });
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync(Channel, It.IsAny<int?>(), "page-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedMembersResult { Members = [JaneInRoster] });
    }

    private static ErrorResponseException TeamsError(int status) => new($"Teams answered {status}") { StatusCode = status };

    /// <summary>A Teams error with its reason in the body, as the connector parses it.</summary>
    private static ErrorResponseException TeamsError(int status, string code, string reason) =>
        new($"ReplyToActivity operation returned an invalid status code '({status})'")
        {
            StatusCode = status,
            Body = new ErrorResponse { Error = new Error { Code = code, Message = reason } }
        };

    private string Warning(string startsWith) =>
        Assert.Single(_log.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning && e.Message.StartsWith(startsWith)).Message;

    [Fact]
    public async Task Send_WithMentions_InAChannel_ChecksThemAgainstTheTeamRoster()
    {
        SetUpTeamRoster();

        var sent = await NewService().SendAsync("team-1", "channel-1", "text",
            "<at>jane</at> and <at>sam</at>, look.", mentions: [Jane, Sam]);

        var activity = Assert.Single(_sent);
        Assert.Equal("<at>Jane Doe</at> and Sam, look.", activity.Text);
        var mention = Assert.IsType<Mention>(Assert.Single(activity.Entities));
        Assert.Equal("29:jane", mention.Mentioned.Id);
        Assert.Equal(["sam@example.com"], sent.UnresolvedMentions);
    }

    [Fact]
    public async Task Send_WithMentions_InAThread_ReadsTheChannelsRoster_NotTheThreads()
    {
        SetUpTeamRoster();

        await NewService().SendAsync("team-1", "channel-1", "text", "<at>jane</at>",
            threadActivityId: "1712345678901", mentions: [Jane]);

        _conversations.Verify(c => c.GetConversationPagedMembersAsync(
            Channel, It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal("<at>Jane Doe</at>", Assert.Single(_sent).Text);
    }

    [Fact]
    public async Task Send_WithMentions_InAGroupChat_ReadsTheChatsMembers()
    {
        StoreReference("chat", "19:chat@thread.v2", "19:chat@thread.v2");
        _conversations
            .Setup(c => c.GetConversationMembersAsync("19:chat@thread.v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync([JaneInRoster]);

        var sent = await NewService().SendAsync("chat", "19:chat@thread.v2", "text", "<at>jane</at>", mentions: [Jane]);

        Assert.Equal("<at>Jane Doe</at>", Assert.Single(_sent).Text);
        Assert.Empty(sent.UnresolvedMentions);
        _conversations.Verify(c => c.GetConversationPagedMembersAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Send_WithMentions_InAGroupChat_FindsAMemberByObjectId_AsTeamsSendsIt()
    {
        // The group-chat roster call names the object ID "objectId"; seen on dev, where a member
        // mentioned by object ID was written as plain text.
        StoreReference("chat", "19:chat@thread.v2", "19:chat@thread.v2");
        var pal = new ChannelAccount { Id = "29:pal", Name = "Pal" };
        pal.Properties["objectId"] = JsonSerializer.SerializeToElement(JaneObjectId);
        _conversations
            .Setup(c => c.GetConversationMembersAsync("19:chat@thread.v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync([pal]);

        var sent = await NewService().SendAsync("chat", "19:chat@thread.v2", "text", "<at>p</at>",
            mentions: [new MessageMention { Key = "p", Id = JaneObjectId, Name = "Pal" }]);

        Assert.Equal("<at>Pal</at>", Assert.Single(_sent).Text);
        Assert.Empty(sent.UnresolvedMentions);
    }

    [Fact]
    public async Task Send_WithMentions_InAPersonalChat_ReadsNoRoster_AndMentionsNobody()
    {
        StoreReference("user", "user-1", "a:personal");

        var sent = await NewService().SendAsync("user", "user-1", "text", "<at>jane</at>", mentions: [Jane]);

        Assert.Equal("Jane", Assert.Single(_sent).Text);
        Assert.Equal([JaneUpn], sent.UnresolvedMentions);
        _conversations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Send_ATagToAGroupChat_IsPlainText_AndReported()
    {
        // The request check rejects tags outside channels; this is an alias repointed while queued.
        StoreReference("chat", "19:chat@thread.v2", "19:chat@thread.v2");

        var sent = await NewService().SendAsync("chat", "19:chat@thread.v2", "text", "<at>oncall</at>", mentions: [OnCall]);

        var activity = Assert.Single(_sent);
        Assert.Equal("On call", activity.Text);
        Assert.Empty(activity.Entities);
        Assert.Equal(["dGFnLWlk"], sent.UnresolvedMentions);
    }

    [Fact]
    public async Task Send_WithOnlyTags_ReadsNoRoster()
    {
        var sent = await NewService().SendAsync("team-1", "channel-1", "text", "<at>oncall</at>", mentions: [OnCall]);

        Assert.Equal("<at>On call</at>", Assert.Single(_sent).Text);
        Assert.Empty(sent.UnresolvedMentions);
        _conversations.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Send_TagsRejectedByTeams_GoAgainAsPlainText()
    {
        // Microsoft documents a 400 for a tag the team doesn't have.
        SetUpTeamRoster();
        _turnContext
            .Setup(t => t.SendActivityAsync(It.Is<IActivity>(a => a.Entities.Any(e => e is Mention && ((Mention)e).Mentioned.Id == "dGFnLWlk")),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(400));

        var sent = await NewService().SendAsync("team-1", "channel-1", "text",
            "<at>oncall</at>: <at>jane</at>", mentions: [OnCall, Jane]);

        var activity = Assert.Single(_sent);
        Assert.Equal("On call: <at>Jane Doe</at>", activity.Text);
        Assert.Equal("29:jane", Assert.IsType<Mention>(Assert.Single(activity.Entities)).Mentioned.Id);
        Assert.Equal(["dGFnLWlk"], sent.UnresolvedMentions);
    }

    [Fact]
    public async Task Send_TagsRejectedByTeams_LogsTeamsReason_Sanitized()
    {
        // The reason is in the response body, not the exception message; it is logged so a rejected
        // tag can be told from a malformed one, and sanitized because it comes from outside.
        _turnContext
            .Setup(t => t.SendActivityAsync(It.Is<IActivity>(a => a.Entities.Count > 0), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(400, "BadArgument", "Mentioned tag with ID x doesn't exist\nin current team"));

        await NewService().SendAsync("team-1", "channel-1", "text", "<at>oncall</at>", mentions: [OnCall]);

        var warning = Warning("Teams rejected the message's tag mentions");
        Assert.Contains("BadArgument: Mentioned tag with ID x doesn't exist_in current team", warning);
        Assert.DoesNotContain("\n", warning);
    }

    [Fact]
    public async Task Send_RosterRefused_LogsTeamsReason_Sanitized()
    {
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync(Channel, It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(403, "Forbidden", "Bot is not\rpart of the roster"));

        await NewService().SendAsync("team-1", "channel-1", "text", "<at>jane</at>", mentions: [Jane]);

        var warning = Warning("Could not read the roster");
        Assert.Contains("403 Forbidden: Bot is not_part of the roster", warning);
        Assert.DoesNotContain("\r", warning);
    }

    [Fact]
    public async Task Send_A400ForAMessageWithoutTags_Propagates()
    {
        SetUpTeamRoster();
        _turnContext
            .Setup(t => t.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(400));

        await Assert.ThrowsAsync<ErrorResponseException>(() =>
            NewService().SendAsync("team-1", "channel-1", "text", "<at>jane</at>", mentions: [Jane]));
    }

    [Fact]
    public async Task Send_RosterRefused_SendsWithThePeopleAsPlainText()
    {
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync(Channel, It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(403));

        var sent = await NewService().SendAsync("team-1", "channel-1", "text", "<at>jane</at>", mentions: [Jane]);

        Assert.Equal("Jane", Assert.Single(_sent).Text);
        Assert.Equal([JaneUpn], sent.UnresolvedMentions);
    }

    [Fact]
    public async Task Send_RosterReadFailsOtherwise_Propagates_WithoutSending()
    {
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync(Channel, It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            NewService().SendAsync("team-1", "channel-1", "text", "<at>jane</at>", mentions: [Jane]));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Send_RosterIsReadOnce_ForMessagesInQuickSuccession()
    {
        SetUpTeamRoster();
        var service = NewService();

        await service.SendAsync("team-1", "channel-1", "text", "<at>jane</at>", mentions: [Jane]);
        await service.SendAsync("team-1", "channel-1", "text", "<at>jane</at> again", mentions: [Jane]);

        // Two pages, read once.
        _conversations.Verify(c => c.GetConversationPagedMembersAsync(
            Channel, It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public async Task Send_Card_WithMentions_CarriesTheEntitiesInTheCard()
    {
        SetUpTeamRoster();

        await NewService().SendAsync("team-1", "channel-1", "adaptive-card",
            """{"type":"AdaptiveCard","version":"1.5","body":[{"type":"TextBlock","text":"<at>jane</at>"}]}""",
            mentions: [Jane]);

        var activity = Assert.Single(_sent);
        Assert.Empty(activity.Entities ?? []);
        var card = JsonSerializer.Serialize(Assert.Single(activity.Attachments).Content);
        Assert.Contains("<at>Jane Doe</at>", JsonDocument.Parse(card).RootElement.GetProperty("body")[0].GetProperty("text").GetString());
        Assert.Equal("29:jane", JsonDocument.Parse(card).RootElement
            .GetProperty("msteams").GetProperty("entities")[0].GetProperty("mentioned").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Update_WithMentions_ResolvesThemAgain()
    {
        SetUpTeamRoster();

        var unresolved = await NewService().UpdateAsync("team-1", "channel-1", $"{Channel};messageid=1712345678901",
            "activity-root", "text", "Resolved by <at>jane</at>; <at>sam</at> was off.", [Jane, Sam]);

        var activity = Assert.Single(_updated);
        Assert.Equal("activity-root", activity.Id);
        Assert.Equal("Resolved by <at>Jane Doe</at>; Sam was off.", activity.Text);
        Assert.Single(activity.Entities);
        Assert.Equal(["sam@example.com"], unresolved);
    }

    [Fact]
    public async Task Update_TagsRejectedByTeams_GoAgainAsPlainText()
    {
        _turnContext
            .Setup(t => t.UpdateActivityAsync(It.Is<IActivity>(a => a.Entities.Count > 0), It.IsAny<CancellationToken>()))
            .ThrowsAsync(TeamsError(400));

        var unresolved = await NewService().UpdateAsync("team-1", "channel-1", Channel,
            "activity-root", "text", "<at>oncall</at>", [OnCall]);

        Assert.Equal("On call", Assert.Single(_updated).Text);
        Assert.Equal(["dGFnLWlk"], unresolved);
    }
}
