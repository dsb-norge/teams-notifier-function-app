using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Agents.Core.Models;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Services;

/// <summary>
/// Integration tests for BotService table storage operations.
///
/// BotService's constructor requires CloudAdapter (no parameterless constructor, can't mock it).
/// Most storage methods also check TEAMS_INTEGRATION_DISABLED and short-circuit.
/// So we test the table operations directly via TableClient, validating the entity schema
/// and serialization that BotService relies on. Methods that DON'T depend on CloudAdapter
/// (UpdateConversationReferenceAsync, QueryTeamReferencesAsync) are tested via BotService
/// where possible.
///
/// For local debugging: start Azurite via setup-local.sh, then run these tests from your IDE
/// with breakpoints — the AzuriteFixture detects the already-running Azurite and reuses it.
/// </summary>
[Collection("Azurite")]
public class BotServiceStorageTests
{
    private readonly TableClient _tableClient;

    public BotServiceStorageTests(AzuriteFixture azurite)
    {
        _tableClient = azurite.CreateTableClient("convrefs");
    }

    private static ConversationReferenceEntity MakeEntity(
        string partitionKey, string rowKey,
        string channelId = "19:test@thread.tacv2",
        string? teamName = null) =>
        new()
        {
            PartitionKey = partitionKey,
            RowKey = rowKey,
            ConversationReference = JsonSerializer.Serialize(new ConversationReference
            {
                ServiceUrl = "https://smba.trafficmanager.net/emea/",
                ChannelId = "msteams",
                Conversation = new ConversationAccount
                {
                    Id = channelId,
                    IsGroup = true,
                    ConversationType = "channel",
                    TenantId = "test-tenant"
                },
                Agent = new ChannelAccount { Id = "28:test-bot", Name = "TestBot" }
            }),
            ConversationType = "channel",
            TeamName = teamName,
            ChannelName = "General",
            InstalledAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task Store_And_ReadBack_AllFields()
    {
        var entity = MakeEntity("team-a", "channel-1", teamName: "Test Team");
        await _tableClient.UpsertEntityAsync(entity);

        var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>("team-a", "channel-1");
        Assert.Equal("team-a", response.Value.PartitionKey);
        Assert.Equal("channel-1", response.Value.RowKey);
        Assert.Equal("channel", response.Value.ConversationType);
        Assert.Equal("Test Team", response.Value.TeamName);
        Assert.Equal("General", response.Value.ChannelName);
        Assert.False(string.IsNullOrEmpty(response.Value.ConversationReference));
    }

    [Fact]
    public async Task Store_ConversationReferenceJson_Deserializable()
    {
        var entity = MakeEntity("team-rt", "channel-rt", channelId: "19:roundtrip@thread.tacv2");
        await _tableClient.UpsertEntityAsync(entity);

        var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>("team-rt", "channel-rt");
        var deserialized = JsonSerializer.Deserialize<ConversationReference>(
            response.Value.ConversationReference,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(deserialized);
        Assert.Equal("https://smba.trafficmanager.net/emea/", deserialized.ServiceUrl);
        Assert.Equal("msteams", deserialized.ChannelId);
        Assert.Equal("19:roundtrip@thread.tacv2", deserialized.Conversation?.Id);
        Assert.Equal("test-tenant", deserialized.Conversation?.TenantId);
    }

    [Fact]
    public async Task Update_Existing_ChangesJsonAndTimestamp()
    {
        var entity = MakeEntity("team-upd", "channel-upd", channelId: "19:original@thread.tacv2");
        await _tableClient.UpsertEntityAsync(entity);

        var beforeEntity = await _tableClient.GetEntityAsync<ConversationReferenceEntity>("team-upd", "channel-upd");
        var originalTimestamp = beforeEntity.Value.LastUpdated;

        await Task.Delay(50);

        // Simulate what BotService.UpdateConversationReferenceAsync does
        var existing = beforeEntity.Value;
        existing.ConversationReference = JsonSerializer.Serialize(new ConversationReference
        {
            ServiceUrl = "https://smba.trafficmanager.net/emea/",
            ChannelId = "msteams",
            Conversation = new ConversationAccount
            {
                Id = "19:updated@thread.tacv2",
                IsGroup = true,
                ConversationType = "channel",
                TenantId = "test-tenant"
            }
        });
        existing.LastUpdated = DateTimeOffset.UtcNow;
        await _tableClient.UpdateEntityAsync(existing, existing.ETag);

        var afterEntity = await _tableClient.GetEntityAsync<ConversationReferenceEntity>("team-upd", "channel-upd");
        Assert.True(afterEntity.Value.LastUpdated > originalTimestamp);
        Assert.Contains("19:updated@thread.tacv2", afterEntity.Value.ConversationReference);
    }

    [Fact]
    public async Task Update_NonExistent_ThrowsOnEmptyETag()
    {
        // A freshly-created entity has default ETag (empty), which UpdateEntityAsync rejects
        // before even hitting the server — this validates the SDK's pre-condition check.
        var entity = MakeEntity("no-team", "no-channel");
        await Assert.ThrowsAsync<ArgumentException>(
            () => _tableClient.UpdateEntityAsync(entity, entity.ETag));
    }

    [Fact]
    public async Task Update_NonExistent_WithWildcardETag_Returns404()
    {
        // With ETag.All, the request reaches the server but the entity doesn't exist
        var entity = MakeEntity("no-team-2", "no-channel-2");
        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => _tableClient.UpdateEntityAsync(entity, Azure.ETag.All));

        Assert.Equal(404, ex.Status);
    }

    [Fact]
    public async Task Remove_ExistingAndNonExistent()
    {
        var entity = MakeEntity("team-rm", "channel-rm");
        await _tableClient.UpsertEntityAsync(entity);

        await _tableClient.DeleteEntityAsync("team-rm", "channel-rm");

        // Verify it's gone
        await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => _tableClient.GetEntityAsync<ConversationReferenceEntity>("team-rm", "channel-rm"));

        // Deleting again with wildcard ETag doesn't throw
        await _tableClient.DeleteEntityAsync("team-rm", "channel-rm", Azure.ETag.All);
    }

    [Fact]
    public async Task QueryTeamReferences_FiltersCorrectly()
    {
        await _tableClient.UpsertEntityAsync(MakeEntity("team-filter-a", "channel-a1"));
        await _tableClient.UpsertEntityAsync(MakeEntity("team-filter-a", "channel-a2"));
        await _tableClient.UpsertEntityAsync(MakeEntity("team-filter-b", "channel-b1"));

        var results = new List<ConversationReferenceEntity>();
        await foreach (var e in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == "team-filter-a"))
        {
            results.Add(e);
        }

        Assert.Equal(2, results.Count);
        Assert.All(results, e => Assert.Equal("team-filter-a", e.PartitionKey));
    }

    [Fact]
    public async Task BatchRemove_DeletesAll()
    {
        for (int i = 0; i < 5; i++)
        {
            await _tableClient.UpsertEntityAsync(
                MakeEntity("team-batch-rm", $"channel-{i}", channelId: $"19:batch-rm-{i}@thread.tacv2"));
        }

        // Simulate BotService.BatchRemoveTeamReferencesAsync
        var actions = new List<TableTransactionAction>();
        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == "team-batch-rm", select: new[] { "PartitionKey", "RowKey" }))
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
        }
        await _tableClient.SubmitTransactionAsync(actions);

        var remaining = new List<ConversationReferenceEntity>();
        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == "team-batch-rm"))
        {
            remaining.Add(entity);
        }

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task ChannelNameBackfill_SetsName_LeavesReferenceAndInstalledAtUntouched()
    {
        // Mirrors BotService.TryUpdateChannelNameAsync against real storage. The byte-identical
        // ConversationReference assertion is what proves the backfill cannot break proactive
        // delivery (invariant 3).
        var seeded = MakeEntity("team-backfill", "channel-nameless", channelId: "19:nameless@thread.tacv2");
        seeded.ChannelName = null;
        await _tableClient.UpsertEntityAsync(seeded);

        var before = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(
            "team-backfill", "channel-nameless");
        var originalReference = before.Value.ConversationReference;
        var originalInstalledAt = before.Value.InstalledAt;

        var entity = before.Value;
        Assert.True(string.IsNullOrEmpty(entity.ChannelName));
        entity.ChannelName = "utvikling - testkanal";
        entity.LastUpdated = DateTimeOffset.UtcNow;
        await _tableClient.UpdateEntityAsync(entity, entity.ETag);

        var after = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(
            "team-backfill", "channel-nameless");
        Assert.Equal("utvikling - testkanal", after.Value.ChannelName);
        Assert.Equal(originalReference, after.Value.ConversationReference);
        Assert.Equal(originalInstalledAt, after.Value.InstalledAt);
    }

    [Fact]
    public async Task ChannelNameBackfill_StaleETag_Returns412()
    {
        // The optimistic-concurrency failure the retry loop is built around: a second writer
        // touched the row between our read and our write.
        var seeded = MakeEntity("team-backfill-412", "channel-412", channelId: "19:conflict@thread.tacv2");
        seeded.ChannelName = null;
        await _tableClient.UpsertEntityAsync(seeded);

        var first = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(
            "team-backfill-412", "channel-412");
        var staleEntity = first.Value;

        // Another writer commits first, invalidating the ETag we are holding.
        var second = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(
            "team-backfill-412", "channel-412");
        second.Value.ChannelName = "winner";
        await _tableClient.UpdateEntityAsync(second.Value, second.Value.ETag);

        staleEntity.ChannelName = "loser";
        var ex = await Assert.ThrowsAsync<Azure.RequestFailedException>(
            () => _tableClient.UpdateEntityAsync(staleEntity, staleEntity.ETag));

        Assert.Equal(412, ex.Status);

        // The winner's value survives; a re-read is what lets the retry loop bail out.
        var after = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(
            "team-backfill-412", "channel-412");
        Assert.Equal("winner", after.Value.ChannelName);
    }

    [Fact]
    public async Task BatchUpdateTeamName_UpdatesAll()
    {
        for (int i = 0; i < 3; i++)
        {
            await _tableClient.UpsertEntityAsync(
                MakeEntity("team-batch-name", $"channel-{i}", teamName: "OldName"));
        }

        // Simulate BotService.BatchUpdateTeamNameAsync
        var actions = new List<TableTransactionAction>();
        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == "team-batch-name"))
        {
            entity.TeamName = "NewName";
            entity.LastUpdated = DateTimeOffset.UtcNow;
            actions.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, entity));
        }
        await _tableClient.SubmitTransactionAsync(actions);

        var results = new List<ConversationReferenceEntity>();
        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == "team-batch-name"))
        {
            results.Add(entity);
        }

        Assert.Equal(3, results.Count);
        Assert.All(results, e => Assert.Equal("NewName", e.TeamName));
    }
}
