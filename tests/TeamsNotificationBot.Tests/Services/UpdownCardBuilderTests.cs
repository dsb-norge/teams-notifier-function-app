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
        Assert.Contains("2026-07-01 10:38:48 UTC", raw); // down since (F10: explicit UTC, not raw ISO)
        Assert.DoesNotContain("2026-07-01T10:38:48Z", raw); // raw ISO is reformatted, not passed through
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
        Assert.Contains("2018-12-07 21:00:18 UTC", raw); // F10: explicit UTC, not raw ISO
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
        Assert.Contains("2019-03-07 21:00:18 UTC", raw); // F10: explicit UTC, not raw ISO
    }

    [Theory]
    // F10: ISO-8601 → explicit, unambiguous UTC (Teams would otherwise auto-localize the bare ISO).
    [InlineData("2026-07-02T10:48:48Z", "2026-07-02 10:48:48 UTC")]
    [InlineData("2026-07-02T12:48:48+02:00", "2026-07-02 10:48:48 UTC")] // normalized to UTC
    public void FormatTimestamp_IsoBecomesExplicitUtc(string input, string expected)
    {
        Assert.Equal(expected, UpdownCardBuilder.FormatTimestamp(input));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("not a date", "not a date")] // unparseable → passthrough (graceful)
    public void FormatTimestamp_InvalidOrEmpty_ReturnsInput(string? input, string? expected)
    {
        Assert.Equal(expected, UpdownCardBuilder.FormatTimestamp(input));
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
