using Azure;
using Azure.Data.Tables;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

/// <summary>
/// Testable wrapper that exposes protected methods for direct testing.
/// </summary>
public class TestableTeamsBotHandler : TeamsBotHandler
{
    public TestableTeamsBotHandler(
        IBotService botService,
        IAliasService aliasService,
        [FromKeyedServices("teamlookup")] TableClient teamLookupTable,
        [FromKeyedServices("botoperations")] QueueClient botOpsQueue,
        Microsoft.Extensions.Logging.ILogger<TeamsBotHandler> logger,
        IQueueManagementService? queueService = null)
        : base(botService, aliasService, teamLookupTable, botOpsQueue, logger, queueService) { }

    public Task<AdaptiveCardInvokeResponse> TestOnAdaptiveCardInvokeAsync(
        ITurnContext<IInvokeActivity> turnContext,
        AdaptiveCardInvokeValue invokeValue,
        CancellationToken cancellationToken)
        => OnAdaptiveCardInvokeAsync(turnContext, invokeValue, cancellationToken);
}

public class TeamsBotHandlerTests
{
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<TableClient> _teamLookupTable = new();
    private readonly Mock<QueueClient> _botOpsQueue = new();
    private readonly Mock<IQueueManagementService> _queueService = new();
    private readonly TeamsBotHandler _handler;
    private readonly TeamsBotHandler _handlerWithQueues;
    private readonly TestableTeamsBotHandler _testableHandler;

    public TeamsBotHandlerTests()
    {
        _handler = new TeamsBotHandler(
            _botService.Object,
            _aliasService.Object,
            _teamLookupTable.Object,
            _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance);
        _handlerWithQueues = new TeamsBotHandler(
            _botService.Object,
            _aliasService.Object,
            _teamLookupTable.Object,
            _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance,
            _queueService.Object);
        _testableHandler = new TestableTeamsBotHandler(
            _botService.Object,
            _aliasService.Object,
            _teamLookupTable.Object,
            _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance);
    }

    [Fact]
    public async Task CheckinCommand_RepliesWithStatus()
    {
        var (turnContext, _) = CreateMessageContext("checkin");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Bot is online") && ((Activity)a).Text.Contains("Version:")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownCommand_ShowsUnknownAndSuggestsHelp()
    {
        var (turnContext, _) = CreateMessageContext("hello there");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Unknown command") && ((Activity)a).Text.Contains("help")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EmptyText_ShowsUnknownCommand()
    {
        var (turnContext, _) = CreateMessageContext(null);

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Unknown command") && ((Activity)a).Text.Contains("help")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PleaseCheckinNow_FallsThroughToUnknownCommand()
    {
        // "please checkin now" doesn't start with "checkin" — falls through to unknown
        var (turnContext, _) = CreateMessageContext("please checkin now");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Unknown command")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpCommand_ShowsOverviewWithTopics()
    {
        var (turnContext, _) = CreateMessageContext("help");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("help aliases") &&
                ((Activity)a).Text.Contains("help endpoints") &&
                ((Activity)a).Text.Contains("help queues") &&
                ((Activity)a).Text.Contains("help diagnostics")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpAliases_ShowsAliasCommands()
    {
        var (turnContext, _) = CreateMessageContext("help aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("set-alias") &&
                ((Activity)a).Text.Contains("remove-alias") &&
                ((Activity)a).Text.Contains("list-aliases") &&
                ((Activity)a).Text.Contains("create-alias")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpQueues_ShowsQueueCommands()
    {
        var (turnContext, _) = CreateMessageContext("help queues");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("queue-status") &&
                ((Activity)a).Text.Contains("queue-peek") &&
                ((Activity)a).Text.Contains("queue-retry") &&
                ((Activity)a).Text.Contains("poison")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpEndpoints_ShowsApiUrls()
    {
        var (turnContext, _) = CreateMessageContext("help endpoints");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("/api/v1/notify/") &&
                ((Activity)a).Text.Contains("/api/v1/alert/") &&
                ((Activity)a).Text.Contains("setup-guide")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpDiagnostics_ShowsDiagCommands()
    {
        var (turnContext, _) = CreateMessageContext("help diagnostics");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("checkin") &&
                ((Activity)a).Text.Contains("setup-guide") &&
                ((Activity)a).Text.Contains("delete-post")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpUnknownTopic_ShowsAvailableTopics()
    {
        var (turnContext, _) = CreateMessageContext("help foobar");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("Unknown help topic") &&
                ((Activity)a).Text.Contains("foobar")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAliasCommand_CallsAliasServiceAndConfirms()
    {
        AliasEntity? capturedEntity = null;
        _aliasService.Setup(s => s.SetAliasAsync("myalias", It.IsAny<AliasEntity>()))
            .Callback<string, AliasEntity>((_, e) => capturedEntity = e)
            .ReturnsAsync(new AliasEntity { RowKey = "myalias" });

        var (turnContext, _) = CreateMessageContext("set-alias myalias Some description",
            conversationType: "channel", teamAadGroupId: "team-guid-1");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.SetAliasAsync("myalias", It.IsAny<AliasEntity>()), Times.Once);
        Assert.NotNull(capturedEntity);
        Assert.Equal("channel", capturedEntity.TargetType);
        Assert.Equal("team-guid-1", capturedEntity.TeamId);
        Assert.Equal("Some description", capturedEntity.Description);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("myalias") &&
                ((Activity)a).Text.Contains("set") &&
                ((Activity)a).Text.Contains("/api/v1/notify/myalias")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAliasCommand_MissingName_RepliesWithUsage()
    {
        var (turnContext, _) = CreateMessageContext("set-alias");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Usage")),
            It.IsAny<CancellationToken>()), Times.Once);

        _aliasService.Verify(s => s.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
    }

    [Fact]
    public async Task SetAliasCommand_InvalidName_RepliesWithValidationError()
    {
        var (turnContext, _) = CreateMessageContext("set-alias A!");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Invalid alias name")),
            It.IsAny<CancellationToken>()), Times.Once);

        _aliasService.Verify(s => s.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
    }

    [Fact]
    public async Task RemoveAliasCommand_ExistingAlias_RemovesAndConfirms()
    {
        _aliasService.Setup(s => s.RemoveAliasAsync("myalias")).ReturnsAsync(true);

        var (turnContext, _) = CreateMessageContext("remove-alias myalias");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.RemoveAliasAsync("myalias"), Times.Once);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("myalias") && ((Activity)a).Text.Contains("removed")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAliasCommand_NotFound_RepliesNotFound()
    {
        _aliasService.Setup(s => s.RemoveAliasAsync("noexist")).ReturnsAsync(false);

        var (turnContext, _) = CreateMessageContext("remove-alias noexist");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("noexist") && ((Activity)a).Text.Contains("not found")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_WithAliases_ListsThem()
    {
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "dev-test", TargetType = "channel", TeamId = "t1", ChannelId = "c1", Description = "Dev", CreatedByName = "Alice", CreatedAt = DateTimeOffset.UtcNow.AddHours(-2) },
            new() { RowKey = "ops-alerts", TargetType = "channel", TeamId = "t2", ChannelId = "c2", CreatedAt = DateTimeOffset.UtcNow.AddDays(-5) }
        });

        // Return display names for channel lookup
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("t1", "c1"))
            .ReturnsAsync(new ConversationReferenceEntity { ChannelName = "Development", TeamName = "Engineering" });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("t2", "c2"))
            .ReturnsAsync((ConversationReferenceEntity?)null);

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a,
                "dev-test", "ops-alerts",
                "#Development in Engineering",
                "Alice",
                "/api/v1/notify/dev-test",
                "/api/v1/notify/ops-alerts")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_Empty_RepliesNoAliases()
    {
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>());

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("No aliases")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_PersonalAlias_ShowsUserName()
    {
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "my-dm", TargetType = "personal", UserId = "user-oid-123", CreatedAt = DateTimeOffset.UtcNow }
        });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("user", "user-oid-123"))
            .ReturnsAsync(new ConversationReferenceEntity { UserName = "Bob Smith" });

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a, "Bob Smith", "personal chat")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_GroupChatAlias_ShowsGroupChat()
    {
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "my-chat", TargetType = "groupChat", ChatId = "chat-id-1", CreatedAt = DateTimeOffset.UtcNow }
        });

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a, "group chat")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_ChannelNoEntity_FallsBackToGuids()
    {
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "orphan", TargetType = "channel", TeamId = "t-guid", ChannelId = "c-id", CreatedAt = DateTimeOffset.UtcNow }
        });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("t-guid", "c-id"))
            .ReturnsAsync((ConversationReferenceEntity?)null);

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a, "c-id", "t-guid")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_PersonalAlias_FallsBackToConversationReferenceJson()
    {
        // Entity has UserName=null, but ConversationReference JSON has User.Name
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "dm-alias", TargetType = "personal", UserId = "oid-123", CreatedAt = DateTimeOffset.UtcNow }
        });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("user", "oid-123"))
            .ReturnsAsync(new ConversationReferenceEntity
            {
                UserName = null,
                ConversationReference = """{"User":{"Name":"Jane Doe"},"Conversation":{"Id":"a]oid-123"}}"""
            });

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a, "Jane Doe")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_PersonalAlias_NoEntityNoJson_FallsBackToUserId()
    {
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "dm-alias", TargetType = "personal", UserId = "oid-unknown", CreatedAt = DateTimeOffset.UtcNow }
        });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("user", "oid-unknown"))
            .ReturnsAsync((ConversationReferenceEntity?)null);

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a, "oid-unknown")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_ChannelAlias_FallsBackToConversationReferenceJson()
    {
        // Entity has ChannelName=null, but ConversationReference JSON has Conversation.Name
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "ch-alias", TargetType = "channel", TeamId = "t1", ChannelId = "19:abc@thread.tacv2",
                     CreatedAt = DateTimeOffset.UtcNow }
        });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("t1", "19:abc@thread.tacv2"))
            .ReturnsAsync(new ConversationReferenceEntity
            {
                ChannelName = null,
                TeamName = "My Team",
                ConversationReference = """{"Conversation":{"Name":"test-channel","Id":"19:abc@thread.tacv2"}}"""
            });

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a, "#test-channel", "My Team")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAliasesCommand_ChannelAlias_EntityHasTeamNameOnly_ShowsChannelIdFallback()
    {
        // Entity has TeamName but ChannelName=null and JSON has no Conversation.Name
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new() { RowKey = "ch2", TargetType = "channel", TeamId = "t1", ChannelId = "19:xyz@thread.tacv2",
                     CreatedAt = DateTimeOffset.UtcNow }
        });
        _botService.Setup(s => s.GetConversationReferenceEntityAsync("t1", "19:xyz@thread.tacv2"))
            .ReturnsAsync(new ConversationReferenceEntity
            {
                ChannelName = null,
                TeamName = "DevOps Team",
                ConversationReference = """{"Conversation":{"Id":"19:xyz@thread.tacv2"}}"""
            });

        var (turnContext, _) = CreateMessageContext("list-aliases");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        // Should show "channel in TeamName" (team name resolved, channel name unknown)
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => IsAdaptiveCardContaining(a, "channel in DevOps Team")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Delete-post command tests ---

    [Fact]
    public async Task DeletePostCommand_InChannel_WithReplyToId_DeletesActivity()
    {
        var (turnContext, _) = CreateMessageContext("delete-post",
            conversationType: "channel", teamAadGroupId: "team-guid-1", replyToId: "1:msg-id");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.DeleteActivityAsync("1:msg-id", It.IsAny<CancellationToken>()), Times.Once);
        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletePostCommand_InChannel_WithoutReplyToId_NoThread_SendsUsageHint()
    {
        var (turnContext, _) = CreateMessageContext("delete-post",
            conversationType: "channel", teamAadGroupId: "team-guid-1");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Reply to a bot message")),
            It.IsAny<CancellationToken>()), Times.Once);
        turnContext.Verify(t => t.DeleteActivityAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletePostCommand_InChannel_WithoutReplyToId_ThreadConversation_DeletesRootMessage()
    {
        // Teams thread replies may not set ReplyToId, but the conversation ID contains the root message ID
        var (turnContext, _) = CreateMessageContext("delete-post",
            conversationType: "channel", teamAadGroupId: "team-guid-1",
            conversationId: "19:aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789abcdef01@thread.tacv2;messageid=1700000000000");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.DeleteActivityAsync("1700000000000", It.IsAny<CancellationToken>()), Times.Once);
        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletePostCommand_InPersonalChat_WithThreadConversation_StillRejected()
    {
        // Ensure thread ID extraction doesn't bypass the channel-only check
        var (turnContext, _) = CreateMessageContext("delete-post",
            conversationType: "personal",
            conversationId: "19:xxx@thread.tacv2;messageid=1234567890");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("only available in team channels")),
            It.IsAny<CancellationToken>()), Times.Once);
        turnContext.Verify(t => t.DeleteActivityAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletePostCommand_InGroupChat_WithThreadConversation_StillRejected()
    {
        // Ensure thread ID extraction doesn't bypass the channel-only check
        var (turnContext, _) = CreateMessageContext("delete-post",
            conversationType: "groupChat",
            conversationId: "19:xxx@thread.tacv2;messageid=1234567890");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("only available in team channels")),
            It.IsAny<CancellationToken>()), Times.Once);
        turnContext.Verify(t => t.DeleteActivityAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeletePostCommand_InChannel_DeleteThrows_SendsErrorMessage()
    {
        var (turnContext, _) = CreateMessageContext("delete-post",
            conversationType: "channel", teamAadGroupId: "team-guid-1", replyToId: "1:msg-id");
        turnContext.Setup(t => t.DeleteActivityAsync("1:msg-id", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Forbidden"));

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Could not delete")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletePostCommand_InPersonalChat_RejectedWithMessage()
    {
        var (turnContext, _) = CreateMessageContext("delete-post",
            conversationType: "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("only available in team channels")),
            It.IsAny<CancellationToken>()), Times.Once);
        turnContext.Verify(t => t.DeleteActivityAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HelpDiagnostics_IncludesDeletePost()
    {
        var (turnContext, _) = CreateMessageContext("help diagnostics");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("delete-post")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Create-alias card command tests ---

    [Fact]
    public async Task CreateAliasCommand_SendsAdaptiveCardAttachment()
    {
        var (turnContext, _) = CreateMessageContext("create-alias",
            conversationType: "channel", teamAadGroupId: "team-guid-1");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Attachments != null &&
                ((Activity)a).Attachments.Count == 1 &&
                ((Activity)a).Attachments[0].ContentType == "application/vnd.microsoft.card.adaptive"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AdaptiveCardInvoke_CreateAlias_Valid_CreatesAlias()
    {
        _aliasService.Setup(s => s.SetAliasAsync("test-alias", It.IsAny<AliasEntity>()))
            .ReturnsAsync(new AliasEntity { RowKey = "test-alias" });

        var (turnContext, invokeValue) = CreateAdaptiveCardInvokeInput(new
        {
            action = "createAlias",
            aliasName = "test-alias",
            aliasDescription = "My test alias"
        }, conversationType: "channel", teamAadGroupId: "team-guid-1");

        var response = await _testableHandler.TestOnAdaptiveCardInvokeAsync(
            turnContext.Object, invokeValue, CancellationToken.None);

        _aliasService.Verify(s => s.SetAliasAsync("test-alias", It.IsAny<AliasEntity>()), Times.Once);
        Assert.Equal(200, response.StatusCode);
    }

    [Fact]
    public async Task AdaptiveCardInvoke_CreateAlias_InvalidName_ReturnsError()
    {
        var (turnContext, invokeValue) = CreateAdaptiveCardInvokeInput(new
        {
            action = "createAlias",
            aliasName = "A!",
            aliasDescription = ""
        }, conversationType: "channel", teamAadGroupId: "team-guid-1");

        var response = await _testableHandler.TestOnAdaptiveCardInvokeAsync(
            turnContext.Object, invokeValue, CancellationToken.None);

        _aliasService.Verify(s => s.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
        Assert.Equal(400, response.StatusCode);
    }

    [Fact]
    public async Task AdaptiveCardInvoke_CreateAlias_EmptyName_ReturnsError()
    {
        var (turnContext, invokeValue) = CreateAdaptiveCardInvokeInput(new
        {
            action = "createAlias",
            aliasName = "",
            aliasDescription = ""
        }, conversationType: "channel", teamAadGroupId: "team-guid-1");

        var response = await _testableHandler.TestOnAdaptiveCardInvokeAsync(
            turnContext.Object, invokeValue, CancellationToken.None);

        _aliasService.Verify(s => s.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
        Assert.Equal(400, response.StatusCode);
    }

    // --- Card submit via message activity tests (Action.Submit flow) ---

    [Fact]
    public async Task CardSubmit_CreateAlias_Valid_CreatesAliasAndConfirms()
    {
        _aliasService.Setup(s => s.SetAliasAsync("card-alias", It.IsAny<AliasEntity>()))
            .ReturnsAsync(new AliasEntity { RowKey = "card-alias" });

        var (turnContext, activity) = CreateMessageContext(null, conversationType: "personal");
        activity.Value = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            action = "createAlias",
            aliasName = "card-alias",
            aliasDescription = "Created via card"
        });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.SetAliasAsync("card-alias", It.Is<AliasEntity>(e =>
            e.TargetType == "personal" && e.Description == "Created via card")), Times.Once);
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("card-alias") &&
                ((Activity)a).Text.Contains("created") &&
                ((Activity)a).Text.Contains("/api/v1/notify/card-alias")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CardSubmit_CreateAlias_InvalidName_ReturnsError()
    {
        var (turnContext, activity) = CreateMessageContext(null, conversationType: "personal");
        activity.Value = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            action = "createAlias",
            aliasName = "INVALID!",
            aliasDescription = ""
        });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Invalid alias name")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CardSubmit_CreateAlias_EmptyName_ReturnsError()
    {
        var (turnContext, activity) = CreateMessageContext(null, conversationType: "personal");
        activity.Value = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            action = "createAlias",
            aliasName = "",
            aliasDescription = ""
        });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.SetAliasAsync(It.IsAny<string>(), It.IsAny<AliasEntity>()), Times.Never);
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("required")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CardSubmit_UnknownAction_SilentlyIgnored()
    {
        var (turnContext, activity) = CreateMessageContext(null, conversationType: "personal");
        activity.Value = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            action = "unknownAction"
        });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        // Should not send any message or create any alias
        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CardSubmit_CreateAlias_Channel_CreatesChannelAlias()
    {
        _aliasService.Setup(s => s.SetAliasAsync("channel-card", It.IsAny<AliasEntity>()))
            .ReturnsAsync(new AliasEntity { RowKey = "channel-card" });

        var (turnContext, activity) = CreateMessageContext(null,
            conversationType: "channel", teamAadGroupId: "team-guid-1");
        activity.Value = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            action = "createAlias",
            aliasName = "channel-card",
            aliasDescription = ""
        });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.SetAliasAsync("channel-card", It.Is<AliasEntity>(e =>
            e.TargetType == "channel" && e.TeamId == "team-guid-1")), Times.Once);
    }

    [Fact]
    public async Task CardSubmit_WithValue_DoesNotDispatchToTextCommands()
    {
        // Even if text says "checkin", Value takes precedence
        var (turnContext, activity) = CreateMessageContext("checkin", conversationType: "personal");
        activity.Value = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            action = "unknownAction"
        });

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        // Should NOT respond with checkin status — Value intercepts before text dispatch
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text != null && ((Activity)a).Text.Contains("Bot is online")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Setup guide command tests ---

    [Fact]
    public async Task SetupGuideCommand_SendsAdaptiveCardAttachment()
    {
        var (turnContext, _) = CreateMessageContext("setup-guide");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Attachments != null &&
                ((Activity)a).Attachments.Count == 1 &&
                ((Activity)a).Attachments[0].ContentType == "application/vnd.microsoft.card.adaptive"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpEndpoints_IncludesSetupGuide()
    {
        var (turnContext, _) = CreateMessageContext("help endpoints");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("setup-guide")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Queue management command tests ---
    // Auth model: any authenticated user (valid Entra ID) can run queue commands in any scope.
    // See TeamsBotHandler.IsAuthorizedForQueueCommandsAsync for deviation note.

    [Fact]
    public async Task QueueStatusCommand_PersonalChat_Authorized()
    {
        _queueService.Setup(q => q.GetQueueStatusAsync()).ReturnsAsync(new Dictionary<string, int>
        {
            ["notifications"] = 5,
            ["botoperations"] = 0,
            ["notifications-poison"] = 2,
            ["botoperations-poison"] = 0
        });

        var (turnContext, _) = CreateMessageContext("queue-status", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("Queue Status") &&
                ((Activity)a).Text.Contains("notifications") &&
                ((Activity)a).Text.Contains("5")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueStatusCommand_GroupChat_Authorized()
    {
        _queueService.Setup(q => q.GetQueueStatusAsync()).ReturnsAsync(new Dictionary<string, int>
        {
            ["notifications"] = 0,
            ["botoperations"] = 0,
            ["notifications-poison"] = 0,
            ["botoperations-poison"] = 0
        });

        var (turnContext, _) = CreateMessageContext("queue-status", conversationType: "groupChat");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Queue Status")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueStatusCommand_Channel_Authorized()
    {
        _queueService.Setup(q => q.GetQueueStatusAsync()).ReturnsAsync(new Dictionary<string, int>
        {
            ["notifications"] = 0,
            ["botoperations"] = 0,
            ["notifications-poison"] = 0,
            ["botoperations-poison"] = 0
        });

        var (turnContext, _) = CreateMessageContext("queue-status",
            conversationType: "channel", teamThreadId: "19:thread@thread.tacv2");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Queue Status")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueStatusCommand_NullAadObjectId_Rejected()
    {
        // User with no Entra ID cannot run queue commands
        var (turnContext, activity) = CreateMessageContext("queue-status", conversationType: "personal");
        activity.From = new ChannelAccount { Id = "user-id", Name = "User", AadObjectId = null };

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("require a valid Entra ID")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueuePeekCommand_MissingQueueName_ShowsUsage()
    {
        var (turnContext, _) = CreateMessageContext("queue-peek", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Usage")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueuePeekCommand_InvalidQueueName_ShowsUsage()
    {
        var (turnContext, _) = CreateMessageContext("queue-peek some-invalid-queue", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Usage")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueuePeekCommand_EmptyQueue_ShowsEmpty()
    {
        _queueService.Setup(q => q.PeekMessagesAsync("notifications-poison", 5))
            .ReturnsAsync(new List<PeekedMessage>());

        var (turnContext, _) = CreateMessageContext("queue-peek notifications-poison", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("empty")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueRetryCommand_RetriesMessages()
    {
        _queueService.Setup(q => q.RetryMessagesAsync("notifications-poison", 3))
            .ReturnsAsync(3);

        var (turnContext, _) = CreateMessageContext("queue-retry notifications-poison 3", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("Retried") &&
                ((Activity)a).Text.Contains("3")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueRetryAllCommand_RetriesAllMessages()
    {
        _queueService.Setup(q => q.RetryAllMessagesAsync("botoperations-poison"))
            .ReturnsAsync(10);

        var (turnContext, _) = CreateMessageContext("queue-retry-all botoperations-poison", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("Retried all") &&
                ((Activity)a).Text.Contains("10")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueStatusCommand_NoQueueService_ShowsUnavailable()
    {
        // Use _handler (no queue service) instead of _handlerWithQueues
        var (turnContext, _) = CreateMessageContext("queue-status", conversationType: "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("not available")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpQueues_IncludesAllQueueCommands()
    {
        var (turnContext, _) = CreateMessageContext("help queues");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("queue-status") &&
                ((Activity)a).Text.Contains("queue-peek") &&
                ((Activity)a).Text.Contains("queue-retry")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpOverview_DoesNotContainAdminReferences()
    {
        var (turnContext, _) = CreateMessageContext("help");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        // Verify no sent activity contains admin/owner references
        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text != null && (
                ((Activity)a).Text.Contains("admin", StringComparison.OrdinalIgnoreCase) ||
                ((Activity)a).Text.Contains("owner", StringComparison.OrdinalIgnoreCase))),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task QueuePeekCommand_WithMessages_ShowsContent()
    {
        var messages = new List<PeekedMessage>
        {
            QueuesModelFactory.PeekedMessage(
                messageId: "msg-1",
                messageText: """{"alias":"test","message":"Hello world"}""",
                dequeueCount: 0),
            QueuesModelFactory.PeekedMessage(
                messageId: "msg-2",
                messageText: """{"alias":"test2","message":"Another"}""",
                dequeueCount: 0)
        };
        _queueService.Setup(q => q.PeekMessagesAsync("notifications-poison", 5))
            .ReturnsAsync(messages);

        var (turnContext, _) = CreateMessageContext("queue-peek notifications-poison", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("notifications-poison") &&
                ((Activity)a).Text.Contains("2 peeked")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueuePeekCommand_CustomCount_UsesProvidedCount()
    {
        _queueService.Setup(q => q.PeekMessagesAsync("notifications-poison", 10))
            .ReturnsAsync(new List<PeekedMessage>());

        var (turnContext, _) = CreateMessageContext("queue-peek notifications-poison 10", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        _queueService.Verify(q => q.PeekMessagesAsync("notifications-poison", 10), Times.Once);
    }

    [Fact]
    public async Task QueuePeekCommand_CountCappedAt32()
    {
        _queueService.Setup(q => q.PeekMessagesAsync("notifications-poison", 32))
            .ReturnsAsync(new List<PeekedMessage>());

        var (turnContext, _) = CreateMessageContext("queue-peek notifications-poison 100", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        _queueService.Verify(q => q.PeekMessagesAsync("notifications-poison", 32), Times.Once);
    }

    [Fact]
    public async Task QueueRetryCommand_NonNumericCount_DefaultsToOne()
    {
        _queueService.Setup(q => q.RetryMessagesAsync("notifications-poison", 1))
            .ReturnsAsync(1);

        var (turnContext, _) = CreateMessageContext("queue-retry notifications-poison abc", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        _queueService.Verify(q => q.RetryMessagesAsync("notifications-poison", 1), Times.Once);
    }

    [Fact]
    public async Task QueueRetryAllCommand_MissingQueueName_ShowsUsage()
    {
        var (turnContext, _) = CreateMessageContext("queue-retry-all", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Usage")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task QueueStatusCommand_WithErrors_ShowsErrorForFailedQueues()
    {
        _queueService.Setup(q => q.GetQueueStatusAsync()).ReturnsAsync(new Dictionary<string, int>
        {
            ["notifications"] = 5,
            ["botoperations"] = -1,
            ["notifications-poison"] = 2,
            ["botoperations-poison"] = 0
        });

        var (turnContext, _) = CreateMessageContext("queue-status", conversationType: "personal");

        await ((IAgent)_handlerWithQueues).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a =>
                ((Activity)a).Text.Contains("error") &&
                ((Activity)a).Text.Contains("5")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // --- Regression tests: channel key extraction ---

    [Fact]
    public async Task SetAliasCommand_ChannelWithoutAadGroupId_ResolvesViaTeamLookup()
    {
        // Regular @mention messages don't include aadGroupId in channelData.
        // ExtractConversationKeysAsync must resolve the team GUID via the teamlookup table.
        _teamLookupTable.Setup(t => t.GetEntityAsync<TeamLookupEntity>(
            "teamlookup", "19:thread@thread.tacv2",
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(
                new TeamLookupEntity { TeamGuid = "resolved-team-guid", RowKey = "19:thread@thread.tacv2" },
                Mock.Of<Response>()));

        AliasEntity? capturedEntity = null;
        _aliasService.Setup(s => s.SetAliasAsync("test-alias", It.IsAny<AliasEntity>()))
            .Callback<string, AliasEntity>((_, e) => capturedEntity = e)
            .ReturnsAsync(new AliasEntity { RowKey = "test-alias" });

        var (turnContext, _) = CreateMessageContext("set-alias test-alias My description",
            conversationType: "channel", teamThreadId: "19:thread@thread.tacv2");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.SetAliasAsync("test-alias", It.IsAny<AliasEntity>()), Times.Once);
        Assert.NotNull(capturedEntity);
        Assert.Equal("channel", capturedEntity.TargetType);
        Assert.Equal("resolved-team-guid", capturedEntity.TeamId);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("test-alias") && ((Activity)a).Text.Contains("set")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAliasCommand_ThreadReply_UsesCleanChannelId()
    {
        // When set-alias is used in a thread reply, the conversation ID contains
        // ";messageid=<id>". The alias must store the clean channel ID, not the
        // thread-specific one.
        AliasEntity? capturedEntity = null;
        _aliasService.Setup(s => s.SetAliasAsync("thread-alias", It.IsAny<AliasEntity>()))
            .Callback<string, AliasEntity>((_, e) => capturedEntity = e)
            .ReturnsAsync(new AliasEntity { RowKey = "thread-alias" });

        var (turnContext, _) = CreateMessageContext("set-alias thread-alias Thread test",
            conversationType: "channel", teamAadGroupId: "team-guid-1",
            conversationId: "19:channel@thread.tacv2;messageid=1772090278084",
            channelId: "19:channel@thread.tacv2");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        _aliasService.Verify(s => s.SetAliasAsync("thread-alias", It.IsAny<AliasEntity>()), Times.Once);
        Assert.NotNull(capturedEntity);
        Assert.Equal("channel", capturedEntity.TargetType);
        Assert.Equal("19:channel@thread.tacv2", capturedEntity.ChannelId);
        Assert.DoesNotContain(";messageid=", capturedEntity.ChannelId);
    }

    // --- Install event tests ---

    [Fact]
    public async Task BotInstalledInTeam_SendsWelcomeWithHelpReference()
    {
        var (turnContext, _) = CreateInstallContext("add", "channel", teamAadGroupId: "team-guid-1");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("aliases") && ((Activity)a).Text.Contains("help")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BotInstalledInPersonalChat_SendsPersonalGreeting()
    {
        var (turnContext, _) = CreateInstallContext("add", "personal");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("direct notifications") && ((Activity)a).Text.Contains("help")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BotInstalledInGroupChat_SendsGroupGreeting()
    {
        var (turnContext, _) = CreateInstallContext("add", "groupChat");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("notifications to this chat") && ((Activity)a).Text.Contains("help")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BotRemoved_NoWelcomeMessage()
    {
        var (turnContext, _) = CreateInstallContext("remove", "channel", teamAadGroupId: "team-guid-1");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Text.Contains("Hi")),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Poison alias setup nudge tests (v1.5 §5) ---

    [Fact]
    public async Task GetPoisonAliasNudge_EnvEmpty_ReturnsConfigWarning()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", null);
            var nudge = await _handler.GetPoisonAliasNudgeAsync();
            Assert.NotNull(nudge);
            Assert.Contains("not configured", nudge);
            Assert.Contains("PoisonAlertAlias", nudge);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public async Task GetPoisonAliasNudge_AliasNotCreated_ReturnsSetupWarning()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts")).ReturnsAsync((AliasEntity?)null);

            // Create fresh handler to avoid cache from prior tests
            var handler = new TeamsBotHandler(
                _botService.Object, _aliasService.Object, _teamLookupTable.Object,
                _botOpsQueue.Object, NullLogger<TeamsBotHandler>.Instance);

            var nudge = await handler.GetPoisonAliasNudgeAsync();
            Assert.NotNull(nudge);
            Assert.Contains("ops-alerts", nudge);
            Assert.Contains("Setup incomplete", nudge);
            Assert.Contains("set-alias ops-alerts", nudge);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public async Task GetPoisonAliasNudge_AliasExists_ReturnsNull()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts"))
                .ReturnsAsync(new AliasEntity { TargetType = "channel" });

            var handler = new TeamsBotHandler(
                _botService.Object, _aliasService.Object, _teamLookupTable.Object,
                _botOpsQueue.Object, NullLogger<TeamsBotHandler>.Instance);

            var nudge = await handler.GetPoisonAliasNudgeAsync();
            Assert.Null(nudge);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public async Task Checkin_ShowsNudge_WhenPoisonAliasNotCreated()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts")).ReturnsAsync((AliasEntity?)null);

            var handler = new TeamsBotHandler(
                _botService.Object, _aliasService.Object, _teamLookupTable.Object,
                _botOpsQueue.Object, NullLogger<TeamsBotHandler>.Instance);

            var (turnContext, _) = CreateMessageContext("checkin");
            await ((IAgent)handler).OnTurnAsync(turnContext.Object);

            // Should have 2 SendActivityAsync calls: checkin response + nudge
            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("Bot is online")),
                It.IsAny<CancellationToken>()), Times.Once);
            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("Setup incomplete")),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public async Task Help_ShowsNudge_WhenPoisonAliasNotCreated()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts")).ReturnsAsync((AliasEntity?)null);

            var handler = new TeamsBotHandler(
                _botService.Object, _aliasService.Object, _teamLookupTable.Object,
                _botOpsQueue.Object, NullLogger<TeamsBotHandler>.Instance);

            var (turnContext, _) = CreateMessageContext("help");
            await ((IAgent)handler).OnTurnAsync(turnContext.Object);

            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("help aliases")),
                It.IsAny<CancellationToken>()), Times.Once);
            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("Setup incomplete")),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public async Task ListAliases_ShowsNudge_WhenPoisonAliasNotCreated()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts")).ReturnsAsync((AliasEntity?)null);
            _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>());

            var handler = new TeamsBotHandler(
                _botService.Object, _aliasService.Object, _teamLookupTable.Object,
                _botOpsQueue.Object, NullLogger<TeamsBotHandler>.Instance);

            var (turnContext, _) = CreateMessageContext("list-aliases");
            await ((IAgent)handler).OnTurnAsync(turnContext.Object);

            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("No aliases")),
                It.IsAny<CancellationToken>()), Times.Once);
            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("Setup incomplete")),
                It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public void InvalidatePoisonNudgeCache_MatchingAlias_ResetsExpiry()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _handler.InvalidatePoisonNudgeCacheIfMatch("ops-alerts");
            // No exception — method works. Cache expiry is internal, but we verify
            // the method doesn't throw and matches case-insensitively.
            _handler.InvalidatePoisonNudgeCacheIfMatch("OPS-ALERTS");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public async Task Checkin_NoNudge_WhenPoisonAliasExists()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts"))
                .ReturnsAsync(new AliasEntity { TargetType = "channel" });

            var handler = new TeamsBotHandler(
                _botService.Object, _aliasService.Object, _teamLookupTable.Object,
                _botOpsQueue.Object, NullLogger<TeamsBotHandler>.Instance);

            var (turnContext, _) = CreateMessageContext("checkin");
            await ((IAgent)handler).OnTurnAsync(turnContext.Object);

            // Only 1 SendActivityAsync: checkin response, NO nudge
            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("Bot is online")),
                It.IsAny<CancellationToken>()), Times.Once);
            turnContext.Verify(t => t.SendActivityAsync(
                It.Is<IActivity>(a => ((Activity)a).Text.Contains("Setup incomplete")),
                It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    [Fact]
    public async Task SetAlias_InvalidatesNudgeCache()
    {
        var orig = Environment.GetEnvironmentVariable("PoisonAlertAlias");
        try
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", "ops-alerts");
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts")).ReturnsAsync((AliasEntity?)null);

            var handler = new TeamsBotHandler(
                _botService.Object, _aliasService.Object, _teamLookupTable.Object,
                _botOpsQueue.Object, NullLogger<TeamsBotHandler>.Instance);

            // First call should return nudge (alias not created)
            var nudge1 = await handler.GetPoisonAliasNudgeAsync();
            Assert.NotNull(nudge1);

            // Now "create" the alias and invalidate cache
            _aliasService.Setup(s => s.GetAliasAsync("ops-alerts"))
                .ReturnsAsync(new AliasEntity { TargetType = "channel" });
            handler.InvalidatePoisonNudgeCacheIfMatch("ops-alerts");

            // Second call should re-check and return null (alias now exists)
            var nudge2 = await handler.GetPoisonAliasNudgeAsync();
            Assert.Null(nudge2);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PoisonAlertAlias", orig);
        }
    }

    // --- Channel event tests ---

    [Fact]
    public async Task ChannelCreated_NoMessage()
    {
        var (turnContext, _) = CreateConversationUpdateContext("channelCreated");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChannelRestored_NoMessage()
    {
        var (turnContext, _) = CreateConversationUpdateContext("channelRestored");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ChannelDeleted_NoMessage()
    {
        var (turnContext, _) = CreateConversationUpdateContext("channelDeleted");

        await ((IAgent)_handler).OnTurnAsync(turnContext.Object);

        turnContext.Verify(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Helpers ---

    /// <summary>
    /// Extracts the serialized JSON content from the first Adaptive Card attachment in an activity.
    /// </summary>
    private static bool IsAdaptiveCardContaining(IActivity activity, params string[] expectedSubstrings)
    {
        var act = (Activity)activity;
        if (act.Attachments == null || act.Attachments.Count == 0)
            return false;
        if (act.Attachments[0].ContentType != "application/vnd.microsoft.card.adaptive")
            return false;
        var json = System.Text.Json.JsonSerializer.Serialize(act.Attachments[0].Content);
        return expectedSubstrings.All(s => json.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    private static (Mock<ITurnContext<IMessageActivity>> turnContext, Activity activity) CreateMessageContext(
        string? text,
        string conversationType = "personal",
        string? teamAadGroupId = null,
        string? teamThreadId = null,
        string? replyToId = null,
        string? conversationId = null,
        string? channelId = null)
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = text,
            ReplyToId = replyToId,
            Recipient = new ChannelAccount { Id = "bot-id", Name = "Bot" },
            From = new ChannelAccount { Id = "user-id", Name = "User", AadObjectId = "user-aad-oid" },
            Conversation = new ConversationAccount
            {
                Id = conversationId ?? "conv-id",
                ConversationType = conversationType
            },
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/emea/"
        };

        // Set channel data for team context if needed
        var effectiveChannelId = channelId ?? "conv-id";
        if (teamAadGroupId != null)
        {
            activity.ChannelData = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                team = new { id = teamThreadId ?? "19:thread@thread.tacv2", aadGroupId = teamAadGroupId, name = "Test Team" },
                channel = new { id = effectiveChannelId }
            });
        }
        else if (teamThreadId != null)
        {
            // Simulate regular @mention message: team.id present but no aadGroupId
            activity.ChannelData = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                team = new { id = teamThreadId, name = "Test Team" },
                channel = new { id = effectiveChannelId }
            });
        }

        var turnContext = new Mock<ITurnContext<IMessageActivity>>();
        turnContext.Setup(t => t.Activity).Returns(activity);
        turnContext.As<ITurnContext>().Setup(t => t.Activity).Returns(activity);
        turnContext.Setup(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceResponse());
        turnContext.Setup(t => t.DeleteActivityAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return (turnContext, activity);
    }

    private static (Mock<ITurnContext<IInstallationUpdateActivity>> turnContext, Activity activity) CreateInstallContext(
        string action,
        string conversationType,
        string? teamAadGroupId = null)
    {
        var activity = new Activity
        {
            Type = ActivityTypes.InstallationUpdate,
            Action = action,
            Recipient = new ChannelAccount { Id = "bot-id", Name = "Bot" },
            From = new ChannelAccount { Id = "user-id", Name = "User", AadObjectId = "user-aad-oid" },
            Conversation = new ConversationAccount
            {
                Id = conversationType == "personal" ? "a]user-id" : "conv-id",
                ConversationType = conversationType
            },
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/emea/"
        };

        if (teamAadGroupId != null || conversationType == "channel")
        {
            activity.ChannelData = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                team = new { id = "19:thread@thread.tacv2", aadGroupId = teamAadGroupId ?? "team-guid", name = "Test Team" },
                channel = new { id = activity.Conversation.Id }
            });
        }

        var turnContext = new Mock<ITurnContext<IInstallationUpdateActivity>>();
        turnContext.Setup(t => t.Activity).Returns(activity);
        turnContext.As<ITurnContext>().Setup(t => t.Activity).Returns(activity);
        turnContext.Setup(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceResponse());

        return (turnContext, activity);
    }

    private static (Mock<ITurnContext<IInvokeActivity>> turnContext, AdaptiveCardInvokeValue invokeValue) CreateAdaptiveCardInvokeInput(
        object actionData,
        string conversationType = "personal",
        string? teamAadGroupId = null)
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Invoke,
            Name = "adaptiveCard/action",
            Recipient = new ChannelAccount { Id = "bot-id", Name = "Bot" },
            From = new ChannelAccount { Id = "user-id", Name = "User", AadObjectId = "user-aad-oid" },
            Conversation = new ConversationAccount
            {
                Id = "conv-id",
                ConversationType = conversationType
            },
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/emea/"
        };

        if (teamAadGroupId != null)
        {
            activity.ChannelData = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                team = new { id = "19:thread@thread.tacv2", aadGroupId = teamAadGroupId, name = "Test Team" },
                channel = new { id = "conv-id" }
            });
        }

        var invokeValue = new AdaptiveCardInvokeValue
        {
            Action = new AdaptiveCardInvokeAction
            {
                Type = "Action.Submit",
                Data = System.Text.Json.JsonSerializer.SerializeToElement(actionData)
            }
        };

        var turnContext = new Mock<ITurnContext<IInvokeActivity>>();
        turnContext.Setup(t => t.Activity).Returns(activity);
        turnContext.As<ITurnContext>().Setup(t => t.Activity).Returns(activity);
        turnContext.Setup(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceResponse());

        return (turnContext, invokeValue);
    }

    private (Mock<ITurnContext<IConversationUpdateActivity>> turnContext, Activity activity) CreateConversationUpdateContext(
        string eventType)
    {
        var teamGuid = "team-guid-1";
        var channelId = "19:newchannel@thread.tacv2";
        var teamThreadId = "19:thread@thread.tacv2";

        var activity = new Activity
        {
            Type = ActivityTypes.ConversationUpdate,
            Recipient = new ChannelAccount { Id = "bot-id", Name = "Bot" },
            From = new ChannelAccount { Id = "user-id", Name = "User" },
            Conversation = new ConversationAccount
            {
                Id = channelId,
                IsGroup = true,
                ConversationType = "channel",
                TenantId = "tenant-id"
            },
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/emea/",
            ChannelData = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                eventType,
                team = new { id = teamThreadId, aadGroupId = teamGuid, name = "Test Team" },
                channel = new { id = channelId, name = "New Channel" }
            })
        };

        // For channelDeleted, set up RemoveConversationReferenceAsync
        if (eventType == "channelDeleted")
        {
            _botService.Setup(s => s.RemoveConversationReferenceAsync(teamGuid, channelId))
                .Returns(Task.CompletedTask);
        }

        var turnContext = new Mock<ITurnContext<IConversationUpdateActivity>>();
        turnContext.Setup(t => t.Activity).Returns(activity);
        turnContext.As<ITurnContext>().Setup(t => t.Activity).Returns(activity);
        turnContext.Setup(t => t.SendActivityAsync(
            It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceResponse());

        return (turnContext, activity);
    }
}
