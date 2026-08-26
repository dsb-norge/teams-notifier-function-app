using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;
using TeamsApi = Microsoft.Teams.Api;

namespace TeamsNotificationBot.Services;

public class BotService : IBotService
{
    // Azure Table Storage caps a single SubmitTransaction at 100 entities.
    private const int MaxBatchSize = 100;

    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CloudAdapter _adapter;
    private readonly TableClient _tableClient;
    private readonly string _botAppId;
    private readonly bool _teamsDisabled;
    private readonly ILogger<BotService> _logger;
    private readonly IConnections _connections;
    private readonly IHttpClientFactory _httpClientFactory;

    // Required (not optional) so a missing AddHttpClient()/IConnections registration fails at
    // startup DI resolution instead of on the first channel-enumeration turn in production.
    public BotService(
        CloudAdapter adapter,
        TableClient tableClient,
        ILogger<BotService> logger,
        IConnections connections,
        IHttpClientFactory httpClientFactory)
    {
        _adapter = adapter;
        _tableClient = tableClient;
        _connections = connections;
        _httpClientFactory = httpClientFactory;
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

        await Helpers.ThrottleRetry.ExecuteAsync(() => _adapter.ContinueConversationAsync(
            AgentClaims.CreateIdentity(_botAppId),
            reference,
            async (turnContext, ct) =>
            {
                await turnContext.SendActivityAsync(MessageFactory.Text(message), ct);
            },
            CancellationToken.None), logger: _logger);

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

        await Helpers.ThrottleRetry.ExecuteAsync(() => _adapter.ContinueConversationAsync(
            AgentClaims.CreateIdentity(_botAppId),
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
            CancellationToken.None), logger: _logger);

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
        catch (Azure.RequestFailedException ex) when (ex.Status == 412)
        {
            // Lost an optimistic-concurrency race with another writer (e.g. a ChannelName
            // backfill). The next inbound message refreshes the reference anyway.
            _logger.LogDebug(ex, "Concurrency conflict refreshing reference for {PK}/{RK}, skipping", partitionKey, rowKey);
            return false;
        }
    }

    public async Task<bool> TryUpdateChannelNameAsync(string partitionKey, string rowKey, string channelName)
    {
        if (_teamsDisabled)
            return false;
        if (string.IsNullOrEmpty(channelName))
            return false;

        // Best-effort bookkeeping, same optimistic-concurrency pattern as UpdateLastUpdatedAsync:
        // retry a few times on 412 so a transient race doesn't silently drop the write.
        const int maxRetries = 3;
        try
        {
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(partitionKey, rowKey);
                    var entity = response.Value;
                    if (!string.IsNullOrEmpty(entity.ChannelName))
                        return false; // an earlier run or a concurrent writer already set it — never overwrite

                    entity.ChannelName = channelName;
                    entity.LastUpdated = DateTimeOffset.UtcNow;
                    await _tableClient.UpdateEntityAsync(entity, entity.ETag);
                    _logger.LogInformation("Backfilled ChannelName for {PK}/{RK}", partitionKey, rowKey);
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    if (attempt < maxRetries)
                    {
                        _logger.LogDebug(
                            ex,
                            "Concurrency conflict backfilling ChannelName for {PK}/{RK}; retry {Next}/{MaxRetries}",
                            partitionKey, rowKey, attempt + 1, maxRetries);
                        continue;
                    }
                    _logger.LogWarning(
                        ex,
                        "Gave up backfilling ChannelName for {PK}/{RK} after {MaxRetries} concurrency conflicts",
                        partitionKey, rowKey, maxRetries);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill ChannelName for {PK}/{RK}", partitionKey, rowKey);
        }
        return false;
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
            AgentClaims.CreateIdentity(_botAppId),
            reference,
            async (turnContext, ct) =>
            {
                try
                {
                    var channels = await GetTeamChannelsProactiveAsync(turnContext, teamThreadId, ct);
                    _logger.LogInformation("Enumerated {Count} channels in team {TeamGuid}", channels.Count, teamGuid);

                    foreach (var channel in channels)
                    {
                        var channelName = ChannelNameResolver.Resolve(channel.Name, channel.Id, teamThreadId);

                        if (channel.Id == installChannelId)
                        {
                            // The handler already stored this row with the real activity-derived
                            // reference — never overwrite it. Just fill in the name the
                            // conversationUpdate payload lacked.
                            await TryUpdateChannelNameAsync(teamGuid, channel.Id, channelName ?? string.Empty);
                            continue;
                        }

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
                            "channel", teamName, channelName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enumerate channels for team {TeamGuid}", teamGuid);
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Builds an authenticated Teams ApiClient for a proactive turn and lists the team's
    /// channels. Proactive callbacks from ContinueConversationAsync bypass the AgentApplication
    /// turn pipeline, so the client the TeamsAgentExtension normally stashes in
    /// turnContext.Services is absent — this replicates the SDK's own construction (token from
    /// IConnections against the turn's ServiceUrl), but acquires the token asynchronously up
    /// front instead of sync-over-async per request, and disposes the HTTP wrapper when done.
    /// </summary>
    private async Task<IReadOnlyList<TeamsApi.Channel>> GetTeamChannelsProactiveAsync(
        ITurnContext turnContext, string teamThreadId, CancellationToken cancellationToken)
    {
        string? token = null;
        if (!AgentClaims.AllowAnonymous(turnContext.Identity))
        {
            var tokenAccess = _connections.GetTokenProvider(
                turnContext.Identity, turnContext.Activity.ServiceUrl);
            token = await tokenAccess.GetAccessTokenAsync(
                AuthenticationConstants.BotFrameworkAudience,
                [AuthenticationConstants.BotFrameworkDefaultScope]);
        }

        using var teamsHttpClient = new Microsoft.Teams.Common.Http.HttpClient(
            _httpClientFactory.CreateClient(nameof(BotService)));
        if (token != null)
            teamsHttpClient.Options.TokenFactory = () => token;

        var apiClient = new TeamsApi.Clients.ApiClient(
            turnContext.Activity.ServiceUrl, teamsHttpClient);
        return await Helpers.TeamsChannelList.GetTeamChannelsAsync(
            apiClient, teamThreadId, cancellationToken);
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
            // Build a minimal entity with an explicit wildcard ETag — the query
            // projection above doesn't include ETag, so we'd otherwise rely on
            // an implicitly-defaulted value which TableTransactionAction.Delete
            // treats as "must match", causing 412 on otherwise-valid deletes.
            var deleteStub = new ConversationReferenceEntity
            {
                PartitionKey = entity.PartitionKey,
                RowKey = entity.RowKey,
                ETag = ETag.All
            };
            actions.Add(new TableTransactionAction(TableTransactionActionType.Delete, deleteStub));
            count++;

            if (actions.Count == MaxBatchSize)
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

            if (actions.Count == MaxBatchSize)
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
        // Best-effort bookkeeping. The optimistic ETag check races against any
        // concurrent writer on the same row; retry a few times on 412 before
        // giving up so a transient race doesn't silently drop the timestamp.
        const int maxRetries = 3;
        try
        {
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    var response = await _tableClient.GetEntityAsync<ConversationReferenceEntity>(partitionKey, rowKey);
                    var entity = response.Value;
                    entity.LastUpdated = DateTimeOffset.UtcNow;
                    await _tableClient.UpdateEntityAsync(entity, entity.ETag);
                    return;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    if (attempt < maxRetries)
                    {
                        _logger.LogDebug(
                            ex,
                            "Concurrency conflict updating LastUpdated for {PK}/{RK}; retry {Next}/{MaxRetries}",
                            partitionKey, rowKey, attempt + 1, maxRetries);
                        continue;
                    }
                    _logger.LogWarning(
                        ex,
                        "Failed to update LastUpdated for {PK}/{RK} after {MaxRetries} concurrency conflicts",
                        partitionKey, rowKey, maxRetries);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update LastUpdated for {PK}/{RK}", partitionKey, rowKey);
        }
    }
}
