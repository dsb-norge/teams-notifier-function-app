using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class BotOperationsFunctionTests
{
    private readonly Mock<IBotService> _botService = new();
    private readonly Mock<FunctionContext> _functionContext = new();
    private readonly BotOperationsFunction _function;

    public BotOperationsFunctionTests()
    {
        _function = new BotOperationsFunction(
            _botService.Object,
            NullLogger<BotOperationsFunction>.Instance);
    }

    [Fact]
    public async Task EnumerateChannels_ValidMessage_CallsBotService()
    {
        var message = new BotOperationMessage
        {
            Operation = "enumerate_channels",
            TeamGuid = "team-guid-1",
            TeamName = "Test Team",
            TeamThreadId = "19:thread@thread.tacv2",
            SerializedReference = """{"serviceUrl":"https://smba.trafficmanager.net/emea/"}""",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(message);

        await _function.Run(json, _functionContext.Object);

        _botService.Verify(b => b.EnumerateAndStoreTeamChannelsAsync(
            """{"serviceUrl":"https://smba.trafficmanager.net/emea/"}""",
            "team-guid-1", "Test Team", "19:thread@thread.tacv2"), Times.Once);
    }

    [Fact]
    public async Task EnumerateChannels_MissingSerializedReference_DoesNotCallBotService()
    {
        var message = new BotOperationMessage
        {
            Operation = "enumerate_channels",
            TeamGuid = "team-guid-1",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(message);

        await _function.Run(json, _functionContext.Object);

        _botService.Verify(b => b.EnumerateAndStoreTeamChannelsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task RemoveTeamRefs_CallsBatchRemove()
    {
        var message = new BotOperationMessage
        {
            Operation = "remove_team_refs",
            TeamGuid = "team-guid-1",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(message);

        await _function.Run(json, _functionContext.Object);

        _botService.Verify(b => b.BatchRemoveTeamReferencesAsync("team-guid-1"), Times.Once);
    }

    [Fact]
    public async Task RenameTeam_CallsBatchUpdateTeamName()
    {
        var message = new BotOperationMessage
        {
            Operation = "rename_team",
            TeamGuid = "team-guid-1",
            TeamName = "New Name",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(message);

        await _function.Run(json, _functionContext.Object);

        _botService.Verify(b => b.BatchUpdateTeamNameAsync("team-guid-1", "New Name"), Times.Once);
    }

    [Fact]
    public async Task UnknownOperation_DoesNotCallBotService()
    {
        var message = new BotOperationMessage
        {
            Operation = "unknown_op",
            TeamGuid = "team-guid-1",
            EnqueuedAt = DateTimeOffset.UtcNow
        };
        var json = JsonSerializer.Serialize(message);

        await _function.Run(json, _functionContext.Object);

        _botService.Verify(b => b.EnumerateAndStoreTeamChannelsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _botService.Verify(b => b.BatchRemoveTeamReferencesAsync(It.IsAny<string>()), Times.Never);
        _botService.Verify(b => b.BatchUpdateTeamNameAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task InvalidJson_DoesNotCallBotService()
    {
        await _function.Run("not-json{{{", _functionContext.Object);

        _botService.Verify(b => b.EnumerateAndStoreTeamChannelsAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        _botService.Verify(b => b.BatchRemoveTeamReferencesAsync(It.IsAny<string>()), Times.Never);
        _botService.Verify(b => b.BatchUpdateTeamNameAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
    }
}
