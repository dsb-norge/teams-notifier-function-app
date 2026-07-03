using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class WebhookListCardBuilderTests
{
    private static WebhookDisplayInfo Sample() => new(
        Id: "abc12345",
        Source: "updown",
        TargetLabel: "personal chat",
        Description: "prod site health",
        UpdownAccount: "ops@dsb.no",
        EnabledEvents: "check.down, check.up",
        CreatedByName: "Tester",
        RelativeCreated: "just now",
        LastReceived: "never");

    [Fact]
    public void BuildSingle_IncludesIdAccountDescription_AndNoSecret()
    {
        var json = WebhookListCardBuilder.BuildSingle(Sample());

        Assert.Contains("AdaptiveCard", json);
        Assert.Contains("abc12345", json);
        Assert.Contains("ops@dsb.no", json);
        Assert.Contains("prod site health", json);
        Assert.Contains("check.down", json);
        // Single-webhook header, not the list header.
        Assert.Contains("Webhook", json);
        Assert.DoesNotContain("Webhooks (", json);
    }

    [Fact]
    public void Build_List_UsesPluralHeaderWithCount()
    {
        var json = WebhookListCardBuilder.Build([Sample(), Sample()]);

        Assert.Contains("Webhooks (2)", json);
        Assert.Contains("abc12345", json);
    }
}
