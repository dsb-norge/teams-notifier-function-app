using System.Text.Json.Serialization;
using Microsoft.Teams.Common.Http;
using TeamsApi = Microsoft.Teams.Api;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Lists a team's channels via the Bot Framework REST endpoint
/// (GET {serviceUrl}/v3/teams/{teamThreadId}/conversations).
///
/// Deliberately NOT TeamClient.GetConversationsAsync: in Microsoft.Teams.Api 2.0.9 that method
/// deserializes the response body as a bare array, but the service returns
/// {"conversations":[...]} — so it throws JsonException on every real call. This helper issues
/// the same request through the same authenticated IHttpClient and deserializes the documented
/// wrapper. Revisit when bumping Microsoft.Teams.Api past 2.0.9 (teams.net main has since fixed
/// the deserialization).
/// </summary>
internal static class TeamsChannelList
{
    internal static async Task<IReadOnlyList<TeamsApi.Channel>> GetTeamChannelsAsync(
        TeamsApi.Clients.ApiClient apiClient,
        string teamThreadId,
        CancellationToken cancellationToken = default)
    {
        var url = $"{apiClient.ServiceUrl.TrimEnd('/')}/v3/teams/{Uri.EscapeDataString(teamThreadId)}/conversations";
        var response = await apiClient.Client.SendAsync<ConversationList>(
            HttpRequest.Get(url), cancellationToken);
        return response?.Body?.Conversations ?? [];
    }

    private sealed class ConversationList
    {
        [JsonPropertyName("conversations")]
        public List<TeamsApi.Channel>? Conversations { get; set; }
    }
}
