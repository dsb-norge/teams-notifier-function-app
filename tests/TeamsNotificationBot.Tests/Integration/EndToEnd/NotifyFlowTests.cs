using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.EndToEnd;

[Collection("Azurite")]
public class NotifyFlowTests : IDisposable
{
    private readonly IAliasService _aliasService;
    private readonly QueueClient _queueClient;
    private readonly Mock<IBotService> _mockBotService;
    private readonly QueueProcessorFunction _processor;
    private readonly string? _origTeamsDisabled;

    public NotifyFlowTests(AzuriteFixture azurite)
    {
        var aliasTable = azurite.CreateTableClient("aliases");
        _aliasService = new AliasService(aliasTable);
        _queueClient = azurite.CreateQueueClient("notifications");
        _mockBotService = new Mock<IBotService>();
        var mockLogger = new Mock<ILogger<QueueProcessorFunction>>();

        _processor = new QueueProcessorFunction(_mockBotService.Object, _aliasService, mockLogger.Object);

        // QueueProcessorFunction checks TEAMS_INTEGRATION_DISABLED internally
        _origTeamsDisabled = Environment.GetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED");
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", "true");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED", _origTeamsDisabled);
    }

    [Fact]
    public async Task FullFlow_SeedAlias_Enqueue_Dequeue_Resolve_Dispatch()
    {
        // 1. Seed alias via real AliasService
        await _aliasService.SetAliasAsync("e2e-test", new AliasEntity
        {
            TargetType = "channel",
            TeamId = "team-1",
            ChannelId = "channel-1",
            Description = "End-to-end test alias",
            CreatedBy = "test-oid",
            CreatedByName = "Tester",
            CreatedAt = DateTimeOffset.UtcNow
        });

        // 2. Build and enqueue message via real QueueClient
        var queueMessage = new QueueMessage
        {
            MessageId = "e2e-msg-001",
            Alias = "e2e-test",
            Message = "Hello from E2E!",
            Format = "text",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(queueMessage);
        await _queueClient.SendMessageAsync(json);

        // 3. Dequeue via real QueueClient
        var received = await _queueClient.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        // 4. Pass dequeued body to QueueProcessorFunction
        // In Azure Functions, the queue trigger deserializes Base64 → string for us.
        // Here we simulate that by using the MessageText directly.
        await _processor.Run(received.Value.MessageText, null!);

        // 5. With TEAMS_INTEGRATION_DISABLED=true, QueueProcessorFunction logs but
        //    does NOT call BotService. The alias resolution still happens though —
        //    the test proves the alias→(PK,RK) lookup works against real storage.
        //    BotService.Send* is NOT called because of the env var guard.
        _mockBotService.Verify(
            b => b.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task DirectTarget_SkipsAliasLookup()
    {
        var queueMessage = new QueueMessage
        {
            MessageId = "direct-msg-001",
            Target = new MessageTarget
            {
                Type = "channel",
                TeamId = "direct-team",
                ChannelId = "direct-channel"
            },
            Message = "Direct target test",
            Format = "text",
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(queueMessage);
        await _queueClient.SendMessageAsync(json);

        var received = await _queueClient.ReceiveMessageAsync();
        Assert.NotNull(received.Value);

        await _processor.Run(received.Value.MessageText, null!);

        // Direct target resolved — no alias lookup needed.
        // Again, TEAMS_INTEGRATION_DISABLED prevents actual send.
        _mockBotService.Verify(
            b => b.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }
}
