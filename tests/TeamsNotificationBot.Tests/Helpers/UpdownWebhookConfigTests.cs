using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

// Methods in one xunit class run sequentially, and no other test touches this env var, so the
// set/restore below is race-free.
public class UpdownWebhookConfigTests
{
    private const string EnvVar = "UpdownWebhook__IpAllowlistMaxAgeHours";

    [Fact]
    public void AllowlistMaxAge_DefaultsTo48h_WhenUnset()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, null);
            Assert.Equal(48, UpdownWebhookConfig.AllowlistMaxAgeHours);
            Assert.Equal(TimeSpan.FromHours(48), UpdownWebhookConfig.AllowlistMaxAge);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }

    [Theory]
    [InlineData("12", 12)]        // honored
    [InlineData("100000", 8760)]  // clamped to 1 year
    [InlineData("0", 48)]         // non-positive → default
    [InlineData("-5", 48)]        // negative → default
    [InlineData("abc", 48)]       // unparseable → default
    public void AllowlistMaxAgeHours_HonorsEnvWithClampAndDefault(string envValue, int expected)
    {
        var prev = Environment.GetEnvironmentVariable(EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EnvVar, envValue);
            Assert.Equal(expected, UpdownWebhookConfig.AllowlistMaxAgeHours);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVar, prev);
        }
    }
}
