using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.EndToEnd;

[Collection("Azurite")]
public class UpdownIngestFlowTests
{
    private readonly AzuriteFixture _azurite;
    private readonly WebhookService _webhookService;
    private readonly IdempotencyService _idempotency;

    public UpdownIngestFlowTests(AzuriteFixture azurite)
    {
        _azurite = azurite;
        _webhookService = new WebhookService(azurite.CreateTableClient("webhooktokens"));
        _idempotency = new IdempotencyService(azurite.CreateTableClient("idempotencykeys"));
    }

    private (UpdownIngestFunction fn, QueueClient queue) NewFunction(string queueName)
    {
        var queue = _azurite.CreateQueueClient(queueName);
        // Empty-resolver allowlist service → empty list → fail-safe allow (default log-only mode),
        // so these flow tests exercise enqueue/dedupe/target without the IP filter interfering.
        var ipAllowlist = new UpdownIpAllowlistService(
            _azurite.CreateTableClient("updownipallowlist"),
            (_, _) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()));
        var fn = new UpdownIngestFunction(_webhookService, queue, _idempotency, ipAllowlist,
            NullLogger<UpdownIngestFunction>.Instance);
        return (fn, queue);
    }

    [Fact]
    public async Task FullFlow_CreateWebhook_Post_Enqueues_ResolvableTarget()
    {
        var (fn, queue) = NewFunction("updown-flow-full");
        var created = await _webhookService.CreateAsync(
            "updown", "channel", "team-1", "channel-1", null, null, "flow test", "ops@dsb.no", "oid", "Tester");
        await _webhookService.ConfigureAsync(created.Id, "prod site", "prod / ops@dsb.no", null);

        var req = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);
        var result = await fn.Run(req, created.Token);

        Assert.IsType<OkObjectResult>(result);

        var received = await queue.ReceiveMessageAsync();
        Assert.NotNull(received.Value);
        var msg = JsonSerializer.Deserialize<QueueMessage>(received.Value.MessageText);

        Assert.NotNull(msg);
        Assert.Equal("adaptive-card", msg.Format);
        Assert.NotNull(msg.Target);
        Assert.Equal("channel", msg.Target!.Type);
        Assert.Equal("team-1", msg.Target.TeamId);
        Assert.Equal("channel-1", msg.Target.ChannelId);
        Assert.Contains("DOWN", msg.Message);
        Assert.Contains("prod / ops@dsb.no", msg.Message); // account label surfaced

        // LastReceivedAt was bumped
        var after = await _webhookService.GetByIdAsync(created.Id);
        Assert.NotNull(after?.LastReceivedAt);
    }

    [Fact]
    public async Task Retry_SamePayloadTwice_EnqueuesOnce()
    {
        var (fn, queue) = NewFunction("updown-flow-dedupe");
        var created = await _webhookService.CreateAsync(
            "updown", "channel", "team-2", "channel-2", null, null, "flow test", "ops@dsb.no", "oid", "Tester");

        var req1 = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);
        var req2 = HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown);

        await fn.Run(req1, created.Token);
        await fn.Run(req2, created.Token);

        var peeked = await queue.PeekMessagesAsync(maxMessages: 32);
        Assert.Single(peeked.Value);
    }

    [Fact]
    public async Task DirectTargetMessage_ResolvesInQueueProcessor()
    {
        var (fn, queue) = NewFunction("updown-flow-processor");
        var created = await _webhookService.CreateAsync(
            "updown", "channel", "team-3", "channel-3", null, null, "flow test", "ops@dsb.no", "oid", "Tester");

        await fn.Run(HttpRequestHelper.CreatePostRequest(body: UpdownPayloads.CheckDown), created.Token);

        var received = await queue.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        // Feed the enqueued message to the real QueueProcessor with Teams disabled.
        // Proves the direct-target path resolves without touching alias storage.
        var prev = Environment.GetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED");
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", "true");
        try
        {
            var aliasTable = _azurite.CreateTableClient("aliases");
            var processor = new QueueProcessorFunction(
                new Moq.Mock<IBotService>().Object,
                new AliasService(aliasTable),
                NullLogger<QueueProcessorFunction>.Instance);

            var ex = await Record.ExceptionAsync(() =>
                processor.Run(received.Value.MessageText, null!));
            Assert.Null(ex); // direct target resolved cleanly; send skipped by the disable flag
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", prev);
        }
    }
}
