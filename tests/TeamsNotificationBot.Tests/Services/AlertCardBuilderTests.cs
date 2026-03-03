using System.Text.Json;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class AlertCardBuilderTests
{
    [Theory]
    [InlineData("Sev0", "Attention")]
    [InlineData("Sev1", "Attention")]
    [InlineData("Sev2", "Warning")]
    [InlineData("Sev3", "Accent")]
    [InlineData("Sev4", "Good")]
    public void SeverityColorMapping(string severity, string expectedColor)
    {
        var alert = CreateAlert(severity: severity);

        var cardJson = AlertCardBuilder.Build(alert);

        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        var titleBlock = body[0];
        Assert.Equal(expectedColor, titleBlock.GetProperty("color").GetString());
    }

    [Fact]
    public void IncludesAlertRuleInFacts()
    {
        var alert = CreateAlert(alertRule: "HighMemoryUsage");

        var cardJson = AlertCardBuilder.Build(alert);

        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        var factSet = body[1];
        var facts = factSet.GetProperty("facts");

        var alertRuleFact = facts.EnumerateArray().First();
        Assert.Equal("Alert Rule", alertRuleFact.GetProperty("title").GetString());
        Assert.Equal("HighMemoryUsage", alertRuleFact.GetProperty("value").GetString());
    }

    [Fact]
    public void IncludesDescriptionWhenPresent()
    {
        var alert = CreateAlert(description: "CPU exceeded 95% for 5 minutes");

        var cardJson = AlertCardBuilder.Build(alert);

        Assert.Contains("CPU exceeded 95% for 5 minutes", cardJson);
    }

    [Fact]
    public void OmitsDescriptionWhenNull()
    {
        var alert = CreateAlert(description: null);

        var cardJson = AlertCardBuilder.Build(alert);

        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        // Should have title + factset only (no description text block, no target block)
        Assert.Equal(2, body.GetArrayLength());
    }

    [Fact]
    public void IncludesTargetResource()
    {
        var alert = CreateAlert(targetResourceId: "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Web/sites/func-test");

        var cardJson = AlertCardBuilder.Build(alert);

        Assert.Contains("func-test", cardJson);
    }

    [Fact]
    public void ResolvedConditionUsesCheckmarkEmoji()
    {
        var alert = CreateAlert(monitorCondition: "Resolved");

        var cardJson = AlertCardBuilder.Build(alert);

        // JsonSerializer escapes non-ASCII to \uXXXX — check for the escaped form
        Assert.Contains("\\u2705", cardJson);
    }

    private static CommonAlertPayload CreateAlert(
        string alertRule = "TestRule",
        string severity = "Sev2",
        string monitorCondition = "Fired",
        string? description = null,
        string? targetResourceId = null)
    {
        return new CommonAlertPayload
        {
            SchemaId = "azureMonitorCommonAlertSchema",
            Data = new AlertData
            {
                Essentials = new AlertEssentials
                {
                    AlertRule = alertRule,
                    Severity = severity,
                    MonitorCondition = monitorCondition,
                    SignalType = "Metric",
                    FiredDateTime = "2026-02-14T12:00:00Z",
                    Description = description,
                    AlertTargetIDs = targetResourceId != null ? [targetResourceId] : null,
                    MonitoringService = "Platform"
                }
            }
        };
    }
}
