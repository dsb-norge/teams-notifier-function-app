using Azure.Data.Tables;
using Azure.Storage.Queues;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class TeamsBotHandlerWebhookCommandsTests
{
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly Mock<TableClient> _teamLookupTable = new();
    private readonly Mock<QueueClient> _botOpsQueue = new();
    private readonly Mock<IWebhookService> _webhook = new();

    private TeamsBotHandler NewHandler(bool withWebhookService = true)
    {
        _webhook.Setup(s => s.ConfigureAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(true);

        return new TeamsBotHandler(
            _botService.Object, _aliasService.Object, _teamLookupTable.Object, _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance,
            queueService: null,
            webhookService: withWebhookService ? _webhook.Object : null);
    }

    private static Mock<ITurnContext<IMessageActivity>> Context(string text, string? aadObjectId = "user-aad-oid")
    {
        var activity = new Activity
        {
            Type = ActivityTypes.Message,
            Text = text,
            Recipient = new ChannelAccount { Id = "bot-id", Name = "Bot" },
            From = new ChannelAccount { Id = "user-id", Name = "User", AadObjectId = aadObjectId },
            Conversation = new ConversationAccount { Id = "conv-id", ConversationType = "personal" },
            ChannelId = "msteams",
            ServiceUrl = "https://smba.trafficmanager.net/emea/"
        };

        var ctx = new Mock<ITurnContext<IMessageActivity>>();
        ctx.Setup(t => t.Activity).Returns(activity);
        ctx.As<ITurnContext>().Setup(t => t.Activity).Returns(activity);
        ctx.Setup(t => t.SendActivityAsync(It.IsAny<IActivity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceResponse());
        return ctx;
    }

    private static bool TextContains(IActivity a, params string[] needles)
    {
        var text = ((Activity)a).Text ?? "";
        return needles.All(text.Contains);
    }

    private Task Run(TeamsBotHandler handler, Mock<ITurnContext<IMessageActivity>> ctx) =>
        ((IAgent)handler).OnTurnAsync(ctx.Object);

    [Fact]
    public async Task CreateWebhook_ReturnsSecretUrlAndId()
    {
        _webhook.Setup(s => s.CreateAsync("updown", "personal",
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new WebhookCreateResult("abc12345", "SECRETTOKEN99",
                new WebhookTokenEntity { Id = "abc12345" }));

        var ctx = Context("create-webhook");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a,
                "abc12345", "/api/v1/ingest/updown/SECRETTOKEN99")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateWebhook_UnsupportedSource_Rejected()
    {
        var ctx = Context("create-webhook slack");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "Only", "updown")),
            It.IsAny<CancellationToken>()), Times.Once);
        _webhook.Verify(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateWebhook_NoEntraId_Rejected()
    {
        var ctx = Context("create-webhook", aadObjectId: null);
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "valid Entra ID")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WebhookCommand_ServiceUnavailable_Reported()
    {
        var ctx = Context("create-webhook");
        await Run(NewHandler(withWebhookService: false), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "not available")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListWebhooks_Empty_ShowsHint()
    {
        _webhook.Setup(s => s.ListAsync()).ReturnsAsync(new List<WebhookTokenEntity>());

        var ctx = Context("list-webhooks");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "No webhooks configured")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListWebhooks_WithItems_SendsCardWithoutSecret()
    {
        _webhook.Setup(s => s.ListAsync()).ReturnsAsync(new List<WebhookTokenEntity>
        {
            new()
            {
                Id = "abc12345", RowKey = "hashvalue", Source = "updown", TargetType = "channel",
                Description = "prod site", UpdownAccount = "prod / ops@dsb.no", EnabledEvents = "",
                CreatedByName = "Tester", CreatedAt = DateTimeOffset.UtcNow
            }
        });

        var ctx = Context("list-webhooks");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Attachments != null &&
                ((Activity)a).Attachments!.Any(att => att.ContentType == "application/vnd.microsoft.card.adaptive")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_Events_Valid_UpdatesFilter()
    {
        var ctx = Context("configure-webhook abc12345 events check.down,check.up,check.ssl_expiration");
        await Run(NewHandler(), ctx);

        _webhook.Verify(s => s.ConfigureAsync("abc12345", null, null,
            It.Is<IReadOnlyList<string>>(l =>
                l.Contains("check.down") && l.Contains("check.up") && l.Contains("check.ssl_expiration"))),
            Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_Events_All_ExpandsToAll()
    {
        var ctx = Context("configure-webhook abc12345 events all");
        await Run(NewHandler(), ctx);

        _webhook.Verify(s => s.ConfigureAsync("abc12345", null, null,
            It.Is<IReadOnlyList<string>>(l => l.Count == UpdownEventTypes.All.Count)),
            Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_Events_Invalid_Rejected()
    {
        var ctx = Context("configure-webhook abc12345 events not-a-real-event");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "Invalid event type")),
            It.IsAny<CancellationToken>()), Times.Once);
        _webhook.Verify(s => s.ConfigureAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>()), Times.Never);
    }

    [Fact]
    public async Task ConfigureWebhook_Description_PreservesCasing()
    {
        var ctx = Context("configure-webhook abc12345 description Production Site Health");
        await Run(NewHandler(), ctx);

        _webhook.Verify(s => s.ConfigureAsync("abc12345", "Production Site Health", null, null), Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_Account_PreservesCasing()
    {
        var ctx = Context("configure-webhook abc12345 account Prod-Monitoring / Ops@dsb.no");
        await Run(NewHandler(), ctx);

        _webhook.Verify(s => s.ConfigureAsync("abc12345", null, "Prod-Monitoring / Ops@dsb.no", null), Times.Once);
    }

    [Fact]
    public async Task RemoveWebhook_Found_Confirms()
    {
        _webhook.Setup(s => s.RemoveByIdAsync("abc12345")).ReturnsAsync(true);

        var ctx = Context("remove-webhook abc12345");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "abc12345", "removed")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveWebhook_NotFound_Reports()
    {
        _webhook.Setup(s => s.RemoveByIdAsync("nope")).ReturnsAsync(false);

        var ctx = Context("remove-webhook nope");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "not found")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RotateWebhook_Found_ReturnsNewUrl()
    {
        _webhook.Setup(s => s.RotateByIdAsync("abc12345")).ReturnsAsync("NEWTOKEN42");

        var ctx = Context("rotate-webhook abc12345");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "/api/v1/ingest/updown/NEWTOKEN42", "no longer works")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RotateWebhook_NotFound_Reports()
    {
        _webhook.Setup(s => s.RotateByIdAsync("nope")).ReturnsAsync((string?)null);

        var ctx = Context("rotate-webhook nope");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "not found")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HelpWebhooks_ShowsWebhookHelp()
    {
        var ctx = Context("help webhooks");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "create-webhook", "rotate-webhook", "unverified sender")),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
