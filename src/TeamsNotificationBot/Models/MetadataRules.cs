using System.Text.RegularExpressions;

namespace TeamsNotificationBot.Models;

/// <summary>
/// Limits on the caller's <c>metadata</c>. Every entry is copied into the unsampled delivery
/// events (as <c>meta.&lt;key&gt;</c>), so the limits keep those bounded and the keys usable as
/// property names in KQL.
/// </summary>
public static partial class MetadataRules
{
    public const int MaxEntries = 10;
    public const int MaxKeyLength = 64;
    public const int MaxValueLength = 256;

    // \z, not $: in .NET, $ also matches before a final newline, which would let "run\n" through.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex KeyPattern();

    /// <summary>Returns null when <paramref name="metadata"/> is absent or valid, else the reason.</summary>
    public static string? Validate(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata == null)
            return null;

        if (metadata.Count > MaxEntries)
            return $"metadata has {metadata.Count} entries; at most {MaxEntries} are allowed.";

        foreach (var (key, value) in metadata)
        {
            if (!KeyPattern().IsMatch(key))
                return $"metadata key '{Truncate(key)}' is invalid. Keys are 1-{MaxKeyLength} characters " +
                       "of letters, digits, '.', '_' and '-'.";

            // System.Text.Json fills a JSON null into a non-nullable string value.
            if (value is null)
                return $"metadata value for '{key}' must be a string.";

            if (value.Length > MaxValueLength)
                return $"metadata value for '{key}' is {value.Length} characters; at most {MaxValueLength} are allowed.";
        }

        return null;
    }

    private static string Truncate(string key) => key.Length <= MaxKeyLength ? key : key[..MaxKeyLength] + "…";
}
