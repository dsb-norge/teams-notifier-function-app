using Azure;
using Azure.Data.Tables;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

public class AliasService : IAliasService
{
    private readonly TableClient _tableClient;

    public AliasService(TableClient tableClient)
    {
        _tableClient = tableClient;
    }

    public async Task<AliasEntity?> GetAliasAsync(string name)
    {
        // A name that breaks the rule can't have been created, and may hold characters Table
        // Storage rejects in a key (a "%0A" in a notify URL arrives as a newline): unknown, not a
        // storage error.
        var rowKey = name.ToLowerInvariant();
        if (!AliasNames.IsValid(rowKey))
            return null;

        try
        {
            var response = await _tableClient.GetEntityAsync<AliasEntity>("alias", rowKey);
            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<AliasEntity>> GetAllAliasesAsync()
    {
        var aliases = new List<AliasEntity>();
        await foreach (var entity in _tableClient.QueryAsync<AliasEntity>(e => e.PartitionKey == "alias"))
        {
            aliases.Add(entity);
        }
        return aliases;
    }

    public async Task<AliasEntity> SetAliasAsync(string name, AliasEntity entity)
    {
        entity.PartitionKey = "alias";
        entity.RowKey = name.ToLowerInvariant();
        await _tableClient.UpsertEntityAsync(entity);
        return entity;
    }

    public async Task<bool> RemoveAliasAsync(string name)
    {
        var rowKey = name.ToLowerInvariant();

        // Check existence first to return false for missing aliases, rather than
        // catching the 404 RequestFailedException from DeleteEntityAsync.
        var existing = await GetAliasAsync(rowKey);
        if (existing == null)
            return false;

        await _tableClient.DeleteEntityAsync("alias", rowKey);
        return true;
    }
}
