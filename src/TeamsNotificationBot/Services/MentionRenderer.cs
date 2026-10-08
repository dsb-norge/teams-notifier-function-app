using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using TeamsNotificationBot.Models;

namespace TeamsNotificationBot.Services;

/// <summary>
/// Turns a message's <c>&lt;at&gt;key&lt;/at&gt;</c> placements into Teams mentions, once the queue
/// processor has the roster. A person in the roster is mentioned under the roster's display name,
/// so the name shown is always the person pinged; one who isn't is written as plain text (the
/// caller's <c>name</c>, else the <c>id</c>) and reported. A tag is mentioned as given (the bot
/// can't check tags without Graph), or written as plain text where tags aren't allowed.
/// </summary>
public static class MentionRenderer
{
    /// <param name="Message">The text, or the card JSON with its <c>msteams.entities</c> filled in.</param>
    /// <param name="Entities">For a text message, the mention entities the activity carries.</param>
    /// <param name="Unresolved">The <c>id</c> or <c>tag</c> of every mention written as plain text.</param>
    /// <param name="HasTagMentions">Whether any tag was mentioned.</param>
    public sealed record Rendered(
        string Message, IReadOnlyList<Mention> Entities, IReadOnlyList<string> Unresolved, bool HasTagMentions);

    /// <summary>What a key's placements become: <c>&lt;at&gt;name&lt;/at&gt;</c> with an entity, or plain text.</summary>
    private sealed record Display(string Text, Placed? Entity);

    private sealed record Placed(string Text, string Id, string Name, bool IsTag);

    /// <param name="findPerson">The roster member for a person's <c>id</c>, or null.</param>
    /// <param name="tagsAllowed">False outside channels, or after Teams rejected the tags.</param>
    /// <remarks>
    /// Teams pairs a message's <c>&lt;at&gt;</c> tags with its mention entities by position, so there
    /// is one entity per placement, in the order the placements appear (in a card, the order of its
    /// string values), however the <c>mentions</c> array is ordered and however often a key is placed.
    /// </remarks>
    public static Rendered Render(
        string format, string message, IReadOnlyList<MessageMention> mentions,
        Func<string, ChannelAccount?> findPerson, bool tagsAllowed)
    {
        var displays = new Dictionary<string, Display>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var mention in mentions)
        {
            if (mention.Tag != null)
            {
                displays[mention.Key] = tagsAllowed
                    ? Place(mention.Tag, mention.Name!, isTag: true)
                    : Unresolved(unresolved, mention.Tag, mention.Name!);
                continue;
            }

            var member = findPerson(mention.Id!);
            var name = DisplayName(member?.Name);
            displays[mention.Key] = member is { Id.Length: > 0 } && name.Length > 0
                ? Place(member.Id, name, isTag: false)
                : Unresolved(unresolved, mention.Id!, mention.Name ?? mention.Id!);
        }

        // Filled while the placements are replaced, so it follows their order.
        var placed = new List<Placed>();
        var rendered = format == "adaptive-card"
            ? RenderCard(message, displays, placed)
            : Replace(message, displays, placed);

        var hasTags = placed.Any(p => p.IsTag);
        var entities = format == "adaptive-card"
            ? []
            : placed.Select(p => new Mention { Text = p.Text, Mentioned = Account(p) }).ToList();
        return new Rendered(rendered, entities, unresolved, hasTags);
    }

    private static Display Place(string id, string name, bool isTag)
    {
        var text = $"<at>{name}</at>";
        return new Display(text, new Placed(text, id, name, isTag));
    }

    private static Display Unresolved(List<string> unresolved, string id, string plainText)
    {
        if (!unresolved.Contains(id, StringComparer.Ordinal))
            unresolved.Add(id);
        return new Display(plainText, null);
    }

    // A roster name is written between <at> and </at>: a '<' or '>' in it would break the tag.
    private static string DisplayName(string? name) =>
        name == null ? string.Empty : new string(name.Where(c => c is not ('<' or '>') && !char.IsControl(c)).ToArray()).Trim();

    private static ChannelAccount Account(Placed placed)
    {
        var account = new ChannelAccount { Id = placed.Id, Name = placed.Name };
        if (placed.IsTag)
            account.Properties["type"] = System.Text.Json.JsonSerializer.SerializeToElement("tag");
        return account;
    }

    private static string Replace(string text, Dictionary<string, Display> displays, List<Placed> placed) =>
        MentionRules.Placement().Replace(text, match =>
        {
            if (!displays.TryGetValue(match.Groups[1].Value, out var display))
                return match.Value;
            if (display.Entity != null)
                placed.Add(display.Entity);
            return display.Text;
        });

    private static string RenderCard(string cardJson, Dictionary<string, Display> displays, List<Placed> placed)
    {
        var card = JsonNode.Parse(cardJson)!.AsObject();
        ReplaceStrings(card, displays, placed);

        if (placed.Count > 0)
        {
            // Validation guarantees that msteams and msteams.entities are absent or of the right kind.
            if (card["msteams"] is not JsonObject msteams)
                card["msteams"] = msteams = new JsonObject();
            if (msteams["entities"] is not JsonArray entities)
                msteams["entities"] = entities = new JsonArray();

            foreach (var p in placed)
            {
                var mentioned = new JsonObject { ["id"] = p.Id, ["name"] = p.Name };
                if (p.IsTag)
                    mentioned["type"] = "tag";
                entities.Add(new JsonObject { ["type"] = "mention", ["text"] = p.Text, ["mentioned"] = mentioned });
            }
        }

        return card.ToJsonString();
    }

    // Depth-first in document order: properties as written, array items by index.
    private static void ReplaceStrings(JsonNode? node, Dictionary<string, Display> displays, List<Placed> placed)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var name in obj.Select(p => p.Key).ToList())
                {
                    if (StringValue(obj[name]) is { } text)
                        obj[name] = Replace(text, displays, placed);
                    else
                        ReplaceStrings(obj[name], displays, placed);
                }
                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (StringValue(array[i]) is { } text)
                        array[i] = Replace(text, displays, placed);
                    else
                        ReplaceStrings(array[i], displays, placed);
                }
                break;
        }
    }

    private static string? StringValue(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
