using System.Security.Cryptography;
using System.Text;
using Azure;
using Azure.Data.Tables;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class WebhookService : IWebhookService
{
    private const string Partition = "webhook";
    private readonly TableClient _tableClient;

    public WebhookService(TableClient tableClient)
    {
        _tableClient = tableClient;
    }

    public async Task<WebhookTokenEntity?> ResolveByTokenAsync(string token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var response = await _tableClient.GetEntityAsync<WebhookTokenEntity>(Partition, Sha256Hex(token));
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<WebhookCreateResult> CreateAsync(
        string source, string targetType,
        string? teamId, string? channelId, string? userId, string? chatId,
        string createdBy, string createdByName)
    {
        var token = GenerateToken();
        var entity = new WebhookTokenEntity
        {
            PartitionKey = Partition,
            RowKey = Sha256Hex(token),
            Id = GenerateId(),
            Source = source,
            TargetType = targetType,
            TeamId = teamId,
            ChannelId = channelId,
            UserId = userId,
            ChatId = chatId,
            EnabledEvents = string.Join(',', UpdownEventTypes.DefaultEnabled),
            CreatedBy = createdBy,
            CreatedByName = createdByName,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _tableClient.UpsertEntityAsync(entity);
        return new WebhookCreateResult(entity.Id, token, entity);
    }

    public async Task<IReadOnlyList<WebhookTokenEntity>> ListAsync()
    {
        var results = new List<WebhookTokenEntity>();
        await foreach (var entity in _tableClient.QueryAsync<WebhookTokenEntity>(e => e.PartitionKey == Partition))
        {
            results.Add(entity);
        }
        return results;
    }

    public async Task<WebhookTokenEntity?> GetByIdAsync(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        await foreach (var entity in _tableClient.QueryAsync<WebhookTokenEntity>(
            e => e.PartitionKey == Partition && e.Id == id))
        {
            return entity;
        }
        return null;
    }

    public async Task<bool> RemoveByIdAsync(string id)
    {
        var entity = await GetByIdAsync(id);
        if (entity == null) return false;

        await _tableClient.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
        return true;
    }

    public async Task<string?> RotateByIdAsync(string id)
    {
        var entity = await GetByIdAsync(id);
        if (entity == null) return null;

        var oldRowKey = entity.RowKey;
        var newToken = GenerateToken();
        entity.RowKey = Sha256Hex(newToken);
        entity.ETag = default; // new row

        await _tableClient.UpsertEntityAsync(entity);
        if (!string.Equals(oldRowKey, entity.RowKey, StringComparison.Ordinal))
        {
            try { await _tableClient.DeleteEntityAsync(Partition, oldRowKey); }
            catch (RequestFailedException ex) when (ex.Status == 404) { /* already gone */ }
        }
        return newToken;
    }

    public async Task<bool> ConfigureAsync(
        string id, string? description, string? updownAccount, IReadOnlyList<string>? enabledEvents)
    {
        var entity = await GetByIdAsync(id);
        if (entity == null) return false;

        if (description != null) entity.Description = description;
        if (updownAccount != null) entity.UpdownAccount = updownAccount;
        if (enabledEvents != null) entity.EnabledEvents = string.Join(',', enabledEvents);

        await _tableClient.UpsertEntityAsync(entity);
        return true;
    }

    public async Task TouchLastReceivedAsync(WebhookTokenEntity entity)
    {
        try
        {
            entity.LastReceivedAt = DateTimeOffset.UtcNow;
            await _tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Merge);
        }
        catch (RequestFailedException)
        {
            // Best-effort bookkeeping — a concurrent update or missing row must not fail delivery.
        }
    }

    // --- helpers (internal for unit testing) ---

    /// <summary>Lowercase hex SHA-256 of the token. Deterministic; used as the table RowKey.</summary>
    internal static string Sha256Hex(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    /// <summary>256-bit URL-safe random token (base64url, no padding).</summary>
    internal static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Short public id (8 lowercase hex).</summary>
    internal static string GenerateId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}
