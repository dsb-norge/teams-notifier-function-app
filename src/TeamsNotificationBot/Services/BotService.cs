using System.Text.Json;
using Azure.Data.Tables;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Extensions.Teams.Connector;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class BotService : IBotService
{
    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CloudAdapter _adapter;
    private readonly TableClient _tableClient;
    private readonly string _botAppId;
    private readonly bool _teamsDisabled;
    private readonly ILogger<BotService> _logger;

    public BotService(
        CloudAdapter adapter,
        TableClient tableClient,
        ILogger<BotService> logger)
    {
        _adapter = adapter;
        _tableClient = tableClient;
        _botAppId = Environment.GetEnvironmentVariable("BotAppId") ?? string.Empty;
        _teamsDisabled = string.Equals(
            Environment.GetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        _logger = logger;
    }

    public async Task SendMessageAsync(string partitionKey, string rowKey, string message)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would send text to {PK}/{RK}: {Message}",
                partitionKey, rowKey, message);
            return;
        }

        var reference = await GetConversationReferenceAsync(partitionKey, rowKey);
        if (reference == null)
        {
            _logger.LogError("No conversation reference found for {PK}/{RK}", partitionKey, rowKey);
            throw new InvalidOperationException(
                $"No conversation reference found for '{partitionKey}'/'{rowKey}'. Ensure the bot is installed.");
        }

        await _adapter.ContinueConversationAsync(
            _botAppId,
            reference,
            async (turnContext, ct) =>
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(message), ct);
            },
            CancellationToken.None);

        await UpdateLastUpdatedAsync(partitionKey, rowKey);
        _logger.LogInformation("Sent text message to {PK}/{RK}", partitionKey, rowKey);
    }

    public async Task SendAdaptiveCardAsync(string partitionKey, string rowKey, JsonElement card)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would send adaptive card to {PK}/{RK}",
                partitionKey, rowKey);
            return;
        }

        var reference = await GetConversationReferenceAsync(partitionKey, rowKey);
        if (reference == null)
        {
            _logger.LogError("No conversation reference found for {PK}/{RK}", partitionKey, rowKey);
            throw new InvalidOperationException(
                $"No conversation reference found for '{partitionKey}'/'{rowKey}'. Ensure the bot is installed.");
        }

        await _adapter.ContinueConversationAsync(
            _botAppId,
            reference,
            async (turnContext, ct) =>
            {
                var attachment = new Attachment
                {
                    ContentType = "application/vnd.microsoft.card.adaptive",
                    Content = JsonSerializer.Deserialize<object>(card.GetRawText())
                };
                var activity = MessageFactory.Attachment(attachment);
                await turnContext.SendActivityAsync(activity, ct);
            },
            CancellationToken.None);

        await UpdateLastUpdatedAsync(partitionKey, rowKey);
        _logger.LogInformation("Sent adaptive card to {PK}/{RK}", partitionKey, rowKey);
    }

    public async Task StoreConversationReferenceAsync(
        ConversationReference reference, string partitionKey, string rowKey,
        string conversationType, string? teamName = null, string? channelName = null, string? userName = null)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would store conversation reference for {PK}/{RK}",
                partitionKey, rowKey);
            return;
        }

        var entity = new ConversationReferenceEntity
        {
            PartitionKey = partitionKey,
            RowKey = rowKey,
            ConversationReference = JsonSerializer.Serialize(reference),
            ConversationType = conversationType,
            TeamName = teamName,
            ChannelName = channelName,
            UserName = userName,
            InstalledAt = DateTimeOffset.UtcNow,
            LastUpdated = DateTimeOffset.UtcNow
        };

        await _tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Stored conversation reference for {PK}/{RK} (type={Type})",
            partitionKey, rowKey, conversationType);
    }

    public async Task<bool> UpdateConversationReferenceAsync(
        ConversationReference reference, string partitionKey, string rowKey)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(partitionKey, rowKey);
            var entity = response.Value;
            entity.ConversationReference = JsonSerializer.Serialize(reference);
            entity.LastUpdated = DateTimeOffset.UtcNow;
            await _tableClient.UpdateEntityAsync(entity, entity.ETag);
            _logger.LogDebug("Updated conversation reference for {PK}/{RK}", partitionKey, rowKey);
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogDebug("No existing reference for {PK}/{RK} to update, skipping", partitionKey, rowKey);
            return false;
        }
    }

    public async Task RemoveConversationReferenceAsync(string partitionKey, string rowKey)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would remove conversation reference for {PK}/{RK}",
                partitionKey, rowKey);
            return;
        }

        try
        {
            await _tableClient.DeleteEntityAsync(partitionKey, rowKey);
            _logger.LogInformation("Removed conversation reference for {PK}/{RK}", partitionKey, rowKey);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning("Conversation reference not found for {PK}/{RK} during removal", partitionKey, rowKey);
        }
    }

    public async Task RemoveTeamReferencesAsync(string teamId)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would remove all references for team {TeamId}", teamId);
            return;
        }

        var count = 0;
        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == teamId, select: new[] { "PartitionKey", "RowKey" }))
        {
            await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
            count++;
        }
        _logger.LogInformation("Removed {Count} conversation references for team {TeamId}", count, teamId);
    }

    public async IAsyncEnumerable<ConversationReferenceEntity> QueryTeamReferencesAsync(string teamId)
    {
        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == teamId))
        {
            yield return entity;
        }
    }

    public async Task UpdateEntityAsync(ConversationReferenceEntity entity)
    {
        await _tableClient.UpdateEntityAsync(entity, entity.ETag);
    }

    public async Task<ConversationReferenceEntity?> GetConversationReferenceEntityAsync(string partitionKey, string rowKey)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(partitionKey, rowKey);
            return response.Value;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    private async Task<ConversationReference?> GetConversationReferenceAsync(string partitionKey, string rowKey)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(partitionKey, rowKey);
            var entity = response.Value;
            return JsonSerializer.Deserialize<ConversationReference>(
                entity.ConversationReference,
                CaseInsensitiveOptions);
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task EnumerateAndStoreTeamChannelsAsync(
        string serializedReference, string teamGuid, string? teamName, string? teamThreadId)
    {
        if (string.IsNullOrEmpty(teamThreadId))
        {
            _logger.LogWarning("Cannot enumerate channels: teamThreadId is null");
            return;
        }

        var reference = JsonSerializer.Deserialize<ConversationReference>(
            serializedReference,
            CaseInsensitiveOptions);

        if (reference == null)
        {
            _logger.LogError("Failed to deserialize ConversationReference for channel enumeration");
            return;
        }

        var installChannelId = reference.Conversation?.Id;

        await _adapter.ContinueConversationAsync(
            _botAppId,
            reference,
            async (turnContext, ct) =>
            {
                try
                {
                    var channels = await TeamsInfo.GetTeamChannelsAsync(turnContext, teamThreadId, ct);
                    _logger.LogInformation("Enumerated {Count} channels in team {TeamGuid}", channels.Count, teamGuid);

                    foreach (var channel in channels)
                    {
                        // Skip install channel — already stored by handler
                        if (channel.Id == installChannelId) continue;

                        var channelRef = new ConversationReference
                        {
                            ServiceUrl = reference.ServiceUrl,
                            ChannelId = reference.ChannelId,
                            Agent = reference.Agent,
                            Conversation = new ConversationAccount
                            {
                                Id = channel.Id,
                                IsGroup = true,
                                ConversationType = "channel",
                                TenantId = reference.Conversation?.TenantId
                            }
                        };

                        await StoreConversationReferenceAsync(
                            channelRef, teamGuid, channel.Id,
                            "channel", teamName, channel.Name);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate channels for team {TeamGuid}", teamGuid);
                }
            },
            CancellationToken.None);
    }

    public async Task BatchRemoveTeamReferencesAsync(string teamId)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would batch-remove references for team {TeamId}", teamId);
            return;
        }

        var actions = new List<TableTransactionAction>();
        var count = 0;

        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == teamId, select: new[] { "PartitionKey", "RowKey" }))
        {
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, entity));
            count++;

            if (actions.Count == 100)
            {
                await _tableClient.SubmitTransactionAsync(actions);
                actions.Clear();
            }
        }

        if (actions.Count > 0)
        {
            await _tableClient.SubmitTransactionAsync(actions);
        }

        _logger.LogInformation("Batch-removed {Count} conversation references for team {TeamId}", count, teamId);
    }

    public async Task BatchUpdateTeamNameAsync(string teamId, string? newTeamName)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would batch-update team name for {TeamId}", teamId);
            return;
        }

        var actions = new List<TableTransactionAction>();
        var count = 0;

        await foreach (var entity in _tableClient.QueryAsync<ConversationReferenceEntity>(
            e => e.PartitionKey == teamId))
        {
            entity.TeamName = newTeamName;
            entity.LastUpdated = DateTimeOffset.UtcNow;
            actions.Add(new TableTransactionAction(TableTransactionActionType.UpdateMerge, entity));
            count++;

            if (actions.Count == 100)
            {
                await _tableClient.SubmitTransactionAsync(actions);
                actions.Clear();
            }
        }

        if (actions.Count > 0)
        {
            await _tableClient.SubmitTransactionAsync(actions);
        }

        _logger.LogInformation("Batch-updated team name to '{NewName}' on {Count} references for team {TeamId}",
            newTeamName, count, teamId);
    }

    private async Task UpdateLastUpdatedAsync(string partitionKey, string rowKey)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(partitionKey, rowKey);
            var entity = response.Value;
            entity.LastUpdated = DateTimeOffset.UtcNow;
            await _tableClient.UpdateEntityAsync(entity, entity.ETag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastUpdated for {PK}/{RK}", partitionKey, rowKey);
        }
    }
}
