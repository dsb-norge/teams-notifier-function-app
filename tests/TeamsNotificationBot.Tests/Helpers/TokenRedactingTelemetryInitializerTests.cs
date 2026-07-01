using Microsoft.ApplicationInsights.DataContracts;
using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

public class TokenRedactingTelemetryInitializerTests
{
    private readonly TokenRedactingTelemetryInitializer _init = new();

    [Fact]
    public void RedactsTokenInUrl()
    {
        var req = new RequestTelemetry
        {
            Url = new Uri("https://func.example.net/api/v1/ingest/updown/SUPERSECRETTOKEN123")
        };

        _init.Initialize(req);

        Assert.Equal("https://func.example.net/api/v1/ingest/updown/***", req.Url!.ToString());
        Assert.DoesNotContain("SUPERSECRETTOKEN123", req.Url.ToString());
    }

    [Fact]
    public void RedactsTokenInName()
    {
        var req = new RequestTelemetry { Name = "POST /api/v1/ingest/updown/abc123def456" };

        _init.Initialize(req);

        Assert.Equal("POST /api/v1/ingest/updown/***", req.Name);
    }

    [Fact]
    public void RedactsTokenWithQueryString()
    {
        var req = new RequestTelemetry
        {
            Url = new Uri("https://func.example.net/api/v1/ingest/updown/tok123?x=1")
        };

        _init.Initialize(req);

        Assert.DoesNotContain("tok123", req.Url!.ToString());
        Assert.Contains("***", req.Url.ToString());
    }

    [Fact]
    public void LeavesOtherUrlsUnchanged()
    {
        var req = new RequestTelemetry
        {
            Url = new Uri("https://func.example.net/api/v1/notify/ops-alerts")
        };

        _init.Initialize(req);

        Assert.Equal("https://func.example.net/api/v1/notify/ops-alerts", req.Url!.ToString());
    }
}
