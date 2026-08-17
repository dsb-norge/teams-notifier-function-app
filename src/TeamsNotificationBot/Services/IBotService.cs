using System.Text.Json;
using Microsoft.Agents.Core.Models;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public interface IBotService
{
    Task SendMessageAsync(string partitionKey, string rowKey, string message);
    Task SendAdaptiveCardAsync(string partitionKey, string rowKey, JsonElement card);
    Task StoreConversationReferenceAsync(
        ConversationReference reference, string partitionKey, string rowKey,
        string conversationType, string? teamName = null, string? channelName = null, string? userName = null);
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
    /// Sets ChannelName on an existing conversationreferences row if — and only if — ChannelName
    /// is currently empty, refreshing LastUpdated with it. ETag-guarded; touches no other column,
    /// in particular never ConversationReference or InstalledAt. Best-effort: never throws.
    /// Returns true only when the name was actually written.
    /// </summary>
    Task<bool> TryUpdateChannelNameAsync(string partitionKey, string rowKey, string channelName);
}
