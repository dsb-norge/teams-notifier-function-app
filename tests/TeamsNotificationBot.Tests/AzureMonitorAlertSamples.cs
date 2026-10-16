using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace TeamsNotificationBot.Tests;

/// <summary>
/// Microsoft's published common alert schema samples, vendored verbatim under
/// TestData/AzureMonitorAlerts by scripts/fetch-alert-samples.py (see the README there).
/// Microsoft publishes no usable machine-readable schema, so these samples and the
/// documented essentials field list are what the alert tests run against. A sample
/// that is only an <c>alertContext</c> fragment is wrapped in the essentials of the
/// docs' generic sample payload, so every sample can go through the whole pipeline.
/// </summary>
public static class AzureMonitorAlertSamples
{
    private static readonly string Root =
        Path.Join(AppContext.BaseDirectory, "TestData", "AzureMonitorAlerts");

    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllText(Path.Join(Root, "manifest.json"))).RootElement);

    /// <summary>The sample files, relative to the TestData folder.</summary>
    public static TheoryData<string> Files
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var sample in Manifest.Value.GetProperty("samples").EnumerateArray())
                data.Add(sample.GetProperty("file").GetString()!);
            return data;
        }
    }

    /// <summary>The field names of the docs' "Essentials fields" table.</summary>
    public static IReadOnlyList<string> EssentialsFields =>
        Manifest.Value.GetProperty("essentialsFields").EnumerateArray()
            .Select(f => f.GetString()!).ToList();

    /// <summary>
    /// The sample as a complete common alert schema payload: verbatim when the docs give a
    /// whole payload, otherwise the fragment's alertContext inside the generic sample.
    /// </summary>
    public static string Payload(string file)
    {
        var sample = JsonNode.Parse(File.ReadAllText(Path.Join(Root, file)))!.AsObject();
        if (sample.ContainsKey("schemaId"))
            return sample.ToJsonString();

        var envelope = JsonNode.Parse(File.ReadAllText(
            Path.Join(Root, "samples", "common-schema", "alert-payload.json")))!.AsObject();
        envelope["data"]!["alertContext"] = sample["alertContext"]!.DeepClone();
        return envelope.ToJsonString();
    }
}
