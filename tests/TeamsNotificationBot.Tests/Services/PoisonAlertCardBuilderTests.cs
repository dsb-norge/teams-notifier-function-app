using System.Text.Json;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class PoisonAlertCardBuilderTests
{
    [Fact]
    public void Build_ReturnsValidAdaptiveCard()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", "test message", null);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("AdaptiveCard", root.GetProperty("type").GetString());
        Assert.Equal("1.4", root.GetProperty("version").GetString());
        Assert.True(root.GetProperty("body").GetArrayLength() > 0);
    }

    [Fact]
    public void Build_ContainsPoisonQueueAlertTitle()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", null, null);
        Assert.Contains("Poison Queue Alert", json);
    }

    [Fact]
    public void Build_ContainsSourceQueueInFacts()
    {
        var json = PoisonAlertCardBuilder.Build("botoperations-poison", null, null);
        Assert.Contains("botoperations-poison", json);
    }

    [Fact]
    public void Build_WithEnqueuedTime_IncludesOriginallyEnqueued()
    {
        var time = new DateTimeOffset(2026, 2, 27, 10, 30, 0, TimeSpan.Zero);
        var json = PoisonAlertCardBuilder.Build("notifications-poison", null, time);
        Assert.Contains("Originally Enqueued", json);
        Assert.Contains("2026-02-27", json);
    }

    [Fact]
    public void Build_WithoutEnqueuedTime_OmitsOriginallyEnqueued()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", null, null);
        Assert.DoesNotContain("Originally Enqueued", json);
    }

    [Fact]
    public void Build_WithMessageExcerpt_IncludesExcerpt()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", "{\"alias\":\"test\"}", null);
        Assert.Contains("Message Excerpt", json);
        // The message excerpt is embedded in JSON — verify the content is present
        Assert.Contains("alias", json);
        Assert.Contains("test", json);
    }

    [Fact]
    public void Build_WithNullExcerpt_OmitsExcerpt()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", null, null);
        Assert.DoesNotContain("Message Excerpt", json);
    }

    [Fact]
    public void Build_LongExcerpt_TruncatesAt500()
    {
        var longMessage = new string('x', 600);
        var json = PoisonAlertCardBuilder.Build("notifications-poison", longMessage, null);

        // Should contain truncated message (500 chars + "...")
        Assert.Contains("...", json);
        // Original 600-char string should not be present
        Assert.DoesNotContain(longMessage, json);
    }

    [Fact]
    public void Build_IncludesManagementHint()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", null, null);
        Assert.Contains("queue-status", json);
        Assert.Contains("queue-retry", json);
    }

    [Fact]
    public void Build_WithEmptyExcerpt_OmitsExcerpt()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", "", null);
        Assert.DoesNotContain("Message Excerpt", json);
    }

    [Fact]
    public void Build_Exactly500CharExcerpt_NoTruncation()
    {
        var exactMessage = new string('a', 500);
        var json = PoisonAlertCardBuilder.Build("notifications-poison", exactMessage, null);

        // 500 chars should not be truncated (condition is > 500)
        Assert.Contains("Message Excerpt", json);
        Assert.DoesNotContain("...", json);
    }

    [Fact]
    public void Build_501CharExcerpt_IsTruncated()
    {
        var message = new string('b', 501);
        var json = PoisonAlertCardBuilder.Build("notifications-poison", message, null);

        Assert.Contains("...", json);
    }

    [Fact]
    public void Build_DetectedAtTimestamp_IsPresent()
    {
        var json = PoisonAlertCardBuilder.Build("notifications-poison", null, null);
        Assert.Contains("Detected At", json);
    }
}
