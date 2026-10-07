using System.Text.Json;
using Microsoft.Agents.Core.Models;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public interface IBotService
{
    Task SendMessageAsync(string partitionKey, string rowKey, string message);
    Task SendAdaptiveCardAsync(string partitionKey, string rowKey, JsonElement card);

    /// <summary>
    /// Sends <paramref name="message"/> (<paramref name="format"/> <c>text</c> or <c>adaptive-card</c>)
    /// to the conversation stored under (<paramref name="partitionKey"/>, <paramref name="rowKey"/>)
    /// and returns where it landed. With <paramref name="threadActivityId"/> the message is posted
    /// as a reply in that thread, which only channels have.
    /// </summary>
    Task<SentActivity> SendAsync(
        string partitionKey, string rowKey, string format, string message, string? threadActivityId = null);

    /// <summary>
    /// Replaces activity <paramref name="activityId"/>, which lives in conversation
    /// <paramref name="conversationId"/> (threaded for a reply), with <paramref name="message"/>.
    /// The stored reference under (<paramref name="partitionKey"/>, <paramref name="rowKey"/>)
    /// supplies the service URL and bot identity.
    /// </summary>
    Task UpdateAsync(
        string partitionKey, string rowKey, string conversationId, string activityId, string format, string message);
    Task StoreConversationReferenceAsync(
        ConversationReference reference, string partitionKey, string rowKey,
        string conversationType, string? teamName = null, string? channelName = null, string? userName = null);
    /// <summary>
    /// Stores a channel reference from a channel event (created/renamed/restored) or from
    /// install-time channel enumeration. A missing row is
    /// inserted (insert-only, never an upsert); an existing row is updated in place: the
    /// reference and LastUpdated are replaced, a non-empty name replaces the stored one, and
    /// InstalledAt and any name the event lacks are kept. ETag-guarded; retries on 412 and on a
    /// 409 from a concurrent insert, and propagates the last conflict.
    /// </summary>
    Task UpsertChannelReferenceAsync(
        ConversationReference reference, string teamGuid, string channelId,
        string? teamName, string? channelName);
    Task<bool> UpdateConversationReferenceAsync(ConversationReference reference, string partitionKey, string rowKey);
    Task RemoveConversationReferenceAsync(string partitionKey, string rowKey);
    Task RemoveTeamReferencesAsync(string teamId);
    IAsyncEnumerable<ConversationReferenceEntity> QueryTeamReferencesAsync(string teamId);
    Task UpdateEntityAsync(ConversationReferenceEntity entity);
    Task EnumerateAndStoreTeamChannelsAsync(string serializedReference, string teamGuid, string? teamName, string? teamThreadId);
    Task BatchRemoveTeamReferencesAsync(string teamId);
    Task BatchUpdateTeamNameAsync(string teamId, string? newTeamName);
    Task<ConversationReferenceEntity?> GetConversationReferenceEntityAsync(string partitionKey, string rowKey);

    /// <summary>
    /// Whether the bot still has the conversation reference <paramref name="alias"/> points to,
    /// i.e. whether a message to it can be delivered at all. False for a malformed alias. Always
    /// true when Teams integration is disabled, which stores no references.
    /// </summary>
    Task<bool> HasConversationAsync(AliasEntity alias);

    /// <summary>
    /// Whether the bot still has the conversation reference stored under
    /// (<paramref name="partitionKey"/>, <paramref name="rowKey"/>). Always true when Teams
    /// integration is disabled, which stores no references.
    /// </summary>
    Task<bool> HasConversationAsync(string partitionKey, string rowKey);

    /// <summary>
    /// Sets ChannelName on an existing conversationreferences row if — and only if — ChannelName
    /// is currently empty, refreshing LastUpdated with it. ETag-guarded; touches no other column,
    /// in particular never ConversationReference or InstalledAt. Best-effort: never throws.
    /// Returns true only when the name was actually written.
    /// </summary>
    Task<bool> TryUpdateChannelNameAsync(string partitionKey, string rowKey, string channelName);

    /// <summary>
    /// TeamName counterpart of <see cref="TryUpdateChannelNameAsync"/>, with the same contract:
    /// writes only when TeamName is empty, touches nothing else but LastUpdated, never throws.
    /// </summary>
    Task<bool> TryUpdateTeamNameAsync(string partitionKey, string rowKey, string teamName);
}

/// <summary>Where a sent activity landed. <c>ActivityId</c> is null if the channel didn't return one.</summary>
public sealed record SentActivity(string ConversationId, string? ActivityId);
