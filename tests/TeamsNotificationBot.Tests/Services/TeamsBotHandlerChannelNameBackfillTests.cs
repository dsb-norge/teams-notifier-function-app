using System.Net;
using System.Text;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

/// <summary>
/// Tests the opportunistic ChannelName backfill on the list-aliases read path.
///
/// These are the only handler tests that let TeamsInfo.GetTeamChannelsAsync actually succeed.
/// TeamsInfo builds a concrete RestTeamsConnectorClient internally, so the only seam is the
/// IRestTransport that the IConnectorClient in turn state must also implement — stubbing its
/// HttpClient lets the real SDK code path run against canned JSON.
/// </summary>
public class TeamsBotHandlerChannelNameBackfillTests
{
    private const string TeamGuid = "0cfe6b08-34e2-4918-abd3-83c4f8bff08d";
    private const string TeamThreadId = "19:VaovLGAH@thread.tacv2";
    private const string ChannelId = "19:513be54d@thread.tacv2";

    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<TableClient> _teamLookupTable = new();
    private readonly Mock<QueueClient> _botOpsQueue = new();
    private readonly TeamsBotHandler _handler;

    public TeamsBotHandlerChannelNameBackfillTests()
    {
        _handler = new TeamsBotHandler(
            _botService.Object,
            _aliasService.Object,
            _teamLookupTable.Object,
            _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance);
    }

    private static string RefJson(string conversationId) =>
        "{\"Conversation\":{\"Id\":\"" + conversationId + "\"}}";

    private void SetupChannelAlias() =>
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new()
            {
                RowKey = "dev-alerts", TargetType = "channel",
                TeamId = TeamGuid, ChannelId = ChannelId,
                CreatedAt = DateTimeOffset.UtcNow
            }
        });

    private void SetupEntity(string? channelName) =>
        _botService.Setup(s => s.GetConversationReferenceEntityAsync(TeamGuid, ChannelId))
            .ReturnsAsync(new ConversationReferenceEntity
            {
                PartitionKey = TeamGuid,
                RowKey = ChannelId,
                ChannelName = channelName,
                TeamName = "DevOps - ADM - IKT",
                ConversationReference = RefJson(ChannelId)
            });

    /// <summary>
    /// Builds a turn context for a list-aliases message. When <paramref name="inTeamChannel"/> is
    /// true the activity carries team channel data and turn state carries a connector client whose
    /// HTTP transport returns <paramref name="channelListJson"/>.
    /// </summary>
    private static Mock<ITurnContext<IMessageActivity>> CreateListAliasesContext(
        bool inTeamChannel, string? channelListJson = null)
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = "list-aliases",
            Recipient = new ChannelAccount { Id = "bot-id", Name = "Bot" },
            From = new ChannelAccount { Id = "user-id", Name = "User", AadObjectId = "user-aad-oid" },
            Conversation = new ConversationAccount
            {
                Id = inTeamChannel ? ChannelId : "conv-id",
                ConversationType = inTeamChannel ? "channel" : "personal"
            },
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/emea/"
        };

        if (inTeamChannel)
        {
            activity.ChannelData = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                team = new { id = TeamThreadId, aadGroupId = TeamGuid, name = "DevOps - ADM - IKT" },
                channel = new { id = ChannelId }
            });
        }

        var turnContext = new Mock<ITurnContext<IMessageActivity>>();
        turnContext.Setup(t => t.Activity).Returns(activity);
        turnContext.As<ITurnContext>().Setup(t => t.Activity).Returns(activity);
        turnContext.Setup(t => t.SendActivityAsync(
                It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceResponse());

        var services = new TurnContextStateCollection();
        if (channelListJson != null)
            services.Set(CreateConnectorClient(channelListJson));
        turnContext.Setup(t => t.Services).Returns(services);

        return turnContext;
    }

    /// <summary>
    /// An IConnectorClient that also implements IRestTransport (TeamsInfo casts to it) and hands
    /// out an HttpClient that answers every request with the supplied channel-list JSON.
    /// </summary>
    private static IConnectorClient CreateConnectorClient(string channelListJson)
    {
        var handler = new Mock<HttpMessageHandler>();
        handler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(channelListJson, Encoding.UTF8, "application/json")
            });

        var connector = new Mock<IConnectorClient>();
        connector.Setup(c => c.BaseUri).Returns(new Uri("https://smba.trafficmanager.net/emea/"));
        var transport = connector.As<IRestTransport>();
        transport.Setup(t => t.Endpoint).Returns(new Uri("https://smba.trafficmanager.net/emea/"));
        transport.Setup(t => t.GetHttpClientAsync())
            .ReturnsAsync(new HttpClient(handler.Object));

        return connector.Object;
    }

    [Fact]
    public async Task InTeamChannel_NamelessRow_BackfillsResolvedName()
    {
        SetupChannelAlias();
        SetupEntity(null);
        var turnContext = CreateListAliasesContext(
            inTeamChannel: true,
            channelListJson: $$"""{"conversations":[{"id":"{{ChannelId}}","name":"utvikling - testkanal"}]}""");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.TryUpdateChannelNameAsync(
            TeamGuid, ChannelId, "utvikling - testkanal"), Times.Once);
    }

    [Fact]
    public async Task InTeamChannel_NamelessGeneralRow_BackfillsCanonicalGeneral()
    {
        // The API returns General with a null name; its channel ID equals the team thread ID.
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new()
            {
                RowKey = "general-alias", TargetType = "channel",
                TeamId = TeamGuid, ChannelId = TeamThreadId,
                CreatedAt = DateTimeOffset.UtcNow
            }
        });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync(TeamGuid, TeamThreadId))
            .ReturnsAsync(new ConversationReferenceEntity
            {
                PartitionKey = TeamGuid, RowKey = TeamThreadId,
                ChannelName = null, TeamName = "DevOps - ADM - IKT",
                ConversationReference = RefJson(TeamThreadId)
            });
        var turnContext = CreateListAliasesContext(
            inTeamChannel: true,
            channelListJson: $$"""{"conversations":[{"id":"{{TeamThreadId}}","name":null}]}""");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.TryUpdateChannelNameAsync(
            TeamGuid, TeamThreadId, "General"), Times.Once);
    }

    [Fact]
    public async Task InTeamChannel_RowAlreadyNamed_DoesNotBackfill()
    {
        SetupChannelAlias();
        SetupEntity("already-named");
        var turnContext = CreateListAliasesContext(
            inTeamChannel: true,
            channelListJson: $$"""{"conversations":[{"id":"{{ChannelId}}","name":"utvikling - testkanal"}]}""");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.TryUpdateChannelNameAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task InPersonalChat_NeverBackfills()
    {
        // Invariant 2: no team context means no cache and no Teams API call, so nothing to persist.
        SetupChannelAlias();
        SetupEntity(null);
        var turnContext = CreateListAliasesContext(inTeamChannel: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.TryUpdateChannelNameAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task BackfillDoesNotStick_CardStillRenders()
    {
        // Invariant 4: a failed bookkeeping write must not affect the user-visible command.
        // The writer signals failure by returning false — it never throws, a contract pinned by
        // BotServiceChannelNameTests.UnexpectedException_SwallowsAndReturnsFalse. That is why
        // there is deliberately no try/catch at this call site.
        SetupChannelAlias();
        SetupEntity(null);
        _botService.Setup(s => s.TryUpdateChannelNameAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var turnContext = CreateListAliasesContext(
            inTeamChannel: true,
            channelListJson: $$"""{"conversations":[{"id":"{{ChannelId}}","name":"utvikling - testkanal"}]}""");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ContainsCardText(a, "utvikling - testkanal")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool ContainsCardText(IActivity activity, string expected)
    {
        var attachment = activity.Attachments?.FirstOrDefault();
        if (attachment?.Content == null) return false;
        return System.Text.Json.JsonSerializer.Serialize(attachment.Content).Contains(expected);
    }
}
