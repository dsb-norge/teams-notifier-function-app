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
    public async Task Run_PurgesExpiredIdempotencyRecords()
    {
        var idempotency = new Mock<IIdempotencyService>();
        idempotency.Setup(s => s.PurgeExpiredAsync(It.IsAny<CancellationToken>())).ReturnsAsync(3);

        await new StorageCleanupFunction(idempotency.Object, NullLogger<StorageCleanupFunction>.Instance)
            .Run(new TimerInfo(), TestContext.Current.CancellationToken);

        idempotency.Verify(s => s.PurgeExpiredAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
