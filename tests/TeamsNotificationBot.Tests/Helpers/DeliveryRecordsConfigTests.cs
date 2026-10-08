using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

// Joins the "Azurite" collection only to serialize with the other classes that read or set
// process-wide environment variables.
[Collection("Azurite")]
public class DeliveryRecordsConfigTests : IDisposable
{
    private const string Setting = "DeliveryRecords__RetentionDays";

    public void Dispose() => Environment.SetEnvironmentVariable(Setting, null);

    [Theory]
    [InlineData(null, 180)]
    [InlineData("", 180)]
    [InlineData("not-a-number", 180)]
    [InlineData("0", 180)]
    [InlineData("-1", 180)]
    [InlineData("30", 30)]
    [InlineData("99999", 3650)] // clamped to ten years
    public void Retention_ComesFromTheSetting_WithASafeDefault(string? value, int expectedDays)
    {
        Environment.SetEnvironmentVariable(Setting, value);

        Assert.Equal(TimeSpan.FromDays(expectedDays), DeliveryRecordsConfig.Retention);
    }
}
