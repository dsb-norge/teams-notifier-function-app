using System.Text.Json;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Turns a JSON section of an Azure Monitor alert (<c>customProperties</c>, <c>alertContext</c>)
/// into title/value facts for a card, without knowing what the alert is about: the rules
/// below are about the shapes JSON takes in the common alert schema, never about one alert
/// type. Titles are the field's path (<c>condition.allOf.metricValue</c>), values its text.
/// <list type="bullet">
/// <item>A string that holds JSON is unpacked, a few levels deep. The schema does this a lot:
/// an activity log event's <c>properties.statusMessage</c>, <c>claims</c>, a policy event's
/// <c>policies</c> and Service Health's <c>impactedServices</c> are all escaped JSON.</item>
/// <item>A list of <c>{ name, value }</c> objects (metric and log dimensions, a simple log
/// alert's <c>event</c> rows) becomes one fact per name.</item>
/// <item>A list of plain values becomes one fact; a list with one item doesn't get an index.</item>
/// <item>Nulls, empty strings and empty lists or objects say nothing and are left out.</item>
/// </list>
/// Values are returned whole and unformatted: truncating, escaping and linking them is the
/// card's business, since a link can't be cut short.
/// </summary>
public static class AlertFacts
{
    /// <summary>How many levels of JSON-in-a-string are unpacked.</summary>
    private const int MaxDecodeDepth = 3;

    public readonly record struct Fact(string Title, string Value);

    /// <param name="section">The JSON to flatten; null or a JSON null gives no facts.</param>
    /// <param name="skip">Property names to leave out, at any depth.</param>
    public static List<Fact> Flatten(JsonElement? section, Func<string, bool>? skip = null)
    {
        var facts = new List<Fact>();
        if (section is { } element)
            Walk(element, "", MaxDecodeDepth, skip, facts);
        return facts;
    }

    private static void Walk(JsonElement element, string path, int decodes, Func<string, bool>? skip, List<Fact> facts)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject().Where(p => !Skipped(skip, p.Name)))
                    Walk(property.Value, Join(path, property.Name), decodes, skip, facts);
                break;

            case JsonValueKind.Array:
                WalkArray(element, path, decodes, skip, facts);
                break;

            case JsonValueKind.String:
                var text = element.GetString()!;
                if (decodes > 0 && TryParseEmbedded(text, out var embedded))
                    Walk(embedded, path, decodes - 1, skip, facts);
                else
                    Add(facts, path, text);
                break;

            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                Add(facts, path, element.GetRawText());
                break;

            // Null and Undefined: nothing to show.
        }
    }

    private static void WalkArray(JsonElement array, string path, int decodes, Func<string, bool>? skip, List<Fact> facts)
    {
        var items = array.EnumerateArray()
            .Where(i => i.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .ToList();

        if (items.Count == 0)
            return;

        // Plain values join into one fact; a string holding JSON is walked like any other item,
        // so it is unpacked and its hidden fields are left out.
        if (items.TrueForAll(i => IsScalar(i) && !(decodes > 0 && i.ValueKind == JsonValueKind.String && TryParseEmbedded(i.GetString()!, out _))))
        {
            Add(facts, path, string.Join(", ", items.Select(ScalarText).Where(t => !string.IsNullOrWhiteSpace(t))));
            return;
        }

        // The name is the field's name here, so it is as subject to skip as a property's.
        if (items.TrueForAll(i => NameOf(i) is not null))
        {
            foreach (var (name, value) in items.Select(i => (NameOf(i)!, ValueOf(i))).Where(pair => !Skipped(skip, pair.Item1)))
                Walk(value, Join(path, name), decodes, skip, facts);
            return;
        }

        if (items.Count == 1)
        {
            Walk(items[0], path, decodes, skip, facts);
            return;
        }

        for (var i = 0; i < items.Count; i++)
            Walk(items[i], $"{path}[{i}]", decodes, skip, facts);
    }

    private static bool Skipped(Func<string, bool>? skip, string name) => skip?.Invoke(name) == true;

    private static bool IsScalar(JsonElement e) =>
        e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False;

    private static string ScalarText(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText();

    /// <summary>The name of a <c>{ name, value }</c> object, or null for anything else.</summary>
    private static string? NameOf(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
            return null;

        string? name = null;
        var count = 0;
        var hasValue = false;
        foreach (var property in e.EnumerateObject())
        {
            count++;
            if (property.NameEquals("name") && property.Value.ValueKind == JsonValueKind.String)
                name = property.Value.GetString();
            else if (property.NameEquals("value"))
                hasValue = true;
        }
        return count == 2 && hasValue && !string.IsNullOrWhiteSpace(name) ? name : null;
    }

    private static JsonElement ValueOf(JsonElement pair) => pair.GetProperty("value");

    private static bool TryParseEmbedded(string text, out JsonElement element)
    {
        element = default;
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length < 2 ||
            !((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']')))
            return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false; // text that only looks like JSON stays text
        }
    }

    private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

    private static void Add(List<Fact> facts, string path, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            facts.Add(new Fact(path.Length == 0 ? "value" : path, value));
    }
}
