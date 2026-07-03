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

    // F9: the ASP.NET Core hosting pipeline logs the full request URL in a TraceTelemetry message
    // ("Request starting/finished HTTP/1.1 POST <url>"), which the request-only path missed.
    [Fact]
    public void RedactsTokenInTraceMessage()
    {
        var trace = new TraceTelemetry(
            "Request finished HTTP/1.1 POST https://func.example.net/api/v1/ingest/updown/SECRETTOK99 - 200");

        _init.Initialize(trace);

        Assert.DoesNotContain("SECRETTOK99", trace.Message);
        Assert.Contains("/api/v1/ingest/updown/***", trace.Message);
    }

    [Fact]
    public void LeavesOtherTraceMessagesUnchanged()
    {
        var trace = new TraceTelemetry("updown webhook received. WebhookId=abc12345, SourceIp=1.2.3.4");

        _init.Initialize(trace);

        Assert.Equal("updown webhook received. WebhookId=abc12345, SourceIp=1.2.3.4", trace.Message);
    }

    [Fact]
    public void RedactsTokenInCustomProperties()
    {
        // ASP.NET Core hosting attaches the request path as a scope property.
        var trace = new TraceTelemetry("Request starting");
        trace.Properties["RequestPath"] = "/api/v1/ingest/updown/PROPSECRET42";
        trace.Properties["Unrelated"] = "keep-me";

        _init.Initialize(trace);

        Assert.DoesNotContain("PROPSECRET42", trace.Properties["RequestPath"]);
        Assert.Equal("/api/v1/ingest/updown/***", trace.Properties["RequestPath"]);
        Assert.Equal("keep-me", trace.Properties["Unrelated"]);
    }

    [Fact]
    public void RedactsTokenInRequestTelemetryProperties()
    {
        var req = new RequestTelemetry
        {
            Url = new Uri("https://func.example.net/api/v1/ingest/updown/URLSECRET")
        };
        req.Properties["RequestPath"] = "/api/v1/ingest/updown/PROPSECRET";

        _init.Initialize(req);

        Assert.DoesNotContain("URLSECRET", req.Url!.ToString());
        Assert.DoesNotContain("PROPSECRET", req.Properties["RequestPath"]);
    }
}
