using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Microsoft.Agents.Authentication;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Connector;
using Microsoft.Agents.Core.Errors;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Models;
using TeamsApi = Microsoft.Teams.Api;

namespace TeamsNotificationBot.Services;

public class BotService : IBotService
{
    // Azure Table Storage caps a single SubmitTransaction at 100 entities.
    private const int MaxBatchSize = 100;

    // The largest page the roster API returns.
    private const int RosterPageSize = 500;

    private static readonly JsonSerializerOptions CaseInsensitiveOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly CloudAdapter _adapter;
    private readonly TableClient _tableClient;
    private readonly TableClient _teamLookupTable;
    private readonly string _botAppId;
    private readonly bool _teamsDisabled;
    private readonly ILogger<BotService> _logger;
    private readonly IConnections _connections;
    private readonly IHttpClientFactory _httpClientFactory;

    // A singleton, like the service: the cache outlives single sends.
    private readonly RosterCache _rosters = new(TimeProvider.System);

    // Required (not optional) so a missing AddHttpClient()/IConnections registration fails at
    // startup DI resolution instead of on the first channel-enumeration turn in production.
    public BotService(
        CloudAdapter adapter,
        TableClient tableClient,
        ILogger<BotService> logger,
        IConnections connections,
        IHttpClientFactory httpClientFactory,
        [FromKeyedServices("teamlookup")] TableClient teamLookupTable)
    {
        _adapter = adapter;
        _tableClient = tableClient;
        _teamLookupTable = teamLookupTable;
        _connections = connections;
        _httpClientFactory = httpClientFactory;
        _botAppId = Environment.GetEnvironmentVariable("BotAppId") ?? string.Empty;
        _teamsDisabled = string.Equals(
            Environment.GetEnvironmentVariable("TEAMS_INTEGRATION_DISABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        _logger = logger;
    }

    public Task SendMessageAsync(string partitionKey, string rowKey, string message) =>
        SendAsync(partitionKey, rowKey, "text", message);

    public Task SendAdaptiveCardAsync(string partitionKey, string rowKey, JsonElement card) =>
        SendAsync(partitionKey, rowKey, "adaptive-card", card.GetRawText());

    public async Task<SentActivity> SendAsync(
        string partitionKey, string rowKey, string format, string message, string? threadActivityId = null,
        IReadOnlyList<MessageMention>? mentions = null)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would send {Format} to {PK}/{RK}: {Message}",
                format, partitionKey, rowKey, message);
            return new SentActivity(string.Empty, null);
        }

        var reference = await GetConversationReferenceAsync(partitionKey, rowKey);
        if (reference?.Conversation == null)
        {
            _logger.LogError("No conversation reference found for {PK}/{RK}", partitionKey, rowKey);
            throw new InvalidOperationException(
                $"No conversation reference found for '{partitionKey}'/'{rowKey}'. Ensure the bot is installed.");
        }

        var baseConversationId = BaseConversationId(reference.Conversation.Id);
        if (threadActivityId != null)
        {
            // Posting to "<channel>;messageid=<root>" makes the post a reply in that thread.
            reference.Conversation.Id = $"{baseConversationId};messageid={threadActivityId}";
        }

        string? activityId = null;
        IReadOnlyList<string> unresolved = [];
        await Helpers.ThrottleRetry.ExecuteAsync(() => _adapter.ContinueConversationAsync(
            AgentClaims.CreateIdentity(_botAppId),
            reference,
            async (turnContext, ct) =>
            {
                unresolved = await DeliverAsync(turnContext, partitionKey, baseConversationId, format, message, mentions,
                    async activity => activityId = (await turnContext.SendActivityAsync(activity, ct))?.Id, ct);
            },
            CancellationToken.None), logger: _logger);

        await UpdateLastUpdatedAsync(partitionKey, rowKey);
        _logger.LogInformation(
            "Sent {Format} to {PK}/{RK}. ActivityId={ActivityId}, Threaded={Threaded}, UnresolvedMentions={Unresolved}",
            format, partitionKey, rowKey, activityId, threadActivityId != null, unresolved.Count);
        return new SentActivity(reference.Conversation.Id, activityId) { UnresolvedMentions = unresolved };
    }

    public async Task<IReadOnlyList<string>> UpdateAsync(
        string partitionKey, string rowKey, string conversationId, string activityId, string format, string message,
        IReadOnlyList<MessageMention>? mentions = null)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would update activity {ActivityId} in {PK}/{RK}",
                activityId, partitionKey, rowKey);
            return [];
        }

        var reference = await GetConversationReferenceAsync(partitionKey, rowKey);
        if (reference?.Conversation == null)
        {
            _logger.LogError("No conversation reference found for {PK}/{RK}", partitionKey, rowKey);
            throw new InvalidOperationException(
                $"No conversation reference found for '{partitionKey}'/'{rowKey}'. Ensure the bot is installed.");
        }

        // The activity is addressed in the conversation it was posted to, threaded or not.
        reference.Conversation.Id = conversationId;

        IReadOnlyList<string> unresolved = [];
        await Helpers.ThrottleRetry.ExecuteAsync(() => _adapter.ContinueConversationAsync(
            AgentClaims.CreateIdentity(_botAppId),
            reference,
            async (turnContext, ct) =>
            {
                unresolved = await DeliverAsync(turnContext, partitionKey, BaseConversationId(conversationId),
                    format, message, mentions,
                    activity =>
                    {
                        activity.Id = activityId;
                        return turnContext.UpdateActivityAsync(activity, ct);
                    }, ct);
            },
            CancellationToken.None), logger: _logger);

        _logger.LogInformation("Updated activity {ActivityId} in {PK}/{RK}. UnresolvedMentions={Unresolved}",
            activityId, partitionKey, rowKey, unresolved.Count);
        return unresolved;
    }

    /// <summary>
    /// Builds the activity, with any mentions resolved against the conversation's roster, and hands
    /// it to <paramref name="deliver"/> (a send or an update). Returns the mentions written as plain
    /// text. If Teams rejects a message that mentions tags (Microsoft documents a 400 for a tag the
    /// team doesn't have), the message goes again with the tags as plain text: the message matters
    /// more than the ping, and the caller sees the tags reported.
    /// </summary>
    private async Task<IReadOnlyList<string>> DeliverAsync(
        ITurnContext turnContext, string partitionKey, string conversationId, string format, string message,
        IReadOnlyList<MessageMention>? mentions, Func<IActivity, Task> deliver, CancellationToken ct)
    {
        if (mentions is not { Count: > 0 })
        {
            await deliver(BuildActivity(format, message));
            return [];
        }

        var targetType = ConversationReferenceEntity.TargetTypeOf(partitionKey);
        var roster = mentions.Any(m => m.Id != null)
            ? await GetRosterAsync(turnContext, conversationId, targetType, ct)
            : Roster.Empty;

        var rendered = MentionRenderer.Render(format, message, mentions, roster.Find, tagsAllowed: targetType == "channel");
        try
        {
            await deliver(BuildActivity(format, rendered));
        }
        catch (ErrorResponseException ex) when (ex.StatusCode == 400 && rendered.HasTagMentions)
        {
            // Teams' reason is in the response body, not the exception message: log it, or a
            // rejected tag can't be told from a malformed one.
            _logger.LogWarning(ex,
                "Teams rejected the message's tag mentions ({Code}: {Reason}); sending the tags as plain text instead",
                Helpers.LogSanitizer.Sanitize(ex.Body?.Error?.Code), Helpers.LogSanitizer.Sanitize(ex.Body?.Error?.Message));
            rendered = MentionRenderer.Render(format, message, mentions, roster.Find, tagsAllowed: false);
            await deliver(BuildActivity(format, rendered));
        }
        return rendered.Unresolved;
    }

    /// <summary>
    /// The roster person mentions are checked against: the team's for a channel, the members for a
    /// group chat, nobody in a personal chat. A roster Teams refuses to give (a 4xx other than a
    /// throttle) won't change on a retry, so the message goes out with its people unresolved;
    /// anything else propagates for the queue to retry.
    /// </summary>
    private async Task<Roster> GetRosterAsync(
        ITurnContext turnContext, string conversationId, string targetType, CancellationToken ct)
    {
        if (targetType == "personal")
            return Roster.Empty;

        try
        {
            return await _rosters.GetAsync(conversationId, () => ReadRosterAsync(turnContext, conversationId, targetType, ct));
        }
        catch (ErrorResponseException ex) when (ex.StatusCode is >= 400 and < 500 and not 429)
        {
            _logger.LogWarning(ex,
                "Could not read the roster ({Status} {Code}: {Reason}); person mentions go out as plain text",
                ex.StatusCode, Helpers.LogSanitizer.Sanitize(ex.Body?.Error?.Code), Helpers.LogSanitizer.Sanitize(ex.Body?.Error?.Message));
            return Roster.Empty;
        }
    }

    private static async Task<IEnumerable<ChannelAccount>> ReadRosterAsync(
        ITurnContext turnContext, string conversationId, string targetType, CancellationToken ct)
    {
        var conversations = turnContext.Services.Get<IConnectorClient>()?.Conversations
            ?? throw new InvalidOperationException("The proactive turn has no connector client to read the roster with.");

        // Teams pages team and channel rosters; a chat's comes whole.
        if (targetType != "channel")
            return await conversations.GetConversationMembersAsync(conversationId, ct);

        var members = new List<ChannelAccount>();
        string? continuationToken = null;
        do
        {
            var page = await conversations.GetConversationPagedMembersAsync(conversationId, RosterPageSize, continuationToken, ct);
            members.AddRange(page.Members ?? []);
            continuationToken = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(continuationToken));
        return members;
    }

    public async Task<(string PartitionKey, string RowKey)?> FindPersonalConversationAsync(string userId, string? teamGuid = null)
    {
        // Offline local mode stores no references and reads no rosters.
        if (_teamsDisabled)
            return ("user", PersonIds.ObjectId(userId) ?? userId);

        // A personal conversation is stored under the person's object ID.
        if (PersonIds.ObjectId(userId) is { } objectId && await HasConversationAsync("user", objectId))
            return ("user", objectId);

        await foreach (var team in _teamLookupTable.QueryAsync<TeamLookupEntity>(e => e.PartitionKey == "teamlookup"))
        {
            if (teamGuid != null && !string.Equals(team.TeamGuid, teamGuid, StringComparison.OrdinalIgnoreCase))
                continue;

            var reference = await GetTeamReferenceAsync(team);
            if (reference?.Conversation == null)
            {
                _logger.LogWarning("No stored conversation for team {TeamGuid}; skipping it in the roster search", team.TeamGuid);
                continue;
            }

            // The team's thread ID is its own conversation, whose roster is the team's members.
            ChannelAccount? member = null;
            await Helpers.ThrottleRetry.ExecuteAsync(() => _adapter.ContinueConversationAsync(
                AgentClaims.CreateIdentity(_botAppId),
                reference,
                async (turnContext, ct) =>
                {
                    var roster = await GetRosterAsync(turnContext, team.RowKey, "channel", ct);
                    member = roster.Find(userId);
                },
                CancellationToken.None), logger: _logger);

            if (member is not { Id.Length: > 0 } || PersonIds.ObjectId(Roster.ObjectIdOf(member) ?? string.Empty) is not { } memberObjectId)
                continue;

            if (!await HasConversationAsync("user", memberObjectId))
                await CreatePersonalConversationAsync(reference, member, memberObjectId);
            _logger.LogInformation("Found the recipient in team {TeamGuid}; personal conversation {RK}", team.TeamGuid, memberObjectId);
            return ("user", memberObjectId);
        }

        return null;
    }

    /// <summary>A stored reference in the team, for its service URL, tenant and bot account: the
    /// General channel's (its ID is the team's thread ID) if there is one, else any channel's.</summary>
    private async Task<ConversationReference?> GetTeamReferenceAsync(TeamLookupEntity team)
    {
        if (await GetConversationReferenceAsync(team.TeamGuid, team.RowKey) is { } general)
            return general;

        await foreach (var entity in QueryTeamReferencesAsync(team.TeamGuid))
        {
            var reference = JsonSerializer.Deserialize<ConversationReference>(entity.ConversationReference, CaseInsensitiveOptions);
            if (reference?.Conversation != null)
                return reference;
        }
        return null;
    }

    /// <summary>
    /// Opens a one-to-one conversation with <paramref name="member"/>, a member of the team
    /// <paramref name="teamReference"/> belongs to, and stores it under ("user", object ID) like a
    /// personal installation would. Teams returns the existing chat if there is one, so this is safe
    /// to repeat.
    /// </summary>
    private async Task CreatePersonalConversationAsync(
        ConversationReference teamReference, ChannelAccount member, string objectId)
    {
        var tenantId = teamReference.Conversation.TenantId;
        var parameters = new ConversationParameters
        {
            IsGroup = false,
            Agent = teamReference.Agent,
            Members = [new ChannelAccount { Id = member.Id }],
            TenantId = tenantId,
            ChannelData = new { tenant = new { id = tenantId } }
        };

        var identity = AgentClaims.CreateIdentity(_botAppId);
        ConversationReference? created = null;
        // No callback: the reference the SDK returns is all that's needed, without a turn.
        await Helpers.ThrottleRetry.ExecuteAsync(async () => created = await _adapter.CreateConversationAsync(
            identity,
            teamReference.ChannelId,
            teamReference.ServiceUrl,
            AgentClaims.GetOutgoingAudienceClaim(identity),
            parameters,
            null!,
            CancellationToken.None), logger: _logger);

        if (created?.Conversation == null)
            throw new InvalidOperationException("Teams created no personal conversation with the recipient.");
        await StoreConversationReferenceAsync(created, "user", objectId, "personal", userName: member.Name);
    }

    private static IActivity BuildActivity(string format, string message) => format == "adaptive-card"
        ? MessageFactory.Attachment(new Attachment
        {
            ContentType = "application/vnd.microsoft.card.adaptive",
            Content = JsonSerializer.Deserialize<object>(message)
        })
        : MessageFactory.Text(message);

    private static IActivity BuildActivity(string format, MentionRenderer.Rendered rendered)
    {
        var activity = BuildActivity(format, rendered.Message);
        if (rendered.Entities.Count > 0)
        {
            activity.Entities ??= [];
            foreach (var entity in rendered.Entities)
                activity.Entities.Add(entity);
        }
        return activity;
    }

    /// <summary>A channel conversation ID without any ";messageid=…" thread suffix.</summary>
    internal static string BaseConversationId(string conversationId)
    {
        var separator = conversationId.IndexOf(";messageid=", StringComparison.OrdinalIgnoreCase);
        return separator < 0 ? conversationId : conversationId[..separator];
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

        var entity = NewReferenceEntity(
            reference, partitionKey, rowKey, conversationType, teamName, channelName, userName);

        await _tableClient.UpsertEntityAsync(entity);
        _logger.LogInformation("Stored conversation reference for {PK}/{RK} (type={Type})",
            partitionKey, rowKey, conversationType);
    }

    private static ConversationReferenceEntity NewReferenceEntity(
        ConversationReference reference, string partitionKey, string rowKey,
        string conversationType, string? teamName, string? channelName, string? userName) => new()
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

    public async Task UpsertChannelReferenceAsync(
        ConversationReference reference, string teamGuid, string channelId,
        string? teamName, string? channelName)
    {
        if (_teamsDisabled)
        {
            _logger.LogInformation(
                "Teams integration disabled. Would upsert channel reference for {PK}/{RK}",
                teamGuid, channelId);
            return;
        }

        // Same optimistic-concurrency pattern as the name backfills: retry a few times on 412,
        // or on 409 when a concurrent writer creates the row between our read and our insert.
        // Unlike the backfills this is the event's primary write, so the last conflict propagates.
        const int maxRetries = 3;
        for (var attempt = 1; ; attempt++)
        {
            ConversationReferenceEntity entity;
            try
            {
                entity = (await _tableClient.GetEntityAsync<ConversationReferenceEntity>(teamGuid, channelId)).Value;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Insert-only, so a row another writer just created is updated in place on the
                // next attempt rather than overwritten here.
                try
                {
                    await _tableClient.AddEntityAsync(NewReferenceEntity(
                        reference, teamGuid, channelId, "channel", teamName, channelName, userName: null));
                    _logger.LogInformation("Stored conversation reference for {PK}/{RK} (type={Type})",
                        teamGuid, channelId, "channel");
                    return;
                }
                catch (RequestFailedException addEx) when (addEx.Status == 409 && attempt < maxRetries)
                {
                    _logger.LogDebug(
                        addEx,
                        "Channel reference for {PK}/{RK} created concurrently; retry {Next}/{MaxRetries}",
                        teamGuid, channelId, attempt + 1, maxRetries);
                    continue;
                }
            }

            // Update in place: keep InstalledAt, and never replace a stored name with an absent one.
            entity.ConversationReference = JsonSerializer.Serialize(reference);
            if (!string.IsNullOrEmpty(teamName))
                entity.TeamName = teamName;
            if (!string.IsNullOrEmpty(channelName))
                entity.ChannelName = channelName;
            entity.LastUpdated = DateTimeOffset.UtcNow;

            try
            {
                await _tableClient.UpdateEntityAsync(entity, entity.ETag);
                _logger.LogInformation("Updated channel reference for {PK}/{RK}", teamGuid, channelId);
                return;
            }
            catch (RequestFailedException ex) when (ex.Status == 412 && attempt < maxRetries)
            {
                _logger.LogDebug(
                    ex,
                    "Concurrency conflict updating channel reference for {PK}/{RK}; retry {Next}/{MaxRetries}",
                    teamGuid, channelId, attempt + 1, maxRetries);
            }
        }
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

    public Task<bool> TryUpdateChannelNameAsync(string partitionKey, string rowKey, string channelName) =>
        TryBackfillNameAsync(partitionKey, rowKey, channelName, "ChannelName",
            e => e.ChannelName, (e, v) => e.ChannelName = v);

    public Task<bool> TryUpdateTeamNameAsync(string partitionKey, string rowKey, string teamName) =>
        TryBackfillNameAsync(partitionKey, rowKey, teamName, "TeamName",
            e => e.TeamName, (e, v) => e.TeamName = v);

    private async Task<bool> TryBackfillNameAsync(
        string partitionKey, string rowKey, string name, string column,
        Func<ConversationReferenceEntity, string?> get, Action<ConversationReferenceEntity, string> set)
    {
        if (_teamsDisabled)
            return false;
        if (string.IsNullOrEmpty(name))
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
                    if (!string.IsNullOrEmpty(get(entity)))
                        return false; // an earlier run or a concurrent writer already set it — never overwrite

                    set(entity, name);
                    entity.LastUpdated = DateTimeOffset.UtcNow;
                    await _tableClient.UpdateEntityAsync(entity, entity.ETag);
                    _logger.LogInformation("Backfilled {Column} for {PK}/{RK}", column, partitionKey, rowKey);
                    return true;
                }
                catch (RequestFailedException ex) when (ex.Status == 412)
                {
                    if (attempt < maxRetries)
                    {
                        _logger.LogDebug(
                            ex,
                            "Concurrency conflict backfilling {Column} for {PK}/{RK}; retry {Next}/{MaxRetries}",
                            column, partitionKey, rowKey, attempt + 1, maxRetries);
                        continue;
                    }
                    _logger.LogWarning(
                        ex,
                        "Gave up backfilling {Column} for {PK}/{RK} after {MaxRetries} concurrency conflicts",
                        column, partitionKey, rowKey, maxRetries);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to backfill {Column} for {PK}/{RK}", column, partitionKey, rowKey);
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

    public async Task<bool> HasConversationAsync(AliasEntity alias) =>
        _teamsDisabled ||
        (alias.ConversationKey() is { } key && await HasConversationAsync(key.PartitionKey, key.RowKey));

    public async Task<bool> HasConversationAsync(string partitionKey, string rowKey) =>
        // Offline local mode stores no references, so every conversation would look gone.
        _teamsDisabled || await GetConversationReferenceEntityAsync(partitionKey, rowKey) != null;

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

                        // Same in-place writer as channel events: enumeration can race a
                        // channelCreated for the same channel right after install.
                        await UpsertChannelReferenceAsync(channelRef, teamGuid, channel.Id, teamName, channelName);
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
