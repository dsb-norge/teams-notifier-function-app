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

    private const string ModeVar = "UpdownWebhook__IpFilterMode";

    [Fact]
    public void IpFilterMode_DefaultsToEnforce_WhenUnset()
    {
        var prev = Environment.GetEnvironmentVariable(ModeVar);
        try
        {
            Environment.SetEnvironmentVariable(ModeVar, null);
            // Secure by default — a deployment enforces the source-IP allowlist unless deliberately loosened.
            Assert.Equal("enforce", UpdownWebhookConfig.IpFilterMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModeVar, prev);
        }
    }

    [Theory]
    [InlineData("off", "off")]
    [InlineData("log-only", "log-only")]
    [InlineData("enforce", "enforce")]
    [InlineData("ENFORCE", "enforce")]     // case-insensitive
    [InlineData("  off  ", "off")]         // trimmed
    [InlineData("bogus", "enforce")]       // invalid → secure default
    [InlineData("", "enforce")]            // empty → secure default
    public void IpFilterMode_HonorsValidValues_ElseSecureDefault(string envValue, string expected)
    {
        var prev = Environment.GetEnvironmentVariable(ModeVar);
        try
        {
            Environment.SetEnvironmentVariable(ModeVar, envValue);
            Assert.Equal(expected, UpdownWebhookConfig.IpFilterMode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ModeVar, prev);
        }
    }
}
