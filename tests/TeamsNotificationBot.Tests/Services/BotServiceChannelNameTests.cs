using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

/// <summary>
/// Unit tests for BotService.TryUpdateChannelNameAsync.
///
/// BotService's constructor takes a CloudAdapter that cannot be mocked (no parameterless
/// constructor). TryUpdateChannelNameAsync never touches the adapter — it is a pure table
/// operation — so the adapter is passed as null here. If that ever stops being true, these
/// tests fail loudly with a NullReferenceException rather than silently testing the wrong thing.
/// </summary>
// Joins the "Azurite" collection ONLY to serialize with the other classes that mutate the
// process-global TEAMS_INTEGRATION_DISABLED env var (NotifyFlowTests, UpdownIngestFlowTests,
// BotServiceStorageTests). BotService reads it in its CONSTRUCTOR, so a class constructing
// BotService in parallel with one of those tests intermittently captures the wrong value.
[Collection("Azurite")]
public class BotServiceChannelNameTests
{
    private readonly Mock<TableClient> _tableClient = new();
    private readonly BotService _service;

    public BotServiceChannelNameTests()
    {
        _service = new BotService(null!, _tableClient.Object, NullLogger<BotService>.Instance);
    }

    // Fixed timestamps so "was it preserved / was it bumped" assertions are exact.
    private static readonly DateTimeOffset Installed = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);
    private const string ReferenceJson = """{"Conversation":{"Id":"19:channel@thread.tacv2"}}""";

    private static ConversationReferenceEntity Entity(string? channelName) => new()
    {
        PartitionKey = "team-1",
        RowKey = "19:channel@thread.tacv2",
        ConversationReference = ReferenceJson,
        ConversationType = "channel",
        TeamName = "Test Team",
        ChannelName = channelName,
        InstalledAt = Installed,
        LastUpdated = Installed,
        ETag = new ETag("W/\"datetime'2026-08-14'\"")
    };

    /// <summary>
    /// Returns a FRESH entity per call, mirroring the real TableClient (which deserializes a new
    /// instance from each HTTP response). Handing back one shared mutable instance would let the
    /// retry loop observe its own uncommitted mutation on re-read.
    /// </summary>
    private void SetupGet(Func<int, ConversationReferenceEntity> entityForAttempt)
    {
        var reads = 0;
        _tableClient.Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(
                "team-1", "19:channel@thread.tacv2",
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(
                Response.FromValue(entityForAttempt(++reads), Mock.Of<Response>())));
    }

    private void SetupGet(string? channelName) => SetupGet(_ => Entity(channelName));

    private void SetupUpdate(Func<int, Task<Response>> behaviour)
    {
        var attempt = 0;
        _tableClient.Setup(t => t.UpdateEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
                It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns(() => behaviour(++attempt));
    }

    [Fact]
    public async Task NameEmpty_SetsNameAndBumpsLastUpdated()
    {
        SetupGet((string?)null);
        ConversationReferenceEntity? written = null;
        _tableClient.Setup(t => t.UpdateEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
                It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationReferenceEntity, ETag, TableUpdateMode, CancellationToken>(
                (e, _, _, _) => written = e)
            .ReturnsAsync(Mock.Of<Response>());

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.True(result);
        Assert.NotNull(written);
        Assert.Equal("utvikling", written.ChannelName);
        // Invariant 3: the reference blob and install timestamp are never touched.
        Assert.Equal(ReferenceJson, written.ConversationReference);
        Assert.Equal(Installed, written.InstalledAt);
        Assert.True(written.LastUpdated > Installed);
    }

    [Fact]
    public async Task NameAlreadySet_ReturnsFalseAndWritesNothing()
    {
        SetupGet("existing-name");

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "from-api");

        Assert.False(result);
        _tableClient.Verify(t => t.UpdateEntityAsync(
            It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task EmptyChannelName_ReturnsFalseWithoutReadingTheRow()
    {
        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "");

        Assert.False(result);
        _tableClient.Verify(t => t.GetEntityAsync<ConversationReferenceEntity>(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Conflict_RetriesAndSucceeds()
    {
        SetupGet((string?)null);
        SetupUpdate(attempt => attempt == 1
            ? throw new RequestFailedException(412, "Precondition Failed")
            : Task.FromResult(Mock.Of<Response>()));

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.True(result);
        _tableClient.Verify(t => t.UpdateEntityAsync(
            It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Conflict_GivesUpAfterThreeAttempts()
    {
        SetupGet((string?)null);
        SetupUpdate(_ => throw new RequestFailedException(412, "Precondition Failed"));

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.False(result);
        _tableClient.Verify(t => t.UpdateEntityAsync(
            It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task Conflict_LosesRaceToAnotherWriter_StopsInsteadOfOverwriting()
    {
        // 412 on the first write, and the re-read shows the winner's name. The retry must
        // observe that and bail — this is what makes concurrent runs converge rather than fight.
        SetupGet(read => Entity(read == 1 ? null : "winner-name"));
        SetupUpdate(_ => throw new RequestFailedException(412, "Precondition Failed"));

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.False(result);
        _tableClient.Verify(t => t.UpdateEntityAsync(
            It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RowMissing_SwallowsAndReturnsFalse()
    {
        _tableClient.Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.False(result);
    }

    [Fact]
    public async Task ServerError_SwallowsAndReturnsFalse()
    {
        SetupGet((string?)null);
        SetupUpdate(_ => throw new RequestFailedException(500, "Server Error"));

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.False(result);
    }

    [Fact]
    public async Task UnexpectedException_SwallowsAndReturnsFalse()
    {
        // Pins the "never throws" contract the call sites rely on (they add no try/catch).
        _tableClient.Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.False(result);
    }
}

/// <summary>
/// Separate class because TEAMS_INTEGRATION_DISABLED is read in BotService's constructor, so it
/// must be set before construction. Follows the same set-in-ctor / restore-in-Dispose convention
/// as QueueProcessorFunctionTests and PoisonQueueMonitorFunctionTests.
/// </summary>
// Joins the "Azurite" collection ONLY to serialize with the other classes that mutate the
// process-global TEAMS_INTEGRATION_DISABLED env var (NotifyFlowTests, UpdownIngestFlowTests,
// BotServiceStorageTests). BotService reads it in its CONSTRUCTOR, so a class constructing
// BotService in parallel with one of those tests intermittently captures the wrong value.
[Collection("Azurite")]
public class BotServiceChannelNameTeamsDisabledTests : IDisposable
{
    private const string EnvVar = "TEAMS_INTEGRATION_DISABLED";
    private readonly string? _previous = Environment.GetEnvironmentVariable(EnvVar);
    private readonly Mock<TableClient> _tableClient = new();
    private readonly BotService _service;

    public BotServiceChannelNameTeamsDisabledTests()
    {
        Environment.SetEnvironmentVariable(EnvVar, "true");
        _service = new BotService(null!, _tableClient.Object, NullLogger<BotService>.Instance);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(EnvVar, _previous);

    [Fact]
    public async Task TeamsDisabled_ReturnsFalseWithoutTouchingTheTable()
    {
        var result = await _service.TryUpdateChannelNameAsync("team-1", "19:channel@thread.tacv2", "utvikling");

        Assert.False(result);
        _tableClient.Verify(t => t.GetEntityAsync<ConversationReferenceEntity>(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
