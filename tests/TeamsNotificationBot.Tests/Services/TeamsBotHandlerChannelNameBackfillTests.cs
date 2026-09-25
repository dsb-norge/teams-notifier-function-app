using System.Linq.Expressions;
using System.Net;
using System.Text;
using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

/// <summary>
/// Tests the opportunistic ChannelName and TeamName backfills on the list-aliases read path.
///
/// These are the only handler tests that let the Teams channel-list API call actually succeed.
/// The TeamsAgentExtension before-turn hook builds the Teams ApiClient from the options'
/// IHttpClientFactory, so the seam is a stub HttpClient (handed to TestAgentOptions) whose
/// message handler answers the real SDK request with canned channel-list JSON.
/// </summary>
public class TeamsBotHandlerChannelNameBackfillTests : IDisposable
{
    private const string TeamGuid = "0cfe6b08-34e2-4918-abd3-83c4f8bff08d";
    private const string TeamThreadId = "19:VaovLGAH@thread.tacv2";
    private const string ChannelId = "19:513be54d@thread.tacv2";

    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<TableClient> _teamLookupTable = new();
    private readonly Mock<QueueClient> _botOpsQueue = new();
    private readonly TeamsBotHandler _handler;

    // The canned response the stub HttpClient serves; set per-test via CreateListAliasesContext.
    private string? _channelListJson;
    private readonly CapturingStubHandler _stubHandler;

    // The stub HttpClient must outlive the constructor that builds it (the handler issues
    // requests through it mid-test), so it is disposed here rather than with `using`.
    // (Turn-state collections are owned by TurnContextStub and are not tracked.)
    private readonly List<IDisposable> _disposables = [];

    public TeamsBotHandlerChannelNameBackfillTests()
    {
        _stubHandler = new CapturingStubHandler(() => _channelListJson);
        var stubHttpClient = new HttpClient(_stubHandler);
        _disposables.Add(stubHttpClient);

        _handler = new TeamsBotHandler(
            TestAgentOptions.Create(teamsApiHttpClient: stubHttpClient),
            _botService.Object,
            _aliasService.Object,
            _teamLookupTable.Object,
            _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
            disposable.Dispose();
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

    private void SetupEntity(string? channelName, string? teamName = "DevOps - ADM - IKT") =>
        _botService.Setup(s => s.GetConversationReferenceEntityAsync(TeamGuid, ChannelId))
            .ReturnsAsync(new ConversationReferenceEntity
            {
                PartitionKey = TeamGuid,
                RowKey = ChannelId,
                ChannelName = channelName,
                TeamName = teamName,
                ConversationReference = RefJson(ChannelId)
            });

    /// <summary>
    /// Builds a turn context for a list-aliases message. When <paramref name="inTeamChannel"/> is
    /// true the activity carries team channel data, and the class-level stub HttpClient (behind
    /// the options' IHttpClientFactory) answers the channel-list request with
    /// <paramref name="channelListJson"/>.
    /// </summary>
    private Mock<ITurnContext<IMessageActivity>> CreateListAliasesContext(
        bool inTeamChannel, string? channelListJson = null)
    {
        _channelListJson = channelListJson;
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

        return TurnContextStub.Wrap<IMessageActivity>(activity);
    }

    /// <summary>
    /// Answers the channel-list request with the canned JSON and records the request the SDK
    /// actually issued, so tests can pin the hand-built URL and the Authorization header in
    /// Helpers/TeamsChannelList (both moved from SDK code into first-party code in the MSTeams
    /// migration — a URL typo would otherwise stay green against a production 404).
    /// </summary>
    private sealed class CapturingStubHandler(Func<string?> channelListJson) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            var json = channelListJson();
            return Task.FromResult(json == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
        }
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

        // Pin the request TeamsChannelList hand-builds (URL shape + escaping) and that the
        // token from the options' IConnections actually reaches the wire.
        Assert.Equal(
            $"https://smba.trafficmanager.net/emea/v3/teams/{Uri.EscapeDataString(TeamThreadId)}/conversations",
            _stubHandler.LastRequestUri!.AbsoluteUri);
        Assert.Equal("Bearer test-token", _stubHandler.LastAuthorization);
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

    // --- TeamName backfill (rows written by channel events that lacked the team name) ---

    /// <summary>
    /// Serves teamlookup query results by applying the handler's own filter expression to
    /// <paramref name="rows"/>, so a wrong filter (say, on RowKey instead of TeamGuid) fails here.
    /// </summary>
    private void SetupTeamLookupRows(params TeamLookupEntity[] rows) =>
        _teamLookupTable.Setup(t => t.QueryAsync<TeamLookupEntity>(
                It.IsAny<Expression<Func<TeamLookupEntity, bool>>>(), It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<TeamLookupEntity, bool>> filter, int? _, IEnumerable<string> _, CancellationToken _) =>
                AsyncPageable<TeamLookupEntity>.FromPages(
                [
                    Page<TeamLookupEntity>.FromValues(rows.Where(filter.Compile()).ToList(), null, Mock.Of<Response>())
                ]));

    private static TeamLookupEntity LookupRow(string teamGuid, string threadId, string? teamName) =>
        new() { RowKey = threadId, TeamGuid = teamGuid, TeamName = teamName };

    private void VerifyLookupQueries(Times times) =>
        _teamLookupTable.Verify(t => t.QueryAsync<TeamLookupEntity>(
            It.IsAny<Expression<Func<TeamLookupEntity, bool>>>(), It.IsAny<int?>(),
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task InPersonalChat_TeamlessRow_BackfillsTeamNameFromLookupAndRendersIt()
    {
        // The reported case: list-aliases from a 1:1 chat, on a channel row with no TeamName.
        // No team context and no Teams API call are needed; teamlookup has the name.
        SetupChannelAlias();
        SetupEntity("utvikling", teamName: null);
        SetupTeamLookupRows(
            LookupRow("other-team-guid", "19:other@thread.tacv2", "Other Team"),
            LookupRow(TeamGuid, TeamThreadId, "Platform Team"));
        var turnContext = CreateListAliasesContext(inTeamChannel: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.TryUpdateTeamNameAsync(TeamGuid, ChannelId, "Platform Team"), Times.Once);
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ContainsCardText(a, "#utvikling in Platform Team")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RowHasTeamName_NeverQueriesLookup()
    {
        SetupChannelAlias();
        SetupEntity("utvikling");
        var turnContext = CreateListAliasesContext(inTeamChannel: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        VerifyLookupQueries(Times.Never());
        _botService.Verify(s => s.TryUpdateTeamNameAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task LookupHasNoNameForTeam_RendersGuidAndDoesNotBackfill()
    {
        SetupChannelAlias();
        SetupEntity("utvikling", teamName: null);
        SetupTeamLookupRows(
            LookupRow("other-team-guid", "19:other@thread.tacv2", "Other Team"),
            LookupRow(TeamGuid, TeamThreadId, null));
        var turnContext = CreateListAliasesContext(inTeamChannel: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.TryUpdateTeamNameAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ContainsCardText(a, $"#utvikling in {TeamGuid}")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LookupQueryFails_CardStillRenders()
    {
        // A failed name lookup costs the display name only, never the command.
        SetupChannelAlias();
        SetupEntity("utvikling", teamName: null);
        _teamLookupTable.Setup(t => t.QueryAsync<TeamLookupEntity>(
                It.IsAny<Expression<Func<TeamLookupEntity, bool>>>(), It.IsAny<int?>(),
                It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException(500, "Server Error"));
        var turnContext = CreateListAliasesContext(inTeamChannel: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ContainsCardText(a, $"#utvikling in {TeamGuid}")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TeamlessRowsInOneTeam_QueryLookupOncePerRun()
    {
        const string secondChannelId = "19:second@thread.tacv2";
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "alias-a", TargetType = "channel", TeamId = TeamGuid, ChannelId = ChannelId, CreatedAt = DateTimeOffset.UtcNow },
            new() { RowKey = "alias-b", TargetType = "channel", TeamId = TeamGuid, ChannelId = secondChannelId, CreatedAt = DateTimeOffset.UtcNow }
        });
        SetupEntity("utvikling", teamName: null);
        _botService.Setup(s => s.GetConversationReferenceEntityAsync(TeamGuid, secondChannelId))
            .ReturnsAsync(new ConversationReferenceEntity
            {
                PartitionKey = TeamGuid, RowKey = secondChannelId,
                ChannelName = "second", TeamName = null,
                ConversationReference = RefJson(secondChannelId)
            });
        SetupTeamLookupRows(LookupRow(TeamGuid, TeamThreadId, "Platform Team"));
        var turnContext = CreateListAliasesContext(inTeamChannel: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        VerifyLookupQueries(Times.Once());
        _botService.Verify(s => s.TryUpdateTeamNameAsync(TeamGuid, ChannelId, "Platform Team"), Times.Once);
        _botService.Verify(s => s.TryUpdateTeamNameAsync(TeamGuid, secondChannelId, "Platform Team"), Times.Once);
    }

    private static bool ContainsCardText(IActivity activity, string expected)
    {
        var attachment = activity.Attachments?.FirstOrDefault();
        if (attachment?.Content == null) return false;
        return System.Text.Json.JsonSerializer.Serialize(attachment.Content).Contains(expected);
    }
}
