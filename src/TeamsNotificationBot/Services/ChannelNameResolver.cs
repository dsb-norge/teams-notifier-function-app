namespace TeamsNotificationBot.Services;

/// <summary>
/// The Teams API and conversationUpdate payloads return the General channel with a null
/// name; its channel ID equals the team thread ID. Substitute the canonical "General" so
/// the channel never persists nameless. Teams clients localize the display name (e.g.
/// "Generelt") — the canonical form is an accepted approximation, and General cannot be
/// renamed, so the value never goes stale.
/// </summary>
public static class ChannelNameResolver
{
    public static string? Resolve(string? name, string? channelId, string? teamThreadId)
    {
        if (!string.IsNullOrEmpty(name))
            return name;
        if (!string.IsNullOrEmpty(channelId) && channelId == teamThreadId)
            return "General";
        return null;
    }
}
