using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

/// <summary>
/// Unit tests for BotService.UpsertChannelReferenceAsync, the channel-event writer.
///
/// Like BotServiceChannelNameTests, the CloudAdapter is passed as null: this method is a pure
/// table operation and never touches it.
/// </summary>
// Joins the "Azurite" collection ONLY to serialize with the other classes that mutate the
// process-global TEAMS_INTEGRATION_DISABLED env var (see BotServiceChannelNameTests).
[Collection("Azurite")]
public class BotServiceChannelReferenceUpsertTests
{
    private const string TeamGuid = "team-1";
    private const string ChannelId = "19:channel@thread.tacv2";

    private readonly Mock<TableClient> _tableClient = new();
    private readonly BotService _service;

    public BotServiceChannelReferenceUpsertTests()
    {
        _service = new BotService(null!, _tableClient.Object, NullLogger<BotService>.Instance, null!, null!);
    }

    private static readonly DateTimeOffset Installed = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static ConversationReference NewReference() => new()
    {
        ServiceUrl = "https://smba.trafficmanager.net/emea/",
        Conversation = new ConversationAccount { Id = ChannelId, ConversationType = "channel", IsGroup = true }
    };

    private static ConversationReferenceEntity Stored() => new()
    {
        PartitionKey = TeamGuid,
        RowKey = ChannelId,
        ConversationReference = """{"Conversation":{"Id":"old"}}""",
        ConversationType = "channel",
        TeamName = "Stored Team",
        ChannelName = "stored-channel",
        InstalledAt = Installed,
        LastUpdated = Installed,
        ETag = new ETag("W/\"datetime'2026-08-14'\"")
    };

    private void SetupGet(Func<ConversationReferenceEntity> entity) =>
        _tableClient.Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(
                TeamGuid, ChannelId, It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Response.FromValue(entity(), Mock.Of<Response>())));

    private void VerifyUpdates(Times times) =>
        _tableClient.Verify(t => t.UpdateEntityAsync(
            It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), times);

    [Fact]
    public async Task RowMissing_StoresNewRow()
    {
        _tableClient.Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(
                TeamGuid, ChannelId, It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));
        ConversationReferenceEntity? written = null;
        _tableClient.Setup(t => t.AddEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationReferenceEntity, CancellationToken>((e, _) => written = e)
            .ReturnsAsync(Mock.Of<Response>());

        await _service.UpsertChannelReferenceAsync(NewReference(), TeamGuid, ChannelId, "Team", "channel-a");

        Assert.NotNull(written);
        Assert.Equal("channel", written.ConversationType);
        Assert.Equal("Team", written.TeamName);
        Assert.Equal("channel-a", written.ChannelName);
        VerifyUpdates(Times.Never());
        // Insert-only: an upsert here could overwrite a row a concurrent writer just created.
        _tableClient.Verify(t => t.UpsertEntityAsync(
            It.IsAny<ConversationReferenceEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RowCreatedConcurrently_FallsBackToInPlaceUpdate()
    {
        // GET says missing, but another writer inserts before we do: the 409 sends us back
        // round the loop, where the re-read finds the row and updates it in place.
        var reads = 0;
        _tableClient.Setup(t => t.GetEntityAsync<ConversationReferenceEntity>(
                TeamGuid, ChannelId, It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++reads == 1
                ? throw new RequestFailedException(404, "Not Found")
                : Task.FromResult(Response.FromValue(Stored(), Mock.Of<Response>())));
        _tableClient.Setup(t => t.AddEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(409, "Conflict"));
        ConversationReferenceEntity? written = null;
        _tableClient.Setup(t => t.UpdateEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
                It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationReferenceEntity, ETag, TableUpdateMode, CancellationToken>(
                (e, _, _, _) => written = e)
            .ReturnsAsync(Mock.Of<Response>());

        await _service.UpsertChannelReferenceAsync(NewReference(), TeamGuid, ChannelId, null, "channel-a");

        Assert.NotNull(written);
        Assert.Equal(Installed, written.InstalledAt);
        Assert.Equal("Stored Team", written.TeamName);
        Assert.Equal("channel-a", written.ChannelName);
    }

    [Fact]
    public async Task RowExists_UpdatesInPlaceAndKeepsInstalledAt()
    {
        SetupGet(Stored);
        ConversationReferenceEntity? written = null;
        _tableClient.Setup(t => t.UpdateEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
                It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationReferenceEntity, ETag, TableUpdateMode, CancellationToken>(
                (e, _, _, _) => written = e)
            .ReturnsAsync(Mock.Of<Response>());
        var reference = NewReference();

        await _service.UpsertChannelReferenceAsync(reference, TeamGuid, ChannelId, "Renamed Team", "renamed-channel");

        Assert.NotNull(written);
        Assert.Equal(JsonSerializer.Serialize(reference), written.ConversationReference);
        Assert.Equal("Renamed Team", written.TeamName);
        Assert.Equal("renamed-channel", written.ChannelName);
        Assert.Equal(Installed, written.InstalledAt);
        Assert.True(written.LastUpdated > Installed);
        _tableClient.Verify(t => t.UpsertEntityAsync(
            It.IsAny<ConversationReferenceEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task RowExists_EventLacksNames_KeepsStoredNames(string? absent)
    {
        SetupGet(Stored);
        ConversationReferenceEntity? written = null;
        _tableClient.Setup(t => t.UpdateEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
                It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Callback<ConversationReferenceEntity, ETag, TableUpdateMode, CancellationToken>(
                (e, _, _, _) => written = e)
            .ReturnsAsync(Mock.Of<Response>());

        await _service.UpsertChannelReferenceAsync(NewReference(), TeamGuid, ChannelId, absent, absent);

        Assert.NotNull(written);
        Assert.Equal("Stored Team", written.TeamName);
        Assert.Equal("stored-channel", written.ChannelName);
    }

    [Fact]
    public async Task Conflict_RetriesAndSucceeds()
    {
        SetupGet(Stored);
        var attempt = 0;
        _tableClient.Setup(t => t.UpdateEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
                It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++attempt == 1
                ? throw new RequestFailedException(412, "Precondition Failed")
                : Task.FromResult(Mock.Of<Response>()));

        await _service.UpsertChannelReferenceAsync(NewReference(), TeamGuid, ChannelId, "Team", "channel-a");

        VerifyUpdates(Times.Exactly(2));
    }

    [Fact]
    public async Task Conflict_ThreeTimes_Propagates()
    {
        // The event's primary write, unlike the best-effort backfills: a persistent conflict
        // must surface rather than silently drop the channel's reference.
        SetupGet(Stored);
        _tableClient.Setup(t => t.UpdateEntityAsync(
                It.IsAny<ConversationReferenceEntity>(), It.IsAny<ETag>(),
                It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(412, "Precondition Failed"));

        var ex = await Assert.ThrowsAsync<RequestFailedException>(() =>
            _service.UpsertChannelReferenceAsync(NewReference(), TeamGuid, ChannelId, "Team", "channel-a"));

        Assert.Equal(412, ex.Status);
        VerifyUpdates(Times.Exactly(3));
    }
}
