using System.Text.Json;
using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class UpdownWebhookPayloadTests
{
    private static List<UpdownEvent>? Parse(string json) =>
        JsonSerializer.Deserialize<List<UpdownEvent>>(json);

    [Fact]
    public void CheckDown_ParsesAllFields()
    {
        var events = Parse(UpdownPayloads.CheckDown);

        Assert.NotNull(events);
        var e = Assert.Single(events);
        Assert.Equal("check.down", e.Event);
        Assert.Equal("2026-07-01T10:48:48Z", e.Time);
        Assert.Equal("https://updown.io", e.Check?.Url);
        Assert.Equal("xyz0", e.Check?.Token);
        Assert.True(e.Check?.Down);
        Assert.Equal(418, e.Check?.LastStatus);
        Assert.Equal("2026-07-01T10:38:48Z", e.Downtime?.StartedAt);
        Assert.Equal("https://updown.io/downtimes/6a44f090706306086d4e09bc", e.Downtime?.DetailsUrl);
        Assert.Null(e.Downtime?.Duration);
    }

    [Fact]
    public void CheckUp_ParsesDurationAndAlias()
    {
        var e = Assert.Single(Parse(UpdownPayloads.CheckUp)!);
        Assert.Equal("check.up", e.Event);
        Assert.Equal("prod-site", e.Check?.Alias);
        Assert.False(e.Check?.Down);
        Assert.Equal(585, e.Downtime?.Duration);
        Assert.Equal("2026-07-01T10:48:33Z", e.Downtime?.EndedAt);
    }

    [Fact]
    public void SslInvalid_ParsesCertAndError()
    {
        var e = Assert.Single(Parse(UpdownPayloads.SslInvalid)!);
        Assert.Equal("check.ssl_invalid", e.Event);
        Assert.Equal("updown.io", e.Ssl?.Cert?.Subject);
        Assert.Contains("Let's Encrypt", e.Ssl?.Cert?.Issuer);
        Assert.Contains("error code 20", e.Ssl?.Error);
    }

    [Fact]
    public void SslValid_ParsesCert()
    {
        var e = Assert.Single(Parse(UpdownPayloads.SslValid)!);
        Assert.Equal("check.ssl_valid", e.Event);
        Assert.Equal("2018-12-07T21:00:18Z", e.Ssl?.Cert?.To);
        Assert.Null(e.Ssl?.Error);
    }

    [Fact]
    public void SslExpiration_ParsesDaysBeforeExpiration()
    {
        var e = Assert.Single(Parse(UpdownPayloads.SslExpiration)!);
        Assert.Equal("check.ssl_expiration", e.Event);
        Assert.Equal(7, e.Ssl?.DaysBeforeExpiration);
        Assert.Equal("2018-12-07T21:00:18Z", e.Ssl?.Cert?.To);
    }

    [Fact]
    public void SslRenewed_ParsesNewAndOldCert()
    {
        var e = Assert.Single(Parse(UpdownPayloads.SslRenewed)!);
        Assert.Equal("check.ssl_renewed", e.Event);
        Assert.Equal("2019-03-07T21:00:18Z", e.Ssl?.NewCert?.To);
        Assert.Equal("2018-12-07T21:00:18Z", e.Ssl?.OldCert?.To);
    }

    [Fact]
    public void PerformanceDrop_ParsesApdexAndIgnoresUnknownLastMetrics()
    {
        var e = Assert.Single(Parse(UpdownPayloads.PerformanceDrop)!);
        Assert.Equal("check.performance_drop", e.Event);
        Assert.Equal("47%", e.ApdexDropped);
    }

    [Fact]
    public void UnknownEvent_ParsesWithoutThrowing()
    {
        var e = Assert.Single(Parse(UpdownPayloads.UnknownEvent)!);
        Assert.Equal("check.some_future_thing", e.Event);
        Assert.Equal("xyz0", e.Check?.Token);
    }

    [Fact]
    public void NullsEverywhere_ParsesWithoutThrowing()
    {
        var e = Assert.Single(Parse(UpdownPayloads.NullsEverywhere)!);
        Assert.Equal("check.down", e.Event);
        Assert.Null(e.Check);
        Assert.Null(e.Time);
    }

    [Fact]
    public void EmptyArray_ParsesToEmptyList()
    {
        var events = Parse(UpdownPayloads.EmptyArray);
        Assert.NotNull(events);
        Assert.Empty(events);
    }

    [Fact]
    public void NotAnArray_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => Parse(UpdownPayloads.NotAnArray));
    }

    [Fact]
    public void Malformed_ThrowsJsonException()
    {
        Assert.Throws<JsonException>(() => Parse(UpdownPayloads.Malformed));
    }

    [Fact]
    public void MultipleEvents_AllParse()
    {
        var json = "[" +
            UpdownPayloads.CheckDown.Trim().TrimStart('[').TrimEnd(']') + "," +
            UpdownPayloads.CheckUp.Trim().TrimStart('[').TrimEnd(']') + "]";

        var events = Parse(json);
        Assert.NotNull(events);
        Assert.Equal(2, events.Count);
        Assert.Equal("check.down", events[0].Event);
        Assert.Equal("check.up", events[1].Event);
    }
}
