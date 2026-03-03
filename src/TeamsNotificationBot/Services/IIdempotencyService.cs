namespace TeamsNotificationBot.Services;

public interface IIdempotencyService
{
    Task<IdempotencyResult?> GetAsync(string scope, string key);
    Task SetAsync(string scope, string key, int statusCode, string responseBody);
}

public class IdempotencyResult
{
    public int StatusCode { get; set; }
    public string ResponseBody { get; set; } = string.Empty;
}
