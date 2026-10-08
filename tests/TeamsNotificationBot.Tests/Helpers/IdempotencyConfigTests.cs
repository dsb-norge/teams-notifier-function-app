using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

// Joins the "Azurite" collection only to serialize with the other classes that read or set
// process-wide environment variables.
[Collection("Azurite")]
public class IdempotencyConfigTests : IDisposable
{
    private const string Setting = "Idempotency__ExpiryHours";

    public void Dispose() => Environment.SetEnvironmentVariable(Setting, null);

    [Theory]
    [InlineData(null, 168)]
    [InlineData("", 168)]
    [InlineData("not-a-number", 168)]
    [InlineData("0", 168)]
    [InlineData("-5", 168)]
    [InlineData("24", 24)]
    [InlineData("99999", 8760)] // clamped to a year
    public void Expiry_ComesFromTheSetting_WithASafeDefault(string? value, int expectedHours)
    {
        Environment.SetEnvironmentVariable(Setting, value);

        Assert.Equal(TimeSpan.FromHours(expectedHours), IdempotencyConfig.Expiry);
    }
}
