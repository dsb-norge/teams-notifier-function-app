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
    private readonly Mock<IUpdownIpAllowlistService> _ipAllowlist = new();

    private TeamsBotHandler NewHandler(bool withWebhookService = true)
    {
        _webhook.Setup(s => s.ConfigureAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(true);

        return new TeamsBotHandler(
            _botService.Object, _aliasService.Object, _teamLookupTable.Object, _botOpsQueue.Object,
            NullLogger<TeamsBotHandler>.Instance,
            queueService: null,
            webhookService: withWebhookService ? _webhook.Object : null,
            ipAllowlistService: withWebhookService ? _ipAllowlist.Object : null);
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

    // configure-webhook now reads the entity up front (F6 before/after), so it must exist.
    private void SetupExisting(
        string id = "abc12345", string desc = "old desc", string account = "old@acct", string events = "check.down")
        => _webhook.Setup(s => s.GetByIdAsync(id)).ReturnsAsync(new WebhookTokenEntity
        {
            Id = id, RowKey = "hash", Source = "updown", TargetType = "personal",
            Description = desc, UpdownAccount = account, EnabledEvents = events
        });

    [Fact]
    public async Task CreateWebhook_RequiresAccountAndDescription_PassesThemThrough()
    {
        _webhook.Setup(s => s.CreateAsync("updown", "personal",
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                "Prod uptime + SSL", "ops@dsb.no",
                It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new WebhookCreateResult("abc12345", "SECRETTOKEN99",
                new WebhookTokenEntity { Id = "abc12345" }));

        var ctx = Context("create-webhook account ops@dsb.no description Prod uptime + SSL");
        await Run(NewHandler(), ctx);

        // F3: account + description are captured at creation and forwarded to the service
        // (description, then updownAccount — note the parameter order).
        _webhook.Verify(s => s.CreateAsync("updown", "personal",
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            "Prod uptime + SSL", "ops@dsb.no",
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        // ...and the confirmation surfaces the id, the one-time secret URL, and the labels.
        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a,
                "abc12345", "/api/v1/ingest/updown/SECRETTOKEN99", "ops@dsb.no", "Prod uptime + SSL")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateWebhook_ExplicitUpdownSource_Works()
    {
        _webhook.Setup(s => s.CreateAsync("updown", "personal",
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new WebhookCreateResult("def67890", "TOKzz",
                new WebhookTokenEntity { Id = "def67890" }));

        var ctx = Context("create-webhook updown account a@b.no description hello world");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "def67890", "/api/v1/ingest/updown/TOKzz")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateWebhook_MissingDescription_Rejected()
    {
        var ctx = Context("create-webhook account ops@dsb.no");   // no description
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "required")),
            It.IsAny<CancellationToken>()), Times.Once);
        _webhook.Verify(s => s.CreateAsync(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
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
    public async Task Help_PerCommand_ReturnsDetailedHelp()
    {
        var ctx = Context("help configure-webhook");
        await Run(NewHandler(), ctx);

        // F5: `help <command>` resolves per-command help (fields + full event list).
        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "configure-webhook", "check.performance_drop", "before")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Help_UnknownTopic_HintsPerCommandHelp()
    {
        var ctx = Context("help totally-bogus");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "Unknown help topic", "help <command>")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShowWebhook_Found_SendsSingleCard()
    {
        _webhook.Setup(s => s.GetByIdAsync("abc12345")).ReturnsAsync(new WebhookTokenEntity
        {
            Id = "abc12345", RowKey = "hash", Source = "updown", TargetType = "personal",
            Description = "prod site", UpdownAccount = "ops@dsb.no", EnabledEvents = "",
            CreatedByName = "Tester", CreatedAt = DateTimeOffset.UtcNow
        });

        var ctx = Context("show-webhook abc12345");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => ((Activity)a).Attachments != null &&
                ((Activity)a).Attachments!.Any(att => att.ContentType == "application/vnd.microsoft.card.adaptive")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShowWebhook_NotFound_Reported()
    {
        _webhook.Setup(s => s.GetByIdAsync("missing1")).ReturnsAsync((WebhookTokenEntity?)null);

        var ctx = Context("show-webhook missing1");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "missing1", "not found")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShowWebhook_NoId_ShowsUsage()
    {
        var ctx = Context("show-webhook");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "Usage", "show-webhook")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_Events_Valid_UpdatesFilter()
    {
        SetupExisting();
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
        SetupExisting();
        var ctx = Context("configure-webhook abc12345 events all");
        await Run(NewHandler(), ctx);

        _webhook.Verify(s => s.ConfigureAsync("abc12345", null, null,
            It.Is<IReadOnlyList<string>>(l => l.Count == UpdownEventTypes.All.Count)),
            Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_Events_Invalid_Rejected()
    {
        SetupExisting();
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
        SetupExisting();
        var ctx = Context("configure-webhook abc12345 description Production Site Health");
        await Run(NewHandler(), ctx);

        _webhook.Verify(s => s.ConfigureAsync("abc12345", "Production Site Health", null, null), Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_Account_PreservesCasing()
    {
        SetupExisting();
        var ctx = Context("configure-webhook abc12345 account Prod-Monitoring / Ops@dsb.no");
        await Run(NewHandler(), ctx);

        _webhook.Verify(s => s.ConfigureAsync("abc12345", null, "Prod-Monitoring / Ops@dsb.no", null), Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_ShowsBeforeAndAfter()
    {
        SetupExisting(desc: "old desc");
        var ctx = Context("configure-webhook abc12345 description New shiny description");
        await Run(NewHandler(), ctx);

        // F6: confirmation shows both the previous and the new value.
        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "before", "old desc", "after", "New shiny description")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_NoChange_ReportedAsUnchanged()
    {
        SetupExisting(account: "ops@dsb.no");
        var ctx = Context("configure-webhook abc12345 account ops@dsb.no");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "unchanged", "ops@dsb.no")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfigureWebhook_NotFound_Reported()
    {
        // No SetupExisting → GetByIdAsync returns null.
        var ctx = Context("configure-webhook missing1 description whatever");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "missing1", "not found")),
            It.IsAny<CancellationToken>()), Times.Once);
        _webhook.Verify(s => s.ConfigureAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>()), Times.Never);
    }

    [Fact]
    public async Task ConfigureWebhook_ConcurrentDelete_ReportsNotFound()
    {
        // Exists at the pre-read, but ConfigureAsync reports not-found (deleted in between) — the
        // handler must not send a misleading "updated" confirmation.
        SetupExisting();
        var handler = NewHandler();
        // Override NewHandler's default ConfigureAsync→true (registered last so it wins).
        _webhook.Setup(s => s.ConfigureAsync("abc12345", It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyList<string>?>())).ReturnsAsync(false);

        var ctx = Context("configure-webhook abc12345 description whatever");
        await Run(handler, ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "abc12345", "not found")),
            It.IsAny<CancellationToken>()), Times.Once);
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

    // --- IP allowlist commands (design §17) ---

    [Fact]
    public async Task ShowIpAllowList_RendersModeAndEntries()
    {
        _ipAllowlist.Setup(s => s.GetAsync()).ReturnsAsync(new UpdownIpAllowlistEntity
        {
            Cidrs = "1.2.3.4,10.0.0.0/8",
            RefreshedAt = DateTimeOffset.UtcNow,
            RefreshedBy = "tester"
        });

        var ctx = Context("show-ip-allow-list updown");
        await Run(NewHandler(), ctx);

        // Mode display must reflect the single source of truth (UpdownWebhookConfig.IpFilterMode,
        // secure default "enforce") — guards against a duplicate read drifting from actual enforcement.
        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "allowlist", "10.0.0.0/8",
                $"Mode: **{TeamsNotificationBot.Helpers.UpdownWebhookConfig.IpFilterMode}**")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShowIpAllowList_Empty_HintsToUpdate()
    {
        _ipAllowlist.Setup(s => s.GetAsync()).ReturnsAsync((UpdownIpAllowlistEntity?)null);

        var ctx = Context("show-ip-allow-list updown");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "update-ip-allow-list")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateIpAllowList_ReportsRefreshResult()
    {
        _ipAllowlist.Setup(s => s.RefreshAsync(It.IsAny<string>()))
            .ReturnsAsync(new AllowlistRefreshResult(
                Added: ["3.3.3.3"], Removed: [], Current: ["1.1.1.1", "3.3.3.3"], Error: null));

        var ctx = Context("update-ip-allow-list updown");
        await Run(NewHandler(), ctx);

        _ipAllowlist.Verify(s => s.RefreshAsync(It.IsAny<string>()), Times.Once);
        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "refreshed", "2")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateIpAllowList_Error_Reported()
    {
        _ipAllowlist.Setup(s => s.RefreshAsync(It.IsAny<string>()))
            .ReturnsAsync(new AllowlistRefreshResult([], [], ["1.1.1.1"], "dns down"));

        var ctx = Context("update-ip-allow-list updown");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "failed", "dns down")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IpAllowListCommand_NoEntraId_Rejected()
    {
        var ctx = Context("show-ip-allow-list updown", aadObjectId: null);
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "valid Entra ID")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ShowIpAllowList_UnknownSource_ShowsUsage()
    {
        var ctx = Context("show-ip-allow-list foo");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "Only", "updown", "show-ip-allow-list")),
            It.IsAny<CancellationToken>()), Times.Once);
        _ipAllowlist.Verify(s => s.GetAsync(), Times.Never);
    }

    [Fact]
    public async Task UpdateIpAllowList_UnknownSource_ShowsUsage_AndDoesNotRefresh()
    {
        var ctx = Context("update-ip-allow-list bar");
        await Run(NewHandler(), ctx);

        ctx.Verify(t => t.SendActivityAsync(
            It.Is<IActivity>(a => TextContains(a, "Only", "updown", "update-ip-allow-list")),
            It.IsAny<CancellationToken>()), Times.Once);
        _ipAllowlist.Verify(s => s.RefreshAsync(It.IsAny<string>()), Times.Never);
    }
}
