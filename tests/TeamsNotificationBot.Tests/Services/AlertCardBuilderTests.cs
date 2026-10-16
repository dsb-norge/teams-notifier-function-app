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

    [Fact]
    public void NullEssentials_AreLeftOut()
    {
        // A budget alert: no rule, severity, signal type or target.
        var alert = new CommonAlertPayload
        {
            SchemaId = "azureMonitorCommonAlertSchema",
            Data = new AlertData
            {
                Essentials = new AlertEssentials
                {
                    MonitorCondition = "Fired",
                    MonitoringService = "CostAlerts",
                    FiredDateTime = "2026-02-14T12:00:00Z",
                    Description = "   ",
                    AlertTargetIDs = [""]
                }
            }
        };

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        Assert.Equal("\ud83d\udea8 Azure Monitor Alert: CostAlerts", body[0].GetProperty("text").GetString());
        var titles = body[1].GetProperty("facts").EnumerateArray().Select(f => f.GetProperty("title").GetString());
        Assert.Equal(["Condition", "Fired", "Monitoring Service"], titles);
        Assert.Equal(2, body.GetArrayLength()); // no description, no target
    }

    [Fact]
    public void NoEssentials_GiveATitleOnly()
    {
        var alert = new CommonAlertPayload { SchemaId = "azureMonitorCommonAlertSchema" };

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        // A null monitor condition counts as firing, and nothing is left to put in a FactSet.
        Assert.Equal("\ud83d\udea8 Azure Monitor Alert", Assert.Single(body.EnumerateArray()).GetProperty("text").GetString());
    }

    [Fact]
    public void TargetWithTrailingSlash_ShowsTheResourceName()
    {
        var alert = CreateAlert(targetResourceId: "/subscriptions/sub-1/resourceGroups/rg-1/providers/Microsoft.Web/sites/func-test/");

        var body = JsonDocument.Parse(AlertCardBuilder.Build(alert)).RootElement.GetProperty("body");

        Assert.Equal("**Target:** func-test", body[2].GetProperty("text").GetString());
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
