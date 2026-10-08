using System.Text.Json;
using System.Text.RegularExpressions;

namespace TeamsNotificationBot.Models;

/// <summary>
/// Rules for a notify request's <c>mentions</c>. The caller places each mention with
/// <c>&lt;at&gt;key&lt;/at&gt;</c>: every placement must name a declared key and every key must be
/// placed, so no orphaned <c>&lt;at&gt;</c> tag reaches Teams. An <c>&lt;at&gt;</c> tag without a
/// matching mention entity makes Teams shift the other mentions onto the wrong people. Placements
/// are matched in the raw text (a card's string values); HTML entities are not decoded first, so
/// <c>&amp;lt;at&amp;gt;</c> is just text.
/// </summary>
public static partial class MentionRules
{
    public const int MaxPeople = 20;
    public const int MaxTags = 10;

    /// <summary>
    /// Placements in all, a repeated key counting each time. Each one becomes a display name and a
    /// mention entity, so this bounds how far rendering can grow the message.
    /// </summary>
    public const int MaxPlacements = MaxPeople + MaxTags;
    public const int MaxKeyLength = 64;
    public const int MaxNameLength = 256;
    public const int MaxTagIdLength = 512;

    // \z, not $: in .NET, $ also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}\z")]
    private static partial Regex KeyPattern();

    /// <summary>A mention placement: exactly <c>&lt;at&gt;key&lt;/at&gt;</c>. Group 1 is the key.</summary>
    [GeneratedRegex(@"<at>([^<>]*)</at>")]
    internal static partial Regex Placement();

    // Any opening or closing at-tag, however it is written: <at>, </at>, <AT >, <at id="x">.
    [GeneratedRegex(@"<\s*/?\s*at\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AnyAtTag();

    // Printable ASCII without spaces: Graph's tag IDs are base64.
    [GeneratedRegex(@"^[\x21-\x7E]{1,512}\z")]
    private static partial Regex TagIdPattern();

    /// <summary>
    /// Null when <paramref name="mentions"/> is absent, empty, or valid for
    /// <paramref name="message"/> (<paramref name="format"/> <c>text</c> or <c>adaptive-card</c>);
    /// else the reason.
    /// </summary>
    public static string? Validate(IReadOnlyList<MessageMention?>? mentions, string format, JsonElement message)
    {
        if (mentions is not { Count: > 0 })
            return null;

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var people = 0;
        var tags = 0;
        for (var i = 0; i < mentions.Count; i++)
        {
            var mention = mentions[i];
            if (mention == null)
                return $"mentions[{i}] must be an object.";

            if (mention.Key is null || !KeyPattern().IsMatch(mention.Key))
                return $"mentions[{i}].key is invalid. Keys are 1-{MaxKeyLength} characters of letters, digits, '.', '_' and '-'.";
            if (!keys.Add(mention.Key))
                return $"Mention key '{mention.Key}' is declared more than once.";

            if ((mention.Id == null) == (mention.Tag == null))
                return $"Mention '{mention.Key}' needs exactly one of 'id' (a person) and 'tag'.";

            if (mention.Name != null && !IsValidName(mention.Name))
                return $"Mention '{mention.Key}' has an invalid name: 1-{MaxNameLength} characters, " +
                       "without '<', '>' or control characters.";

            if (mention.Id != null)
            {
                if (!PersonIds.IsValid(mention.Id))
                    return $"Mention '{mention.Key}': 'id' must be an Entra object ID (a GUID) or a UPN.";
                people++;
            }
            else
            {
                if (!TagIdPattern().IsMatch(mention.Tag!))
                    return $"Mention '{mention.Key}': 'tag' must be a tag ID of 1-{MaxTagIdLength} printable characters without spaces.";
                if (mention.Name == null)
                    return $"Mention '{mention.Key}': 'name' is required for a tag.";
                tags++;
            }
        }

        if (people > MaxPeople)
            return $"The message mentions {people} people; at most {MaxPeople} are allowed.";
        if (tags > MaxTags)
            return $"The message mentions {tags} tags; at most {MaxTags} are allowed.";

        if (format == "adaptive-card" && CheckCardEntities(message) is { } cardError)
            return cardError;

        return CheckPlacements(keys, format, message);
    }

    /// <summary>
    /// Null when the mentions can go to a conversation of <paramref name="targetType"/>
    /// (<c>channel</c>, <c>groupChat</c> or <c>personal</c>); else the reason.
    /// </summary>
    public static string? CheckTarget(IReadOnlyList<MessageMention?>? mentions, string? targetType)
    {
        if (mentions is not { Count: > 0 })
            return null;
        if (targetType == "personal")
            return "Mentions can't be used in a personal chat.";
        if (targetType != "channel" && mentions.Any(m => m?.Tag != null))
            return "Tag mentions can only be used in a channel.";
        return null;
    }

    private static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxNameLength &&
        !name.Any(c => c is '<' or '>' || char.IsControl(c));

    // The bot adds the mention entities to the card itself; entities of the caller's own would
    // collide with them, and the bot can't check them against the roster.
    private static string? CheckCardEntities(JsonElement card)
    {
        if (card.ValueKind != JsonValueKind.Object || !card.TryGetProperty("msteams", out var msteams))
            return null;
        if (msteams.ValueKind != JsonValueKind.Object)
            return "The card's 'msteams' must be an object when 'mentions' is used.";
        if (!msteams.TryGetProperty("entities", out var entities))
            return null;
        if (entities.ValueKind != JsonValueKind.Array)
            return "The card's 'msteams.entities' must be an array when 'mentions' is used.";

        foreach (var entity in entities.EnumerateArray())
        {
            if (entity.ValueKind == JsonValueKind.Object &&
                entity.TryGetProperty("type", out var type) &&
                type.ValueKind == JsonValueKind.String &&
                string.Equals(type.GetString(), "mention", StringComparison.OrdinalIgnoreCase))
            {
                return "A card that uses 'mentions' can't also carry mention entities in 'msteams.entities'.";
            }
        }
        return null;
    }

    private static string? CheckPlacements(HashSet<string> keys, string format, JsonElement message)
    {
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var placements = 0;
        foreach (var text in Texts(format, message))
        {
            var matches = Placement().Matches(text);
            if (AnyAtTag().Count(text) != 2 * matches.Count)
                return "The message has an <at> tag that isn't a mention placement. Write each mention " +
                       "exactly as <at>key</at>, with a key from 'mentions'.";

            placements += matches.Count;
            if (placements > MaxPlacements)
                return $"The message has more than {MaxPlacements} mention placements; at most {MaxPlacements} " +
                       "are allowed, a key placed twice counting twice.";

            foreach (var key in matches.Select(p => p.Groups[1].Value))
            {
                if (!keys.Contains(key))
                    return $"<at>{Truncate(key)}</at> doesn't name a key in 'mentions'.";
                placed.Add(key);
            }
        }

        var unplaced = keys.FirstOrDefault(k => !placed.Contains(k));
        return unplaced == null
            ? null
            : $"Mention '{unplaced}' is never placed in the message; add <at>{unplaced}</at> or remove it.";
    }

    /// <summary>The text a format carries: the message itself, or every string value in a card.</summary>
    private static IEnumerable<string> Texts(string format, JsonElement message)
    {
        if (format == "text")
        {
            if (message.ValueKind == JsonValueKind.String)
                yield return message.GetString() ?? string.Empty;
            yield break;
        }

        var pending = new Stack<JsonElement>();
        pending.Push(message);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    yield return element.GetString() ?? string.Empty;
                    break;
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject())
                        pending.Push(property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        pending.Push(item);
                    break;
            }
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxKeyLength ? value : value[..MaxKeyLength] + "…";
}
