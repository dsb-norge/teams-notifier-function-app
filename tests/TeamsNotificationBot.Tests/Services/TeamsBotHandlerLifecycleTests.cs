using System.Text.Json;
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
/// Dispatch-level tests pinning TeamsBotHandler's lifecycle behaviors ahead of the
/// TeamsActivityHandler → Microsoft.Agents.Extensions.MSTeams migration
/// (docs/contributing.md §9 "Deferred migrations").
///
/// Every test drives the handler through the public IAgent.OnTurnAsync seam — never a
/// protected override — and asserts on observable side effects (storage writes, queue
/// messages, outbound activities). That makes the suite portable across the rewrite:
/// the arrange/act shape stays "activity in → effects out" regardless of whether the
/// implementation is an ActivityHandler subclass or an AgentApplication with routes,
/// and it pins the activity ROUTING itself, which the older tests mostly did not.
/// </summary>
// Message turns read the PoisonAlertAlias env var (setup nudge), which other tests in this
// collection mutate — keep serialized with them.
[Collection("PoisonQueueMonitor")]
public class TeamsBotHandlerLifecycleTests
{
    private const string TeamGuid = "team-guid-1";
    private const string TeamThreadId = "19:thread@thread.tacv2";
    private const string ChannelThreadId = "19:channel@thread.tacv2";

    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<TableClient> _teamLookupTable = new();
    private readonly Mock<QueueClient> _botOpsQueue = new();
    private readonly TeamsBotHandler _handler;

    public TeamsBotHandlerLifecycleTests()
    {
        _handler = new TeamsBotHandler(
            TestAgentOptions.Create(),
            _botService.Object,
            _aliasService.Object,
            _teamLookupTable.Object,
            _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance);
    }

    // --- Installation update: add ---

    [Fact]
    public async Task Install_Add_Channel_StoresReferenceLookupAndEnqueuesEnumeration()
    {
        var turnContext = InstallationTurn("add", "channel");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.Is<ConversationReference>(r => r.Conversation.Id == "conv-id"),
            TeamGuid, "conv-id", "channel", "Test Team", It.IsAny<string?>(), null), Times.Once);
        _teamLookupTable.Verify(t => t.UpsertEntityAsync(
            It.Is<TeamLookupEntity>(e => e.RowKey == TeamThreadId && e.TeamGuid == TeamGuid && e.TeamName == "Test Team"),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Once);
        _botOpsQueue.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => OperationIs(s, "enumerate_channels", TeamGuid) && HasSerializedReference(s))), Times.Once);
        VerifySentTextContaining(turnContext, "Teams Notification Bot");
    }

    [Fact]
    public async Task Install_AddUpgrade_Channel_RoutesSameAsAdd()
    {
        var turnContext = InstallationTurn("add-upgrade", "channel");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(),
            TeamGuid, "conv-id", "channel", "Test Team", It.IsAny<string?>(), null), Times.Once);
    }

    [Fact]
    public async Task Install_Add_Channel_WithoutAadGroupId_StoresNothing()
    {
        var turnContext = InstallationTurn("add", "channel", includeAadGroupId: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _botOpsQueue.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Install_Add_Personal_StoresUserReference()
    {
        var turnContext = InstallationTurn("add", "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), "user", "user-aad-oid", "personal", null, null, "User"),
            Times.Once);
        VerifySentTextContaining(turnContext, "Hi User!");
    }

    [Fact]
    public async Task Install_Add_GroupChat_StoresChatReference()
    {
        var turnContext = InstallationTurn("add", "groupChat");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), "chat", "conv-id", "groupChat", null, null, null),
            Times.Once);
    }

    // --- Installation update: remove ---

    [Fact]
    public async Task Install_Remove_Channel_EnqueuesTeamRefRemovalAndDeletesLookup()
    {
        var turnContext = InstallationTurn("remove", "channel");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botOpsQueue.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => OperationIs(s, "remove_team_refs", TeamGuid))), Times.Once);
        _teamLookupTable.Verify(t => t.DeleteEntityAsync(
            "teamlookup", TeamThreadId, It.IsAny<ETag>(), It.IsAny<CancellationToken>()), Times.Once);
        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Install_Remove_Personal_RemovesUserReference()
    {
        var turnContext = InstallationTurn("remove", "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.RemoveConversationReferenceAsync("user", "user-aad-oid"), Times.Once);
    }

    [Fact]
    public async Task Install_Remove_GroupChat_RemovesChatReference()
    {
        var turnContext = InstallationTurn("remove", "groupChat");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.RemoveConversationReferenceAsync("chat", "conv-id"), Times.Once);
    }

    [Fact]
    public async Task Install_RemoveUpgrade_Personal_RoutesSameAsRemove()
    {
        var turnContext = InstallationTurn("remove-upgrade", "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.RemoveConversationReferenceAsync("user", "user-aad-oid"), Times.Once);
    }

    // --- Channel events ---

    [Theory]
    [InlineData("channelCreated")]
    [InlineData("channelRenamed")]
    [InlineData("channelRestored")]
    public async Task ChannelUpsertEvents_StoreChannelScopedReference(string eventType)
    {
        var turnContext = ConversationUpdateTurn(eventType);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        // The handler must rebuild the reference around the CHANNEL conversation (not the
        // activity's own conversation) so proactive sends target the channel top-level.
        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.Is<ConversationReference>(r =>
                r.Conversation.Id == ChannelThreadId &&
                r.Conversation.ConversationType == "channel" &&
                r.Conversation.IsGroup == true),
            TeamGuid, ChannelThreadId, "channel", "Test Team", "New Channel", null), Times.Once);
    }

    [Fact]
    public async Task ChannelDeleted_RemovesReference()
    {
        var turnContext = ConversationUpdateTurn("channelDeleted");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.RemoveConversationReferenceAsync(TeamGuid, ChannelThreadId), Times.Once);
        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task ChannelCreated_TeamGuidUnresolvable_StoresNothing()
    {
        _teamLookupTable.Setup(t => t.GetEntityAsync<TeamLookupEntity>(
                "teamlookup", TeamThreadId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        var turnContext = ConversationUpdateTurn("channelCreated", includeAadGroupId: false);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    // --- Team events ---

    [Fact]
    public async Task TeamRenamed_EnqueuesBatchRenameAndUpdatesLookup()
    {
        var existing = new TeamLookupEntity { RowKey = TeamThreadId, TeamGuid = TeamGuid, TeamName = "Old Name" };
        _teamLookupTable.Setup(t => t.GetEntityAsync<TeamLookupEntity>(
                "teamlookup", TeamThreadId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(existing, Mock.Of<Response>()));
        var turnContext = ConversationUpdateTurn("teamRenamed", teamName: "Renamed Team");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botOpsQueue.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => OperationIs(s, "rename_team", TeamGuid) && TeamNameIs(s, "Renamed Team"))), Times.Once);
        _teamLookupTable.Verify(t => t.UpdateEntityAsync(
            It.Is<TeamLookupEntity>(e => e.RowKey == TeamThreadId && e.TeamName == "Renamed Team"),
            It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TeamRenamed_LookupMissing_StillEnqueuesRename()
    {
        _teamLookupTable.Setup(t => t.GetEntityAsync<TeamLookupEntity>(
                "teamlookup", TeamThreadId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "not found"));
        var turnContext = ConversationUpdateTurn("teamRenamed", teamName: "Renamed Team");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botOpsQueue.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => OperationIs(s, "rename_team", TeamGuid))), Times.Once);
        _teamLookupTable.Verify(t => t.UpdateEntityAsync(
            It.IsAny<TeamLookupEntity>(), It.IsAny<ETag>(),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task TeamDeleted_EnqueuesRemovalAndDeletesLookup()
    {
        var turnContext = ConversationUpdateTurn("teamDeleted");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botOpsQueue.Verify(q => q.SendMessageAsync(
            It.Is<string>(s => OperationIs(s, "remove_team_refs", TeamGuid))), Times.Once);
        _teamLookupTable.Verify(t => t.DeleteEntityAsync(
            "teamlookup", TeamThreadId, It.IsAny<ETag>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Member events: deliberately suppressed (no HTTP, no messages) ---

    [Theory]
    [InlineData("teamMemberAdded")]
    [InlineData("teamMemberRemoved")]
    public async Task TeamMemberEvents_AreSuppressed(string eventType)
    {
        var turnContext = ConversationUpdateTurn(eventType, withMember: true);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
        _botService.VerifyNoOtherCalls();
        _botOpsQueue.Verify(q => q.SendMessageAsync(It.IsAny<string>()), Times.Never);
    }

    // --- Conversation reference auto-refresh (runs on every message) ---

    [Fact]
    public async Task Message_InChannelThread_RefreshesReferenceWithCleanConversationId()
    {
        _botService.Setup(s => s.UpdateConversationReferenceAsync(
                It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);
        var (turnContext, activity) = MessageTurn("checkin", "channel",
            conversationId: $"{ChannelThreadId};messageid=12345", includeChannelData: true);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        // The stored reference must target the channel top-level (";messageid=..." stripped)
        // so proactive notifications post new threads instead of replying into an old one.
        _botService.Verify(s => s.UpdateConversationReferenceAsync(
            It.Is<ConversationReference>(r => r.Conversation.Id == ChannelThreadId),
            TeamGuid, ChannelThreadId), Times.Once);

        // The refresh must clone the conversation, not mutate the live activity — downstream
        // handlers (delete-post) still need the original ";messageid=..." thread id.
        Assert.Equal($"{ChannelThreadId};messageid=12345", activity.Conversation.Id);
    }

    [Fact]
    public async Task Message_Personal_FirstContact_StoresReference()
    {
        _botService.Setup(s => s.UpdateConversationReferenceAsync(
                It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var (turnContext, _) = MessageTurn("checkin", "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), "user", "user-aad-oid", "personal", null, null, "User"),
            Times.Once);
    }

    [Fact]
    public async Task Message_GroupChat_FirstContact_StoresReference()
    {
        _botService.Setup(s => s.UpdateConversationReferenceAsync(
                It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var (turnContext, _) = MessageTurn("checkin", "groupChat");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), "chat", "conv-id", "groupChat", null, null, null),
            Times.Once);
    }

    [Fact]
    public async Task Message_Channel_FirstContact_DoesNotStore()
    {
        _botService.Setup(s => s.UpdateConversationReferenceAsync(
                It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var (turnContext, _) = MessageTurn("checkin", "channel", includeChannelData: true);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        // Channels only get references via install/channel events — a message must not
        // fabricate one from a possibly-thread-scoped context.
        _botService.Verify(s => s.StoreConversationReferenceAsync(
            It.IsAny<ConversationReference>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    // --- @mention stripping and slash prefix ---

    [Fact]
    public async Task Message_WithBotMention_StripsMentionBeforeDispatch()
    {
        var (turnContext, activity) = MessageTurn("<at>Bot</at> checkin", "channel", includeChannelData: true);
        activity.Entities =
        [
            new Mention
            {
                Type = "mention",
                Text = "<at>Bot</at>",
                Mentioned = new ChannelAccount { Id = "bot-id", Name = "Bot" }
            }
        ];

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        VerifySentTextContaining(turnContext, "Bot is online");
    }

    [Fact]
    public async Task Message_WithSlashPrefix_Dispatches()
    {
        var (turnContext, _) = MessageTurn("/checkin", "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        VerifySentTextContaining(turnContext, "Bot is online");
    }

    // --- Adaptive card invoke, through real dispatch (not a protected-override shim) ---

    [Fact]
    public async Task AdaptiveCardInvoke_ValidAlias_CreatesAliasAndReturns200()
    {
        var turnContext = InvokeTurn(new { action = "createAlias", aliasName = "card-alias", aliasDescription = "From card" });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        var body = TurnContextStub.GetInvokeResponseBody(turnContext);
        _aliasService.Verify(a => a.SetAliasAsync("card-alias",
            It.Is<AliasEntity>(e => e.TargetType == "personal" && e.UserId == "user-aad-oid" && e.Description == "From card")),
            Times.Once);
        Assert.Equal(200, body.StatusCode);
    }

    [Fact]
    public async Task AdaptiveCardInvoke_InvalidAliasName_Returns400AndStoresNothing()
    {
        var turnContext = InvokeTurn(new { action = "createAlias", aliasName = "-Bad Name-" });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(a => a.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
        var body = TurnContextStub.GetInvokeResponseBody(turnContext);
        Assert.Equal(400, body.StatusCode);
    }

    [Fact]
    public async Task AdaptiveCardInvoke_UnknownAction_Returns400AndStoresNothing()
    {
        // Pre-migration this fell through to the TeamsActivityHandler base (a bodyless 501);
        // the explicit 400 is a deliberate behavior choice of the migration — pinned here.
        var turnContext = InvokeTurn(new { action = "somethingElse" });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(a => a.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
        Assert.Equal(400, TurnContextStub.GetInvokeResponseBody(turnContext).StatusCode);
    }

    [Fact]
    public async Task ForeignInvoke_Returns501()
    {
        // Invokes this bot doesn't route (signin/*, task/fetch, composeExtension/*, …) must get
        // an explicit 501 like the pre-migration base handler gave — without the catch-all route,
        // the adapter fabricates a 200-empty and the Teams client treats the invoke as succeeded.
        var activity = BaseActivity(ActivityTypes.Invoke, "personal");
        activity.Name = "signin/verifyState";
        var turnContext = WrapContext<IInvokeActivity>(activity);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        var sent = TurnContextStub.SentInvokeResponses(turnContext);
        var invokeResponse = Assert.IsType<InvokeResponse>(((Activity)Assert.Single(sent)).Value);
        Assert.Equal(501, invokeResponse.Status);
    }

    // --- Message-dispatch gaps ---

    [Fact]
    public async Task SetAlias_InGroupChat_StoresChatTarget()
    {
        var (turnContext, _) = MessageTurn("set-alias ops-chat", "groupChat");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(a => a.SetAliasAsync("ops-chat",
            It.Is<AliasEntity>(e => e.TargetType == "groupChat" && e.ChatId == "conv-id" && e.TeamId == null && e.UserId == null)),
            Times.Once);
    }

    [Fact]
    public async Task SetAlias_UnknownConversationType_ReportsCannotDetermineTarget()
    {
        var (turnContext, _) = MessageTurn("set-alias orphan", conversationType: "");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(a => a.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
        VerifySentTextContaining(turnContext, "Could not determine conversation target");
    }

    [Fact]
    public async Task ListAlias_SingularForm_Dispatches()
    {
        _aliasService.Setup(a => a.GetAllAliasesAsync()).ReturnsAsync([]);
        var (turnContext, _) = MessageTurn("list-alias", "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        VerifySentTextContaining(turnContext, "No aliases configured");
    }

    // --- Fixture builders ---

    private static Mock<ITurnContext<IInstallationUpdateActivity>> InstallationTurn(
        string action, string conversationType, bool includeAadGroupId = true)
    {
        var activity = BaseActivity(ActivityTypes.InstallationUpdate, conversationType);
        activity.Action = action;

        if (conversationType == "channel")
        {
            activity.ChannelData = includeAadGroupId
                ? JsonSerializer.SerializeToElement(new
                {
                    team = new { id = TeamThreadId, aadGroupId = TeamGuid, name = "Test Team" },
                    channel = new { id = activity.Conversation.Id }
                })
                : JsonSerializer.SerializeToElement(new
                {
                    team = new { id = TeamThreadId, name = "Test Team" },
                    channel = new { id = activity.Conversation.Id }
                });
        }

        return WrapContext<IInstallationUpdateActivity>(activity);
    }

    private Mock<ITurnContext<IConversationUpdateActivity>> ConversationUpdateTurn(
        string eventType, bool includeAadGroupId = true, string teamName = "Test Team", bool withMember = false)
    {
        var activity = BaseActivity(ActivityTypes.ConversationUpdate, "channel");
        activity.Conversation.Id = ChannelThreadId;
        activity.Conversation.IsGroup = true;
        activity.Conversation.TenantId = "tenant-id";
        activity.ChannelData = includeAadGroupId
            ? JsonSerializer.SerializeToElement(new
            {
                eventType,
                team = new { id = TeamThreadId, aadGroupId = TeamGuid, name = teamName },
                channel = new { id = ChannelThreadId, name = "New Channel" }
            })
            : JsonSerializer.SerializeToElement(new
            {
                eventType,
                team = new { id = TeamThreadId, name = teamName },
                channel = new { id = ChannelThreadId, name = "New Channel" }
            });

        if (withMember)
        {
            var member = new ChannelAccount { Id = "29:someuser", Name = "Some User" };
            if (eventType == "teamMemberAdded") activity.MembersAdded = [member];
            else activity.MembersRemoved = [member];
        }

        return WrapContext<IConversationUpdateActivity>(activity);
    }

    private static (Mock<ITurnContext<IMessageActivity>> turnContext, Activity activity) MessageTurn(
        string text, string conversationType, string? conversationId = null, bool includeChannelData = false)
    {
        var activity = BaseActivity(ActivityTypes.Message, conversationType);
        activity.Text = text;
        if (conversationId != null) activity.Conversation.Id = conversationId;

        if (includeChannelData)
        {
            activity.ChannelData = JsonSerializer.SerializeToElement(new
            {
                team = new { id = TeamThreadId, aadGroupId = TeamGuid, name = "Test Team" },
                channel = new { id = ChannelThreadId }
            });
        }

        var turnContext = WrapContext<IMessageActivity>(activity);
        return (turnContext, activity);
    }

    private static Mock<ITurnContext<IInvokeActivity>> InvokeTurn(object actionData)
    {
        var activity = BaseActivity(ActivityTypes.Invoke, "personal");
        activity.Name = "adaptiveCard/action";
        activity.Value = JsonSerializer.SerializeToElement(new
        {
            action = new { type = "Action.Execute", data = actionData }
        });
        return WrapContext<IInvokeActivity>(activity);
    }

    private static Activity BaseActivity(string type, string conversationType) => new()
    {
        Type = type,
        Recipient = new ChannelAccount { Id = "bot-id", Name = "Bot" },
        From = new ChannelAccount { Id = "user-id", Name = "User", AadObjectId = "user-aad-oid" },
        Conversation = new ConversationAccount { Id = "conv-id", ConversationType = conversationType },
        ChannelId = "msteams",
        ServiceUrl = "https://smba.trafficmanager.net/emea/"
    };

    private static Mock<ITurnContext<T>> WrapContext<T>(Activity activity) where T : class, IActivity
        => TurnContextStub.Wrap<T>(activity);

    // --- Assertion helpers ---

    private static void VerifySentTextContaining<T>(Mock<ITurnContext<T>> turnContext, string fragment)
        where T : class, IActivity
    {
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text != null && ((Activity)a).Text.Contains(fragment)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool OperationIs(string json, string operation, string teamGuid)
    {
        var message = JsonSerializer.Deserialize<BotOperationMessage>(json);
        return message != null && message.Operation == operation && message.TeamGuid == teamGuid;
    }

    private static bool TeamNameIs(string json, string teamName)
    {
        var message = JsonSerializer.Deserialize<BotOperationMessage>(json);
        return message?.TeamName == teamName;
    }

    private static bool HasSerializedReference(string json)
    {
        var message = JsonSerializer.Deserialize<BotOperationMessage>(json);
        return !string.IsNullOrEmpty(message?.SerializedReference);
    }
}
