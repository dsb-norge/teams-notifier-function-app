using System.Text.Json;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class UpdownCardBuilderTests
{
    private static UpdownEvent First(string json) =>
        JsonSerializer.Deserialize<List<UpdownEvent>>(json)![0];

    private static (JsonElement root, string raw) BuildCard(string fixture, string? account = null)
    {
        var raw = UpdownCardBuilder.Build(First(fixture), account);
        return (JsonDocument.Parse(raw).RootElement, raw);
    }

    private static void AssertValidatorClean(JsonElement card)
    {
        var (isValid, error) = AdaptiveCardValidator.Validate(card);
        Assert.True(isValid, error);
    }

    [Theory]
    [InlineData(nameof(UpdownPayloads.CheckDown))]
    [InlineData(nameof(UpdownPayloads.CheckUp))]
    [InlineData(nameof(UpdownPayloads.SslInvalid))]
    [InlineData(nameof(UpdownPayloads.SslValid))]
    [InlineData(nameof(UpdownPayloads.SslExpiration))]
    [InlineData(nameof(UpdownPayloads.SslRenewed))]
    [InlineData(nameof(UpdownPayloads.PerformanceDrop))]
    public void EveryEvent_ProducesValidatorCleanCard(string fixtureName)
    {
        var fixture = (string)typeof(UpdownPayloads).GetField(fixtureName)!.GetValue(null)!;
        var (root, _) = BuildCard(fixture);

        Assert.Equal("AdaptiveCard", root.GetProperty("type").GetString());
        AssertValidatorClean(root);
    }

    [Fact]
    public void CheckDown_IsRed_WithReasonAndDownSince()
    {
        var (_, raw) = BuildCard(UpdownPayloads.CheckDown);
        Assert.Contains("Attention", raw);
        Assert.Contains("DOWN", raw);
        Assert.Contains("418", raw);                    // reason (apostrophe is JSON-escaped)
        Assert.Contains("teapot", raw);
        Assert.Contains("2026-07-01T10:38:48Z", raw);   // down since
        Assert.Contains("https://updown.io", raw);      // check url as text
    }

    [Fact]
    public void CheckUp_IsGreen_WithHumanizedDurationAndUpdownLink()
    {
        var (_, raw) = BuildCard(UpdownPayloads.CheckUp);
        Assert.Contains("Good", raw);
        Assert.Contains("9 minutes", raw);              // 585s → "9 minutes" (585/60 = 9)
        Assert.Contains("[View downtime details on updown.io](https://updown.io/downtimes/", raw);
    }

    [Fact]
    public void CheckUp_NonUpdownDetailsUrl_IsNotLinkified()
    {
        var (_, raw) = BuildCard(UpdownPayloads.CheckUpEvilLink);
        Assert.DoesNotContain("evil.example.com", raw);
        Assert.DoesNotContain("View downtime details", raw);
    }

    [Fact]
    public void SslExpiration_IsWarning_WithDaysAndExpiry()
    {
        var (_, raw) = BuildCard(UpdownPayloads.SslExpiration);
        Assert.Contains("Warning", raw);
        Assert.Contains("Days to expiry", raw);
        Assert.Contains("2018-12-07T21:00:18Z", raw);
    }

    [Fact]
    public void SslInvalid_ShowsError()
    {
        var (_, raw) = BuildCard(UpdownPayloads.SslInvalid);
        Assert.Contains("Attention", raw);
        Assert.Contains("error code 20", raw);
    }

    [Fact]
    public void SslRenewed_ShowsNewCertExpiry()
    {
        var (_, raw) = BuildCard(UpdownPayloads.SslRenewed);
        Assert.Contains("2019-03-07T21:00:18Z", raw);
    }

    [Fact]
    public void PerformanceDrop_ShowsApdex()
    {
        var (_, raw) = BuildCard(UpdownPayloads.PerformanceDrop);
        Assert.Contains("47%", raw);
    }

    [Fact]
    public void AccountLabel_WhenProvided_AppearsOnCard()
    {
        var (_, raw) = BuildCard(UpdownPayloads.CheckDown, "prod-monitoring / ops@dsb.no");
        Assert.Contains("prod-monitoring / ops@dsb.no", raw);
        Assert.Contains("updown account", raw);
    }

    [Fact]
    public void AllCards_CarryUnverifiedSenderFooter()
    {
        var (_, raw) = BuildCard(UpdownPayloads.CheckDown);
        Assert.Contains("unverified sender", raw);
    }

    [Fact]
    public void NullFields_DoNotThrow_AndOmitMissingFacts()
    {
        // check.down with check == null, time == null, no downtime
        var (root, raw) = BuildCard(UpdownPayloads.NullsEverywhere);
        AssertValidatorClean(root);
        Assert.DoesNotContain("null", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(30, "30 seconds")]
    [InlineData(1, "1 second")]
    [InlineData(60, "1 minute")]
    [InlineData(585, "9 minutes")]
    [InlineData(3600, "1 hour")]
    [InlineData(3900, "1h 5m")]
    [InlineData(86400, "1 day")]
    [InlineData(90000, "1d 1h")]
    public void HumanizeDuration_FormatsExpected(long seconds, string expected)
    {
        Assert.Equal(expected, UpdownCardBuilder.HumanizeDuration(seconds));
    }

    [Fact]
    public void HumanizeDuration_NullOrNegative_ReturnsNull()
    {
        Assert.Null(UpdownCardBuilder.HumanizeDuration(null));
        Assert.Null(UpdownCardBuilder.HumanizeDuration(-5));
    }

    [Theory]
    [InlineData("https://updown.io/downtimes/abc", true)]
    [InlineData("https://updown.io/", true)]
    [InlineData("https://evil.example.com/x", false)]
    [InlineData("http://updown.io/x", false)]
    [InlineData("https://updown.io.evil.com/x", false)]
    [InlineData(null, false)]
    public void IsUpdownUrl_DomainGates(string? url, bool expected)
    {
        Assert.Equal(expected, UpdownCardBuilder.IsUpdownUrl(url));
    }
}
