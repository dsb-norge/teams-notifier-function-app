using Azure;
using Azure.Data.Tables;

namespace TeamsNotificationBot.Services;

public class IdempotencyService : IIdempotencyService
{
    private readonly TableClient _tableClient;

    public IdempotencyService(TableClient tableClient)
    {
        _tableClient = tableClient;
    }

    public async Task<IdempotencyResult?> GetAsync(string scope, string key)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(scope, key);
            var entity = response.Value;
            return new IdempotencyResult
            {
                StatusCode = entity.GetInt32("StatusCode") ?? 0,
                ResponseBody = entity.GetString("ResponseBody") ?? ""
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task SetAsync(string scope, string key, int statusCode, string responseBody)
    {
        var entity = new TableEntity(scope, key)
        {
            ["StatusCode"] = statusCode,
            ["ResponseBody"] = responseBody,
            ["CreatedAt"] = DateTimeOffset.UtcNow
        };

        await _tableClient.UpsertEntityAsync(entity);
    }
}
