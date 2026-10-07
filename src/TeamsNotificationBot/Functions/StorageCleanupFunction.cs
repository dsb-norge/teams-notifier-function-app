using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Functions;

/// <summary>
/// Daily purge of expired records. Expiry is enforced when a record is read, so this only keeps
/// the tables from growing; a run that fails is simply caught up by the next one.
/// </summary>
public class StorageCleanupFunction
{
    private readonly IIdempotencyService _idempotencyService;
    private readonly ILogger<StorageCleanupFunction> _logger;

    public StorageCleanupFunction(IIdempotencyService idempotencyService, ILogger<StorageCleanupFunction> logger)
    {
        _idempotencyService = idempotencyService;
        _logger = logger;
    }

    [Function("StorageCleanup")]
    public async Task Run([TimerTrigger("0 30 2 * * *")] TimerInfo timer, CancellationToken cancellationToken)
    {
        var purged = await _idempotencyService.PurgeExpiredAsync(cancellationToken);
        _logger.LogInformation("Storage cleanup purged {Count} expired idempotency records", purged);
    }
}
