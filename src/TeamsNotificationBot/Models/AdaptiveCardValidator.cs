using System.Text.Json;

namespace TeamsNotificationBot.Models;

public static class AdaptiveCardValidator
{
    private static readonly HashSet<string> ProhibitedActionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Action.OpenUrl",
        "Action.Submit",
        "Action.Execute"
    };

    public static (bool IsValid, string? Error) Validate(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object)
            return (false, "Adaptive Card must be a JSON object.");

        if (!card.TryGetProperty("type", out var typeElement) ||
            typeElement.GetString() != "AdaptiveCard")
            return (false, "Adaptive Card must have 'type' set to 'AdaptiveCard'.");

        if (!card.TryGetProperty("version", out _))
            return (false, "Adaptive Card must have a 'version' field.");

        if (!card.TryGetProperty("body", out var bodyElement) ||
            bodyElement.ValueKind != JsonValueKind.Array)
            return (false, "Adaptive Card must have a 'body' array.");

        var prohibitedError = FindProhibitedElements(card);
        if (prohibitedError != null)
            return (false, prohibitedError);

        return (true, null);
    }

    private static string? FindProhibitedElements(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("type", out var typeValue) &&
                    typeValue.ValueKind == JsonValueKind.String)
                {
                    var typeName = typeValue.GetString();
                    if (typeName != null && ProhibitedActionTypes.Contains(typeName))
                        return $"Prohibited element '{typeName}' found. Actions that open URLs, submit data, or execute code are not allowed.";

                    if (typeName == "Image" && element.TryGetProperty("url", out var urlValue) &&
                        urlValue.ValueKind == JsonValueKind.String)
                    {
                        var url = urlValue.GetString();
                        if (url != null && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                            url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                            return $"External image URLs are not allowed. Found: '{url}'. Use data URIs or remove the image.";
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    var error = FindProhibitedElements(property.Value);
                    if (error != null)
                        return error;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var error = FindProhibitedElements(item);
                    if (error != null)
                        return error;
                }
                break;
        }

        return null;
    }
}
