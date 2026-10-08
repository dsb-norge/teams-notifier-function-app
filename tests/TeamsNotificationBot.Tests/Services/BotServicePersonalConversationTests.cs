using System.Linq.Expressions;
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
/// Finding a person for a direct message: a stored personal conversation, else the rosters of the
/// teams the bot is installed in, creating the conversation with the first match. The adapter and
/// the connector are mocked; that Teams creates the chat is for the manual pass on dev.
/// </summary>
// Joins the "Azurite" collection only to serialize with the classes that set
// TEAMS_INTEGRATION_DISABLED, which BotService's constructor captures.
[Collection("Azurite")]
public class BotServicePersonalConversationTests : IDisposable
{
    private const string JaneUpn = "jane.doe@example.com";
    private const string JaneObjectId = "0b5f8a8e-1c1e-4f43-9a37-2f6b1b0c9d11";
    private const string TenantId = "11111111-2222-3333-4444-555555555555";

    private readonly Mock<TableClient> _references = new();
    private readonly Mock<TableClient> _teamLookup = new();
    private readonly Mock<CloudAdapter> _adapter =
        new(Mock.Of<IChannelServiceClientFactory>(), Mock.Of<IActivityTaskQueue>(), null!, null!, null!, null!, null!);
    private readonly Mock<ITurnContext> _turnContext = new();
    private readonly Mock<IConversations> _conversations = new();
    private readonly TurnContextStateCollection _services = new();
    private readonly List<TeamLookupEntity> _teams = [];
    private readonly List<ConversationReferenceEntity> _stored = [];
    private ConversationParameters? _created;

    public BotServicePersonalConversationTests()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);

        var connector = new Mock<IConnectorClient>();
        connector.Setup(c => c.Conversations).Returns(_conversations.Object);
        _services.Set(connector.Object);
        _turnContext.Setup(t => t.Services).Returns(_services);

        _adapter
            .Setup(a => a.ContinueConversationAsync(
                It.IsAny<ClaimsIdentity>(), It.IsAny<ConversationReference>(),
                It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()))
            .Returns((ClaimsIdentity _, ConversationReference _, AgentCallbackHandler callback, CancellationToken ct) =>
                callback(_turnContext.Object, ct));
        _adapter
            .Setup(a => a.CreateConversationAsync(
                It.IsAny<ClaimsIdentity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ConversationParameters>(), It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()))
            .Callback<ClaimsIdentity, string, string, string, ConversationParameters, AgentCallbackHandler, CancellationToken>(
                (_, _, _, _, parameters, _, _) => _created = parameters)
            .ReturnsAsync(new ConversationReference
            {
                ServiceUrl = "https://smba.trafficmanager.net/emea/",
                ChannelId = "msteams",
                Conversation = new ConversationAccount { Id = "a:new-personal-chat", TenantId = TenantId }
            });

        _teamLookup
            .Setup(t => t.QueryAsync(It.IsAny<Expression<Func<TeamLookupEntity, bool>>>(), null, null, default))
            .Returns(() => Pageable(_teams));
        _references
            .Setup(t => t.QueryAsync(It.IsAny<Expression<Func<ConversationReferenceEntity, bool>>>(), null, null, default))
            .Returns(() => Pageable<ConversationReferenceEntity>([]));
        // Anything not stored below is absent.
        _references
            .Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(It.IsAny<string>(), It.IsAny<string>(), null, default))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        _references
            .Setup(t => t.UpsertEntityAsync(It.IsAny<ConversationReferenceEntity>(), It.IsAny<TableUpdateMode>(), default))
            .Callback<ConversationReferenceEntity, TableUpdateMode, CancellationToken>((e, _, _) => _stored.Add(e))
            .ReturnsAsync(Mock.Of<Response>());
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", null);
        _services.Dispose();
    }

    private BotService NewService() =>
        new(_adapter.Object, _references.Object, NullLogger<BotService>.Instance, null!, null!, _teamLookup.Object);

    private static AsyncPageable<T> Pageable<T>(IReadOnlyList<T> items) where T : notnull =>
        AsyncPageable<T>.FromPages([Page<T>.FromValues(items, null, Mock.Of<Response>())]);

    private void Store(string pk, string rk, string conversationId)
    {
        var entity = new ConversationReferenceEntity
        {
            PartitionKey = pk,
            RowKey = rk,
            ConversationReference = JsonSerializer.Serialize(new ConversationReference
            {
                ServiceUrl = "https://smba.trafficmanager.net/emea/",
                ChannelId = "msteams",
                Agent = new ChannelAccount { Id = "28:bot-app-id" },
                Conversation = new ConversationAccount { Id = conversationId, TenantId = TenantId }
            })
        };
        _references
            .Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(pk, rk, null, default))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));
    }

    /// <summary>An installed team: its teamlookup row, its General channel's reference, its roster.</summary>
    private void InstalledTeam(string teamGuid, string threadId, params ChannelAccount[] members)
    {
        _teams.Add(new TeamLookupEntity { RowKey = threadId, TeamGuid = teamGuid });
        Store(teamGuid, threadId, threadId);
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync(threadId, It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedMembersResult { Members = [.. members] });
    }

    private static ChannelAccount Member(string id, string name, string objectId, string upn)
    {
        var member = new ChannelAccount { Id = id, Name = name, AadObjectId = objectId };
        member.Properties["userPrincipalName"] = JsonSerializer.SerializeToElement(upn);
        return member;
    }

    private static readonly ChannelAccount Jane = Member("29:jane", "Jane Doe", JaneObjectId, JaneUpn);
    private static readonly ChannelAccount Alex = Member("29:alex", "Alex", "99999999-9999-9999-9999-999999999999", "alex@example.com");

    private void VerifyNoRosterRead() =>
        _conversations.Verify(c => c.GetConversationPagedMembersAsync(
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);

    private void VerifyNoConversationCreated() =>
        _adapter.Verify(a => a.CreateConversationAsync(
            It.IsAny<ClaimsIdentity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<ConversationParameters>(), It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()), Times.Never);

    [Fact]
    public async Task ObjectId_WithAStoredPersonalConversation_UsesIt_WithoutSearching()
    {
        Store("user", JaneObjectId, "a:existing-chat");
        InstalledTeam("team-a", "19:a@thread.tacv2", Jane);

        var key = await NewService().FindPersonalConversationAsync(JaneObjectId.ToUpperInvariant());

        Assert.Equal(("user", JaneObjectId), key);
        VerifyNoRosterRead();
        VerifyNoConversationCreated();
    }

    [Fact]
    public async Task Upn_FoundInATeamRoster_CreatesAndStoresTheConversation()
    {
        InstalledTeam("team-a", "19:a@thread.tacv2", Alex);
        InstalledTeam("team-b", "19:b@thread.tacv2", Alex, Jane);

        var key = await NewService().FindPersonalConversationAsync(JaneUpn);

        Assert.Equal(("user", JaneObjectId), key);
        Assert.Equal("29:jane", Assert.Single(_created!.Members).Id);
        Assert.False(_created.IsGroup);
        Assert.Equal(TenantId, _created.TenantId);
        Assert.Equal("28:bot-app-id", _created.Agent.Id);
        _adapter.Verify(a => a.CreateConversationAsync(It.IsAny<ClaimsIdentity>(), "msteams",
            "https://smba.trafficmanager.net/emea/", "https://api.botframework.com",
            It.IsAny<ConversationParameters>(), It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()), Times.Once);

        var stored = Assert.Single(_stored);
        Assert.Equal(("user", JaneObjectId), (stored.PartitionKey, stored.RowKey));
        Assert.Equal("personal", stored.ConversationType);
        Assert.Equal("Jane Doe", stored.UserName);
        Assert.Contains("a:new-personal-chat", stored.ConversationReference);
    }

    [Fact]
    public async Task Found_WithAConversationAlreadyStored_CreatesNone()
    {
        Store("user", JaneObjectId, "a:existing-chat");
        InstalledTeam("team-a", "19:a@thread.tacv2", Jane);

        var key = await NewService().FindPersonalConversationAsync(JaneUpn);

        Assert.Equal(("user", JaneObjectId), key);
        VerifyNoConversationCreated();
    }

    [Fact]
    public async Task TeamGiven_SearchesOnlyThatTeam()
    {
        InstalledTeam("team-a", "19:a@thread.tacv2", Jane);
        InstalledTeam("team-b", "19:b@thread.tacv2", Alex);

        var key = await NewService().FindPersonalConversationAsync(JaneUpn, "TEAM-B");

        Assert.Null(key);
        _conversations.Verify(c => c.GetConversationPagedMembersAsync(
            "19:a@thread.tacv2", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InNoRoster_ReturnsNull_AndCreatesNothing()
    {
        InstalledTeam("team-a", "19:a@thread.tacv2", Alex);

        Assert.Null(await NewService().FindPersonalConversationAsync(JaneUpn));
        VerifyNoConversationCreated();
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task ATeamWithoutAStoredConversation_IsSkipped()
    {
        _teams.Add(new TeamLookupEntity { RowKey = "19:gone@thread.tacv2", TeamGuid = "team-gone" });
        InstalledTeam("team-b", "19:b@thread.tacv2", Jane);

        Assert.Equal(("user", JaneObjectId), await NewService().FindPersonalConversationAsync(JaneUpn));
    }

    [Fact]
    public async Task ATeamWhoseGeneralChannelIsntStored_IsSearchedThroughAnotherChannel()
    {
        _teams.Add(new TeamLookupEntity { RowKey = "19:a@thread.tacv2", TeamGuid = "team-a" });
        var other = new ConversationReferenceEntity
        {
            PartitionKey = "team-a",
            RowKey = "19:other@thread.tacv2",
            ConversationReference = JsonSerializer.Serialize(new ConversationReference
            {
                ServiceUrl = "https://smba.trafficmanager.net/emea/",
                ChannelId = "msteams",
                Agent = new ChannelAccount { Id = "28:bot-app-id" },
                Conversation = new ConversationAccount { Id = "19:other@thread.tacv2", TenantId = TenantId }
            })
        };
        _references
            .Setup(t => t.QueryAsync(It.IsAny<Expression<Func<ConversationReferenceEntity, bool>>>(), null, null, default))
            .Returns(() => Pageable<ConversationReferenceEntity>([other]));
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync("19:a@thread.tacv2", It.IsAny<int?>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedMembersResult { Members = [Jane] });

        Assert.Equal(("user", JaneObjectId), await NewService().FindPersonalConversationAsync(JaneUpn));
    }

    [Fact]
    public async Task ARosterTeamsRefuses_IsSkipped_AndTheSearchGoesOn()
    {
        InstalledTeam("team-a", "19:a@thread.tacv2");
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync("19:a@thread.tacv2", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ErrorResponseException("forbidden") { StatusCode = 403 });
        InstalledTeam("team-b", "19:b@thread.tacv2", Jane);

        Assert.Equal(("user", JaneObjectId), await NewService().FindPersonalConversationAsync(JaneUpn));
    }

    [Fact]
    public async Task ARosterReadFailingOtherwise_Propagates_SoTheQueueRetries()
    {
        InstalledTeam("team-a", "19:a@thread.tacv2");
        _conversations
            .Setup(c => c.GetConversationPagedMembersAsync("19:a@thread.tacv2", It.IsAny<int?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() => NewService().FindPersonalConversationAsync(JaneUpn));
    }

    [Fact]
    public async Task TeamsCreatingNoConversation_Throws_SoTheQueueRetries()
    {
        InstalledTeam("team-a", "19:a@thread.tacv2", Jane);
        _adapter
            .Setup(a => a.CreateConversationAsync(
                It.IsAny<ClaimsIdentity>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<ConversationParameters>(), It.IsAny<AgentCallbackHandler>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConversationReference)null!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().FindPersonalConversationAsync(JaneUpn));
        Assert.Empty(_stored);
    }

    [Fact]
    public async Task TeamsDisabled_ReturnsTheKeyWithoutLooking()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", "true");

        Assert.Equal(("user", JaneObjectId), await NewService().FindPersonalConversationAsync(JaneObjectId.ToUpperInvariant()));
        Assert.Equal(("user", JaneUpn), await NewService().FindPersonalConversationAsync(JaneUpn));
        _teamLookup.VerifyNoOtherCalls();
    }
}
