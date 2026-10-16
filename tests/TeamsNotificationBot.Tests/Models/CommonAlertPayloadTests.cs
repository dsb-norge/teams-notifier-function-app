using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

/// <summary>
/// Holds <see cref="AlertEssentials"/> to the essentials fields Microsoft documents (the
/// table vendored in TestData/AzureMonitorAlerts/manifest.json) and to the fields its
/// samples send. A field that shows up on a refresh of the samples fails here until it is
/// modelled or added to <see cref="LeftOut"/> with a reason.
/// </summary>
public class CommonAlertPayloadTests
{
    /// <summary>Documented essentials fields the card deliberately doesn't use.</summary>
    private static readonly Dictionary<string, string> LeftOut = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alertContextVersion"] = "schema bookkeeping",
        ["essentialsVersion"] = "schema bookkeeping",
        ["alertId"] = "an ARM ID, not something to read",
        ["alertRuleID"] = "an ARM ID; the rule's name is on the card",
        ["originAlertId"] = "an internal ID of the monitoring service",
        ["configurationItems"] = "repeats the target in nearly every alert",
        ["investigationLink"] = "limited preview",
        ["resolvedDateTime"] = "documented for metric alerts only",
        ["targetResourceGroup"] = "part of the target's resource ID",
        ["targetResourceType"] = "part of the target's resource ID",
    };

    private static readonly HashSet<string> Modelled = typeof(AlertEssentials).GetProperties()
        .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EveryDocumentedEssentialsField_IsModelledOrLeftOut()
    {
        Assert.DoesNotContain(AzureMonitorAlertSamples.EssentialsFields, Undecided);
    }

    [Fact]
    public void EveryEssentialsFieldTheSamplesSend_IsModelledOrLeftOut()
    {
        var sent = AzureMonitorAlertSamples.Files
            .Select(row => JsonDocument.Parse(AzureMonitorAlertSamples.Payload(row.Data)).RootElement)
            .SelectMany(p => p.GetProperty("data").GetProperty("essentials").EnumerateObject())
            .Select(p => p.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain(sent, Undecided);
    }

    [Fact]
    public void ModelledAndLeftOutFields_AreDocumentedAndDisjoint()
    {
        var documented = AzureMonitorAlertSamples.EssentialsFields.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A typo in a JsonPropertyName would leave the property silently empty.
        Assert.DoesNotContain(Modelled, f => !documented.Contains(f));
        Assert.DoesNotContain(LeftOut.Keys, f => !documented.Contains(f));
        Assert.DoesNotContain(LeftOut.Keys, Modelled.Contains);
    }

    private static bool Undecided(string field) => !Modelled.Contains(field) && !LeftOut.ContainsKey(field);
}
