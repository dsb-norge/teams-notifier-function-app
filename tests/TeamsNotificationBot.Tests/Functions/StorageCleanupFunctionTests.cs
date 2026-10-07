using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class StorageCleanupFunctionTests
{
    [Fact]
    public async Task Run_PurgesExpiredIdempotencyAndDeliveryRecords()
    {
        var idempotency = new Mock<IIdempotencyService>();
        var deliveries = new Mock<IDeliveryRecords>();

        await new StorageCleanupFunction(idempotency.Object, deliveries.Object, NullLogger<StorageCleanupFunction>.Instance)
            .Run(new TimerInfo(), TestContext.Current.CancellationToken);

        idempotency.Verify(s => s.PurgeExpiredAsync(It.IsAny<CancellationToken>()), Times.Once);
        deliveries.Verify(s => s.PurgeExpiredAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
