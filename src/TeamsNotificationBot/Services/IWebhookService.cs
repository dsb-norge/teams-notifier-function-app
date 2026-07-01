using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public interface IWebhookService
{
    /// <summary>Point-read the webhook bound to <paramref name="token"/> (by SHA-256), or null.</summary>
    Task<WebhookTokenEntity?> ResolveByTokenAsync(string token);

    /// <summary>
    /// Generates a fresh token, stores only its hash, and returns the plaintext token
    /// (shown to the operator exactly once). Applies the default event filter.
    /// </summary>
    Task<WebhookCreateResult> CreateAsync(
        string source, string targetType,
        string? teamId, string? channelId, string? userId, string? chatId,
        string createdBy, string createdByName);

    Task<IReadOnlyList<WebhookTokenEntity>> ListAsync();

    Task<WebhookTokenEntity?> GetByIdAsync(string id);

    Task<bool> RemoveByIdAsync(string id);

    /// <summary>Issues a new token for an existing webhook (old token stops working). Returns the
    /// new plaintext token, or null if the id was not found.</summary>
    Task<string?> RotateByIdAsync(string id);

    /// <summary>Updates the provided (non-null) config fields. Returns false if the id was not found.</summary>
    Task<bool> ConfigureAsync(string id, string? description, string? updownAccount, IReadOnlyList<string>? enabledEvents);

    /// <summary>Best-effort bump of LastReceivedAt; never throws.</summary>
    Task TouchLastReceivedAsync(WebhookTokenEntity entity);
}

public record WebhookCreateResult(string Id, string Token, WebhookTokenEntity Entity);
